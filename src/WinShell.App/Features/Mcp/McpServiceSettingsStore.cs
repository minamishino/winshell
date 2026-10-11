using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace WinShell.App.Features.Mcp;

public sealed class McpServiceSettingsStore(string configurationRoot)
{
    private const int MaximumBytes = 4096;
    private readonly string _directory = Path.Combine(Path.GetFullPath(configurationRoot), "mcp-service");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _admitted;
    public Task<McpServiceSettings> ReadAsync(CancellationToken cancellationToken) =>
        LockedAsync(ReadCoreAsync, cancellationToken);
    public async Task SaveAsync(McpServiceSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings); settings.Validate();
        await LockedAsync(async token =>
        {
            await ReadCoreAsync(token).ConfigureAwait(false); // Preserve invalid evidence even on retry.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, transport = settings.Transport.ToString(), port = settings.Port, legacySse = settings.EnableLegacySse });
            var destination = Path.Combine(_directory, "options.json");
            var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    SecureFile(temporary); await output.WriteAsync(bytes, token).ConfigureAwait(false); await output.FlushAsync(token).ConfigureAwait(false);
                }
                CheckPath(destination); token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return settings;
        }, cancellationToken).ConfigureAwait(false);
    }
    private async Task<T> LockedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _admitted) > 8) { Interlocked.Decrement(ref _admitted); throw new IOException("settings_busy"); }
        var entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            CheckPath(_directory, directory: true);
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(CurrentUser());
            security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).Create(security);
            CheckPath(_directory, directory: true);
            var lockPath = Path.Combine(_directory, "options.lock");
            FileStream? fileLock = null;
            while (fileLock is null)
            {
                deadline.Token.ThrowIfCancellationRequested(); CheckPath(lockPath);
                try { fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { await Task.Delay(25, deadline.Token).ConfigureAwait(false); }
            }
            await using (fileLock.ConfigureAwait(false))
            {
                SecureFile(lockPath);
                return await operation(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("settings_lock_timeout", error); }
        finally { if (entered) _gate.Release(); Interlocked.Decrement(ref _admitted); }
    }
    private async Task<McpServiceSettings> ReadCoreAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, "options.json"); CheckPath(path);
        if (!File.Exists(path)) return new();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > MaximumBytes) throw new InvalidDataException("settings_invalid");
        var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (await input.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0) throw new InvalidDataException("settings_invalid");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2, AllowDuplicateProperties = false });
            var root = document.RootElement;
            string[] fields = ["schemaVersion", "transport", "port", "legacySse"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length || fields.Any(field => !root.TryGetProperty(field, out _)) ||
                !root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1) throw new InvalidDataException("settings_invalid");
            var name = root.GetProperty("transport").GetString();
            if (!Enum.TryParse<McpServiceTransport>(name, out var transport) || transport.ToString() != name) throw new InvalidDataException("settings_invalid");
            var settings = new McpServiceSettings(transport, root.GetProperty("port").GetInt32(), root.GetProperty("legacySse").GetBoolean());
            settings.Validate(); return settings;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or FormatException)
        { throw new InvalidDataException("settings_invalid", error); }
    }
    private static void CheckPath(string path, bool directory = false)
    {
        var target = Path.GetFullPath(path);
        for (var current = target; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (current == target && !directory && (attributes & FileAttributes.Directory) != 0))
                    throw new InvalidDataException("settings_invalid");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static SecurityIdentifier CurrentUser()
    { using var identity = WindowsIdentity.GetCurrent(); return identity.User ?? throw new UnauthorizedAccessException(); }
    private static void SecureFile(string path)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false); security.SetOwner(CurrentUser());
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
