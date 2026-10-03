using System.Text.Json;
using WinShell.Core.Ssh;
using WinShell.Infrastructure.Windows.Profiles;

namespace WinShell.Infrastructure.Windows.Ssh;

public sealed class JsonKnownHostRepository : IKnownHostRepository
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private static readonly string[] FileNames = ["known-hosts.json", "known-hosts.lock"];
    private readonly string _directory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    /// <param name="configurationRoot">Application configuration root; the repository owns its ssh-known-hosts child.</param>
    public JsonKnownHostRepository(string configurationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        _directory = Path.Combine(Path.GetFullPath(configurationRoot), "ssh-known-hosts");
    }

    public Task<SshHostKey?> ReadAsync(SshEndpointIdentity endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return ExecuteLockedAsync(async token =>
        {
            var entries = await ReadEntriesAsync(token).ConfigureAwait(false);
            return entries.GetValueOrDefault(endpoint);
        }, cancellationToken);
    }

    public Task<bool> TrustIfAbsentOrSameAsync(SshEndpointIdentity endpoint, SshHostKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(key);
        return ExecuteLockedAsync(async token =>
        {
            var entries = await ReadEntriesAsync(token).ConfigureAwait(false);
            if (entries.TryGetValue(endpoint, out var previous)) return previous.Equals(key);
            if (entries.Count >= MaximumEntries) throw new InvalidDataException("可信主机数量超过 10,000 上限。");
            entries.Add(endpoint, key);
            await SaveAsync(entries, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private Task<TResult> ExecuteLockedAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acquisition.CancelAfter(TimeSpan.FromSeconds(5));
            var acquired = false;
            try
            {
                await _operationGate.WaitAsync(acquisition.Token).ConfigureAwait(false);
                acquired = true;
                acquisition.Token.ThrowIfCancellationRequested();
                // Reject ancestor junctions too, before ACL propagation can touch their targets.
                for (var directory = new DirectoryInfo(_directory); directory is not null; directory = directory.Parent)
                    if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("可信主机路径不能经过重解析点。");
                ProfileStorageAccess.EnsureCurrentUserAccess(_directory, FileNames);
                await using var fileLock = await AcquireFileLockAsync(acquisition.Token).ConfigureAwait(false);
                acquisition.CancelAfter(Timeout.InfiniteTimeSpan);
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var name in FileNames)
                    ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory, name));
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && acquisition.IsCancellationRequested)
            {
                throw new TimeoutException("等待可信主机文件锁超过 5 秒。", exception);
            }
            finally
            {
                if (acquired) _operationGate.Release();
            }
        }, cancellationToken);

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "known-hosts.lock");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ProfileStorageAccess.IsRegularFileOrMissing(path);
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Dictionary<SshEndpointIdentity, SshHostKey>> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        FileStream input;
        try
        {
            input = new FileStream(Path.Combine(_directory, "known-hosts.json"), FileMode.Open,
                FileAccess.Read, FileShare.Read, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException) { return []; }

        await using (input)
        {
            if (input.Length > MaximumFileBytes) throw new InvalidDataException("可信主机文件超过 4 MiB 上限。");
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("可信主机文件必须是对象。");
                foreach (var property in root.EnumerateObject())
                    if (property.NameEquals("schemaVersion") && property.Value.ValueKind == JsonValueKind.Number &&
                        (!property.Value.TryGetInt32(out var version) || version > 1))
                        throw new NotSupportedException("可信主机文件版本过新，拒绝读取或修改。");
                ValidateProperties(root, ["schemaVersion", "hosts"]);
                if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
                    !root.GetProperty("schemaVersion").TryGetInt32(out var schemaVersion) || schemaVersion != 1)
                    throw new InvalidDataException("可信主机版本无效。");
                var hosts = root.GetProperty("hosts");
                if (hosts.ValueKind != JsonValueKind.Array || hosts.GetArrayLength() > MaximumEntries)
                    throw new InvalidDataException("可信主机列表无效或超过数量上限。");
                Dictionary<SshEndpointIdentity, SshHostKey> entries = new(hosts.GetArrayLength());
                foreach (var host in hosts.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ValidateProperties(host, ["host", "port", "algorithm", "publicKeyBlob"]);
                    var hostname = ReadString(host, "host", 253);
                    var algorithm = ReadString(host, "algorithm", 128);
                    var encodedKey = ReadString(host, "publicKeyBlob", 21848);
                    if (host.GetProperty("port").ValueKind != JsonValueKind.Number || !host.GetProperty("port").TryGetInt32(out var port))
                        throw new InvalidDataException("可信主机端口无效。");
                    var blob = Convert.FromBase64String(encodedKey);
                    // Require the canonical base64 form, including padding; no whitespace or oversized decoded data.
                    if (Convert.ToBase64String(blob) != encodedKey) throw new InvalidDataException("主机公钥 Base64 无效。");
                    if (!entries.TryAdd(new(hostname, port), new(algorithm, blob)))
                        throw new InvalidDataException("可信主机身份重复。");
                }
                return entries;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException)
            {
                throw new InvalidDataException("可信主机文件包含无效数据。", exception);
            }
        }
    }

    private static string ReadString(JsonElement element, string name, int maximumLength)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("可信主机字段必须为字符串。");
        var text = value.GetString()!;
        if (text.Length > maximumLength) throw new InvalidDataException("可信主机字段超出长度上限。");
        return text;
    }

    private static void ValidateProperties(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("可信主机记录必须为对象。");
        HashSet<string> found = [];
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name)) throw new NotSupportedException("可信主机文件包含未知字段，拒绝丢弃。");
            if (!found.Add(property.Name)) throw new InvalidDataException("可信主机文件含有重复字段。");
        }
        if (found.Count != expected.Count) throw new InvalidDataException("可信主机文件缺少字段。");
    }

    private async Task SaveAsync(Dictionary<SshEndpointIdentity, SshHostKey> entries, CancellationToken cancellationToken)
    {
        var primaryPath = Path.Combine(_directory, "known-hosts.json");
        var temporaryPath = Path.Combine(_directory, $"known-hosts.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous))
            {
                ProfileStorageAccess.EnsureCurrentUserFileAccess(temporaryPath);
                using var writer = new Utf8JsonWriter(output);
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteStartArray("hosts");
                foreach (var (endpoint, key) in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteStartObject();
                    writer.WriteString("host", endpoint.Host);
                    writer.WriteNumber("port", endpoint.Port);
                    writer.WriteString("algorithm", key.Algorithm);
                    writer.WriteBase64String("publicKeyBlob", key.CopyPublicKeyBlob());
                    writer.WriteEndObject();
                    CheckOutputBudget(writer);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                CheckOutputBudget(writer);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (ProfileStorageAccess.IsRegularFileOrMissing(primaryPath)) File.Replace(temporaryPath, primaryPath, null);
            else File.Move(temporaryPath, primaryPath);
            ProfileStorageAccess.EnsureCurrentUserFileAccess(primaryPath);
        }
        finally { File.Delete(temporaryPath); }
    }

    private static void CheckOutputBudget(Utf8JsonWriter writer)
    {
        if (writer.BytesCommitted + writer.BytesPending > MaximumFileBytes)
            throw new InvalidDataException("可信主机序列化超过 4 MiB 上限。");
    }
}
