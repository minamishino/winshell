using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinShell.Core.Profiles;

namespace WinShell.Infrastructure.Windows.Profiles;

public sealed class JsonConnectionProfileRepository : IConnectionProfileRepository
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumProfiles = 5000;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };
    private static readonly HashSet<string> ProfileProperties =
    [
        "id", "displayName", "protocol", "host", "port", "userName", "groupName",
        "authenticationMethod", "privateKeyPath", "credentialReference"
    ];
    private readonly string _configurationDirectory;
    // WaitHandle is never requested: this gate has only managed state and no native handle.
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public JsonConnectionProfileRepository(string configurationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
        _configurationDirectory = Path.GetFullPath(configurationDirectory);
    }

    public Task<ProfileReadResult> ReadAsync(CancellationToken cancellationToken) =>
        ExecuteLockedAsync(ReadCoreAsync, cancellationToken);

    public Task UpsertAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return ExecuteLockedAsync(async operationCancellationToken =>
        {
            ValidateProfile(profile);
            EnsureSerializationBudget(profile);
            var previous = await ReadCoreAsync(operationCancellationToken).ConfigureAwait(false);
            var profiles = previous.Profiles.ToList();
            var existingIndex = profiles.FindIndex(existing => existing.Id == profile.Id);
            if (existingIndex >= 0)
            {
                profiles[existingIndex] = profile;
            }
            else
            {
                if (profiles.Count >= MaximumProfiles)
                {
                    throw new InvalidDataException("连接配置数量不能超过 5,000。");
                }
                profiles.Add(profile);
            }
            await SaveAsync(profiles, previous.RecoveredFromBackup, operationCancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    public Task<bool> DeleteAsync(Guid profileId, CancellationToken cancellationToken) =>
        ExecuteLockedAsync(async operationCancellationToken =>
        {
            var previous = await ReadCoreAsync(operationCancellationToken).ConfigureAwait(false);
            var profiles = previous.Profiles.Where(profile => profile.Id != profileId).ToArray();
            if (profiles.Length == previous.Profiles.Count)
            {
                return false;
            }
            await SaveAsync(profiles, previous.RecoveredFromBackup, operationCancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    private Task<TResult> ExecuteLockedAsync<TResult>(Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var acquisitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        acquisitionCancellation.CancelAfter(LockTimeout);
        var gateAcquired = false;
        try
        {
            try
            {
                await _operationGate.WaitAsync(acquisitionCancellation.Token).ConfigureAwait(false);
                gateAcquired = true;
                acquisitionCancellation.Token.ThrowIfCancellationRequested();
                ProfileStorageAccess.EnsureCurrentUserAccess(_configurationDirectory);
                await using var fileLock = await AcquireFileLockAsync(acquisitionCancellation.Token).ConfigureAwait(false);
                // The five-second budget covers acquisition only; actual I/O uses the caller's token.
                acquisitionCancellation.CancelAfter(Timeout.InfiniteTimeSpan);
                cancellationToken.ThrowIfCancellationRequested();
                ProfileStorageAccess.EnsureConfigurationFileAccess(_configurationDirectory);
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && acquisitionCancellation.IsCancellationRequested)
            {
                throw new TimeoutException("等待配置文件锁超过 5 秒。", exception);
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _operationGate.Release();
            }
        }
    }, cancellationToken);

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = Path.Combine(_configurationDirectory, "profiles.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProfileStorageAccess.IsRegularFileOrMissing(lockPath);
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                // Retry only Windows sharing/lock violations; permissions and disk failures remain distinct.
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProfileReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        InvalidDataException? primaryFailure = null;
        try
        {
            var primary = await ReadFileAsync("profiles.json", cancellationToken).ConfigureAwait(false);
            if (primary is not null)
            {
                return new(primary, false);
            }
        }
        catch (InvalidDataException exception)
        {
            primaryFailure = exception;
        }

        try
        {
            var backup = await ReadFileAsync("profiles.backup.json", cancellationToken).ConfigureAwait(false);
            if (backup is not null)
            {
                return new(backup, true);
            }
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("主配置和备份均无法读取，请保留原文件并检查配置。", exception);
        }

        if (primaryFailure is not null)
        {
            throw new InvalidDataException("主配置已损坏且无有效备份，请保留原文件并检查配置。", primaryFailure);
        }
        return new(Array.Empty<ConnectionProfile>(), false);
    }

    private async Task<IReadOnlyList<ConnectionProfile>?> ReadFileAsync(string fileName, CancellationToken cancellationToken)
    {
        FileStream input;
        try
        {
            input = new FileStream(Path.Combine(_configurationDirectory, fileName), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        await using (input)
        {
            if (input.Length > MaximumFileBytes)
            {
                throw new InvalidDataException("配置文件超过 4 MiB 限额。");
            }
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("配置必须是 JSON 对象。");
                }
                // Inspect every version occurrence before current-schema validation. A duplicate
                // field or an overflowing version must never downgrade a future document to backup.
                foreach (var property in root.EnumerateObject())
                {
                    if (property.NameEquals("schemaVersion") && property.Value.ValueKind == JsonValueKind.Number &&
                        (!property.Value.TryGetInt32(out var candidateVersion) || candidateVersion > 1))
                    {
                        throw new NotSupportedException("配置版本超出当前支持范围，拒绝读取或修改。");
                    }
                }
                if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                    !schema.TryGetInt32(out var schemaVersion))
                {
                    throw new InvalidDataException("配置缺少有效的 schema 版本。");
                }
                if (schemaVersion != 1)
                {
                    throw new InvalidDataException("配置 schema 版本无效。");
                }
                ValidateProperties(root, ["schemaVersion", "profiles"]);
                if (!root.TryGetProperty("profiles", out var profilesElement) || profilesElement.ValueKind != JsonValueKind.Array ||
                    profilesElement.GetArrayLength() > MaximumProfiles)
                {
                    throw new InvalidDataException("配置列表无效或超过 5,000 个连接。");
                }

                List<ConnectionProfile> profiles = new(profilesElement.GetArrayLength());
                HashSet<Guid> identifiers = [];
                foreach (var element in profilesElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateProperties(element, ProfileProperties);
                    var profile = element.Deserialize<ConnectionProfile>(JsonOptions) ??
                        throw new InvalidDataException("连接配置不能为 null。");
                    if (!Enum.IsDefined(profile.Protocol) || !Enum.IsDefined(profile.AuthenticationMethod))
                    {
                        throw new NotSupportedException("配置包含当前版本不支持的协议或认证方式。");
                    }
                    ValidateProfile(profile);
                    if (!identifiers.Add(profile.Id))
                    {
                        throw new InvalidDataException("配置含有重复的连接标识。");
                    }
                    profiles.Add(profile);
                }
                return profiles.AsReadOnly();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("配置 JSON 无效。", exception);
            }
        }
    }

    private static void ValidateProperties(JsonElement element, HashSet<string> supportedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("连接配置必须是 JSON 对象。");
        }
        HashSet<string> names = [];
        foreach (var property in element.EnumerateObject())
        {
            if (!supportedProperties.Contains(property.Name))
            {
                throw new NotSupportedException("配置包含当前版本不支持的字段，拒绝丢弃数据。");
            }
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException("配置含有重复的 JSON 字段。");
            }
        }
        if (names.Count != supportedProperties.Count)
        {
            throw new InvalidDataException("配置缺少必需字段。");
        }
    }

    private static void ValidateProfile(ConnectionProfile profile)
    {
        if (ConnectionProfileValidator.Validate(profile).Count != 0)
        {
            throw new InvalidDataException("连接配置未通过元数据校验。");
        }
    }

    private static void EnsureSerializationBudget(ConnectionProfile profile)
    {
        // Check strings before the JSON serializer can allocate its escaping buffer.
        // UTF-8 input is capped; even worst-case JSON escaping has a bounded scratch size.
        long stringBytes = 0;
        foreach (var value in new[] { profile.DisplayName, profile.Host, profile.UserName, profile.GroupName,
                     profile.PrivateKeyPath, profile.CredentialReference })
        {
            if (value is not null)
            {
                if (value.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException("连接配置超过 4 MiB 限额。");
                }
                stringBytes += Encoding.UTF8.GetByteCount(value);
            }
        }
        if (stringBytes > MaximumFileBytes)
        {
            throw new InvalidDataException("连接配置超过 4 MiB 限额。");
        }
    }

    private async Task SaveAsync(IReadOnlyList<ConnectionProfile> profiles, bool recoveredFromBackup, CancellationToken cancellationToken)
    {
        var primaryPath = Path.Combine(_configurationDirectory, "profiles.json");
        var temporaryPath = Path.Combine(_configurationDirectory, $"profiles.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 16 * 1024, FileOptions.Asynchronous))
            {
                ProfileStorageAccess.EnsureCurrentUserFileAccess(temporaryPath);
                using var boundedOutput = new BoundedWriteStream(output, MaximumFileBytes);
                using var writer = new Utf8JsonWriter(boundedOutput);
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("profiles");
                foreach (var profile in profiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureSerializationBudget(profile);
                    JsonSerializer.Serialize(writer, profile, JsonOptions);
                    // Flush each bounded profile, preventing document-sized serializer accumulation.
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(primaryPath))
            {
                File.Replace(temporaryPath, primaryPath,
                    recoveredFromBackup ? null : Path.Combine(_configurationDirectory, "profiles.backup.json"));
            }
            else
            {
                File.Move(temporaryPath, primaryPath);
            }
            // File.Replace can preserve destination security. Enforce the same canonical policy
            // on both committed paths rather than relying on inheritance or replacement details.
            ProfileStorageAccess.EnsureCurrentUserFileAccess(primaryPath);
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_configurationDirectory, "profiles.backup.json"));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed class BoundedWriteStream(Stream destination, long maximumBytes) : Stream
    {
        private long _writtenBytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _writtenBytes;
        public override long Position { get => _writtenBytes; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => destination.FlushAsync(cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Reserve(buffer.Length);
            destination.Write(buffer);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reserve(buffer.Length);
            await destination.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private void Reserve(int byteCount)
        {
            if (byteCount > maximumBytes - _writtenBytes)
            {
                throw new InvalidDataException("序列化配置超过 4 MiB 限额。");
            }
            _writtenBytes += byteCount;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
