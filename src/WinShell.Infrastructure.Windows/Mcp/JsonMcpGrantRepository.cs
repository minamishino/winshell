using System.Text.Json;
using WinShell.Core.Mcp;
using WinShell.Infrastructure.Windows.Profiles;

namespace WinShell.Infrastructure.Windows.Mcp;

public sealed class JsonMcpGrantRepository : IMcpGrantRepository
{
    private const int MaximumBytes = 262144;
    private static readonly string[] FileNames = ["mcp-grants.json", "mcp-grants.lock"];
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public JsonMcpGrantRepository(string configurationRoot) => _directory = Path.Combine(Path.GetFullPath(configurationRoot), "mcp-permissions");

    public Task<IReadOnlyList<McpGrant>> ReadAsync(CancellationToken cancellationToken) => LockedAsync<IReadOnlyList<McpGrant>>(async token =>
    {
        var path = Path.Combine(_directory, FileNames[0]);
        if (!ProfileStorageAccess.IsRegularFileOrMissing(path)) return [];
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("授权文件超出字节上限。");
        var bytes = new byte[(int)input.Length];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            Properties(root, "schemaVersion", "grants");
            if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number || !root.GetProperty("schemaVersion").TryGetInt32(out var version) || version is not (1 or 2))
                throw new InvalidDataException("授权版本无效。");
            var rows = root.GetProperty("grants");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 64) throw new InvalidDataException("授权数量无效。");
            var grants = new List<McpGrant>(rows.GetArrayLength());
            foreach (var row in rows.EnumerateArray())
            {
                Properties(row, version == 1 ? ["profileId", "profileBinding", "hostKeyBinding", "listProfiles", "runScript"] : ["profileId", "profileBinding", "hostKeyBinding", "listProfiles", "runScript", "dockerRead", "dockerControl", "dockerExec"]);
                grants.Add(new(row.GetProperty("profileId").GetGuid(), row.GetProperty("profileBinding").GetString()!,
                    row.GetProperty("hostKeyBinding").GetString()!, row.GetProperty("listProfiles").GetBoolean(), row.GetProperty("runScript").GetBoolean(),
                    version == 2 && row.GetProperty("dockerRead").GetBoolean(), version == 2 && row.GetProperty("dockerControl").GetBoolean(), version == 2 && row.GetProperty("dockerExec").GetBoolean()));
            }
            Validate(grants);
            return grants;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { throw new InvalidDataException("授权文件无效。", error); }
    }, cancellationToken);

    public Task ReplaceAsync(IReadOnlyList<McpGrant> grants, CancellationToken cancellationToken)
    {
        // Copy bounded input before awaiting; callers cannot mutate a pending save.
        if (grants.Count > 64) return Task.FromException(new InvalidDataException("最多 64 项授权。"));
        var snapshot = grants.ToArray();
        return LockedAsync(async token =>
        {
            Validate(snapshot);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, grants = snapshot.Select(grant => new
            { profileId = grant.ProfileId, profileBinding = grant.ProfileBinding, hostKeyBinding = grant.HostKeyBinding, listProfiles = grant.ListProfiles, runScript = grant.RunScript, dockerRead = grant.DockerRead, dockerControl = grant.DockerControl, dockerExec = grant.DockerExec }) });
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("授权文件超出字节上限。");
            var target = Path.Combine(_directory, FileNames[0]);
            var temporary = Path.Combine(_directory, $"mcp-grants.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    ProfileStorageAccess.EnsureCurrentUserFileAccess(temporary);
                    await output.WriteAsync(bytes, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                    output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (ProfileStorageAccess.IsRegularFileOrMissing(target)) File.Replace(temporary, target, null);
                else File.Move(temporary, target);
                ProfileStorageAccess.EnsureCurrentUserFileAccess(target);
                return true;
            }
            finally { File.Delete(temporary); }
        }, cancellationToken);
    }
    private static void Validate(IReadOnlyList<McpGrant> grants)
    {
        if (grants.Count > 64) throw new InvalidDataException("最多 64 项授权。");
        var ids = new HashSet<Guid>();
        foreach (var grant in grants)
            if (grant is null || grant.ProfileId == Guid.Empty || !ids.Add(grant.ProfileId) || !Hash(grant.ProfileBinding) || !Hash(grant.HostKeyBinding))
                throw new InvalidDataException("授权记录无效或重复。");
    }
    private static bool Hash(string? text) => text is { Length: 64 } && text.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static void Properties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("授权记录必须是对象。");
        var remaining = names.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("授权字段未知或重复。");
        if (remaining.Count != 0) throw new InvalidDataException("授权字段缺失。");
    }
    private Task<T> LockedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token) => Task.Run(async () =>
    {
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(token);
        acquisition.CancelAfter(TimeSpan.FromSeconds(5));
        var acquired = false;
        try
        {
            await _gate.WaitAsync(acquisition.Token).ConfigureAwait(false);
            acquired = true;
            for (var directory = new DirectoryInfo(_directory); directory is not null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("授权路径不能经过重解析点。");
            ProfileStorageAccess.EnsureCurrentUserAccess(_directory, FileNames);
            await using var fileLock = await AcquireAsync(acquisition.Token).ConfigureAwait(false);
            acquisition.CancelAfter(Timeout.InfiniteTimeSpan);
            token.ThrowIfCancellationRequested();
            foreach (var name in FileNames) ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory, name));
            return await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && acquisition.IsCancellationRequested)
        { throw new TimeoutException("等待授权文件锁超过 5 秒。", error); }
        finally { if (acquired) _gate.Release(); }
    }, token);
    private async Task<FileStream> AcquireAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, FileNames[1]);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ProfileStorageAccess.IsRegularFileOrMissing(path);
                return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
}
