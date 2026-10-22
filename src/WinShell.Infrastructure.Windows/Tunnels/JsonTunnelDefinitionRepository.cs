using System.Text.Json;
using System.Text.Json.Serialization;
using WinShell.Core.Tunnels;
using WinShell.Infrastructure.Windows.Profiles;
namespace WinShell.Infrastructure.Windows.Tunnels;

public sealed class JsonTunnelDefinitionRepository(string configurationDirectory) : ITunnelDefinitionRepository
{
    private const int MaximumBytes = 256 * 1024;
    private readonly string _directory = Path.GetFullPath(configurationDirectory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 8, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
    private sealed record Document(int SchemaVersion, TunnelDefinition[] Definitions);
    public Task<IReadOnlyList<TunnelDefinition>> ReadAsync(CancellationToken cancellationToken) => LockedAsync(ReadCoreAsync, cancellationToken);
    public Task ReplaceAsync(IReadOnlyList<TunnelDefinition> definitions, CancellationToken cancellationToken)
    {
        Validate(definitions); var snapshot = definitions.ToArray();
        return LockedAsync(async token => {
            await ReadCoreAsync(token).ConfigureAwait(false); // Refuse unknown/corrupt existing formats instead of silently discarding them.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, snapshot), Options);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("隧道配置超过 256 KiB。");
            var destination = Path.Combine(_directory, "tunnels.json");
            var temporary = Path.Combine(_directory, $"tunnels.{Guid.NewGuid():N}.tmp");
            try {
                await using(var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous)) {
                    ProfileStorageAccess.EnsureCurrentUserFileAccess(temporary);
                    await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (ProfileStorageAccess.IsRegularFileOrMissing(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
                ProfileStorageAccess.EnsureCurrentUserFileAccess(destination);
            } finally { File.Delete(temporary); }
            return true;
        }, cancellationToken);
    }
    private Task<T> LockedAsync<T>(Func<CancellationToken,Task<T>> action, CancellationToken cancellationToken) => Task.Run(async () => {
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); acquisition.CancelAfter(TimeSpan.FromSeconds(5)); var entered = false;
        try {
            await _gate.WaitAsync(acquisition.Token).ConfigureAwait(false); entered = true;
            ProfileStorageAccess.EnsureCurrentUserAccess(_directory, ["tunnels.json", "tunnels.lock"]);
            await using var fileLock = await ProfileStorageAccess.AcquireFileLockAsync(Path.Combine(_directory,"tunnels.lock"),acquisition.Token).ConfigureAwait(false);
            acquisition.CancelAfter(Timeout.InfiniteTimeSpan); cancellationToken.ThrowIfCancellationRequested();
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory,"tunnels.json"));
            ProfileStorageAccess.EnsureCurrentUserFileAccess(Path.Combine(_directory,"tunnels.lock"));
            return await action(cancellationToken).ConfigureAwait(false);
        } catch(OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && acquisition.IsCancellationRequested) { throw new TimeoutException("等待隧道配置锁超过 5 秒。",error); }
        finally { if(entered) _gate.Release(); }
    }, cancellationToken);
    private async Task<IReadOnlyList<TunnelDefinition>> ReadCoreAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory,"tunnels.json"); if(!ProfileStorageAccess.IsRegularFileOrMissing(path)) return [];
        await using var input = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,16384,FileOptions.Asynchronous);
        if(input.Length>MaximumBytes) throw new InvalidDataException("隧道配置超过 256 KiB。");
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes,token).ConfigureAwait(false);
        try {
            using var parsed = JsonDocument.Parse(bytes,new JsonDocumentOptions {MaxDepth=8}); CheckDuplicates(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object || !parsed.RootElement.TryGetProperty("definitions", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 64)
                throw new InvalidDataException("最多保存 64 个隧道定义。");
            var document = JsonSerializer.Deserialize<Document>(bytes,Options) ?? throw new InvalidDataException("隧道配置无效。");
            if(document.SchemaVersion!=1) throw new NotSupportedException("不支持此隧道配置版本。");
            Validate(document.Definitions); return Array.AsReadOnly(document.Definitions);
        } catch(JsonException error) { throw new InvalidDataException("隧道配置 JSON 无效。",error); }
    }
    private static void Validate(IReadOnlyList<TunnelDefinition> definitions)
    {
        if(definitions is null || definitions.Count>64) throw new InvalidDataException("最多保存 64 个隧道定义。");
        HashSet<Guid> ids=[];
        foreach(var definition in definitions) {
            try { TunnelDefinitionValidator.Validate(definition); } catch(ArgumentException error) { throw new InvalidDataException("隧道定义无效。",error); }
            if(!ids.Add(definition.Id)) throw new InvalidDataException("隧道标识重复。");
        }
    }
    private static void CheckDuplicates(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = [];
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new InvalidDataException("JSON 字段重复。");
                    CheckDuplicates(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray()) CheckDuplicates(value);
                break;
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            default:
                break;
        }
    }
}
