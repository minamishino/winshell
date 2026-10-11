using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using WinShell.Core.Credentials;

namespace WinShell.Mcp.Http;

/// <summary>Pure Windows DPAPI storage; link this file into WinUI without an ASP.NET reference.
/// A running host owns a startup snapshot. Stop and join it before rotating, then restart.
/// Each returned Task settles all owned file work. The caller must join admitted calls on shutdown;
/// this object has no background task or eagerly allocated OS wait handle.</summary>
public sealed class LocalMcpHttpTokenStore
{
    private static ReadOnlySpan<byte> Magic => "WSHT1"u8;
    private readonly string _directory;
    private readonly Action<string> _secureFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _admitted;
    public string TokenPath { get; }
    public LocalMcpHttpTokenStore(string configurationRoot) : this(configurationRoot, PrivateFile) { }
    internal LocalMcpHttpTokenStore(string configurationRoot, Action<string> secureFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRoot);
        _directory = Path.Combine(Path.GetFullPath(configurationRoot), "mcp-http");
        _secureFile = secureFile;
        TokenPath = Path.Combine(_directory, "token.dpapi");
    }

    public Task<CredentialSecret?> ReadAsync(CancellationToken cancellationToken) => ExecuteAsync(false, cancellationToken);
    public async Task<CredentialSecret> RotateAsync(CancellationToken cancellationToken) =>
        (await ExecuteAsync(true, cancellationToken).ConfigureAwait(false))!;

    /// <summary>One CLI host per configuration root. Acquire before reading its token snapshot;
    /// dispose only after the host joins. Missing token returns null without creating directories.
    /// A live lease rejects another host and RotateAsync with token_store_running.</summary>
    public async Task<IAsyncDisposable?> AcquireRunningLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _admitted) > 8) { Interlocked.Decrement(ref _admitted); throw new IOException("token_store_busy"); }
        var entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            RejectReparsePoints(_directory); RejectReparsePoints(TokenPath);
            if (!Directory.Exists(_directory) || !File.Exists(TokenPath)) return null;
            deadline.Token.ThrowIfCancellationRequested();
            return AcquireRunningFile();
        }
        finally { if (entered) _gate.Release(); Interlocked.Decrement(ref _admitted); }
    }

    private async Task<CredentialSecret?> ExecuteAsync(bool rotate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Increment(ref _admitted) > 8) { Interlocked.Decrement(ref _admitted); throw new IOException("token_store_busy"); }
        var entered = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await _gate.WaitAsync(deadline.Token).ConfigureAwait(false); entered = true;
            RejectReparsePoints(_directory);
            if (!Directory.Exists(_directory))
            {
                if (!rotate) return null;
                Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(_directory).Create(security);
            }
            RejectReparsePoints(_directory);
            await using var fileLock = await LockAsync(deadline.Token).ConfigureAwait(false);
            await using var rotationLease = rotate ? AcquireRunningFile() : null;
            using var current = await ReadCoreAsync(deadline.Token).ConfigureAwait(false);
            if (!rotate) return current is null ? null : new CredentialSecret(current.Bytes.Span);
            var token = RandomNumberGenerator.GetBytes(32);
            byte[]? protectedToken = null;
            var temporary = Path.Combine(_directory, "token-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                protectedToken = ProtectedData.Protect(token, null, DataProtectionScope.CurrentUser);
                if (protectedToken.Length > 4096 - Magic.Length) throw new InvalidDataException("token_store_invalid");
                await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    _secureFile(temporary);
                    await file.WriteAsync(Magic.ToArray(), deadline.Token).ConfigureAwait(false);
                    await file.WriteAsync(protectedToken, deadline.Token).ConfigureAwait(false);
                    await file.FlushAsync(deadline.Token).ConfigureAwait(false);
                }
                RejectReparsePoints(TokenPath);
                deadline.Token.ThrowIfCancellationRequested();
                File.Move(temporary, TokenPath, true);
                return new CredentialSecret(token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(token);
                if (protectedToken is not null) CryptographicOperations.ZeroMemory(protectedToken);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally { if (entered) _gate.Release(); Interlocked.Decrement(ref _admitted); }
    }

    private async Task<CredentialSecret?> ReadCoreAsync(CancellationToken token)
    {
        RejectReparsePoints(TokenPath);
        if (!File.Exists(TokenPath)) return null;
        await using var file = new FileStream(TokenPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is < 6 or > 4096) throw new InvalidDataException("token_store_invalid");
        var encrypted = new byte[(int)file.Length];
        byte[]? plaintext = null;
        try
        {
            await file.ReadExactlyAsync(encrypted, token).ConfigureAwait(false);
            if (await file.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0 || !encrypted.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new InvalidDataException("token_store_invalid");
            try { plaintext = ProtectedData.Unprotect(encrypted.AsSpan(Magic.Length).ToArray(), null, DataProtectionScope.CurrentUser); }
            catch (CryptographicException error) { throw new InvalidDataException("token_store_invalid", error); }
            if (plaintext.Length != 32) throw new InvalidDataException("token_store_invalid");
            return new CredentialSecret(plaintext);
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }
    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, "token.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested(); RejectReparsePoints(path);
            try
            {
                var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try { _secureFile(path); return file; }
                catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
            }
            catch (IOException) { await Task.Delay(25, token).ConfigureAwait(false); }
        }
    }
    private FileStream AcquireRunningFile()
    {
        var path = Path.Combine(_directory, "token.running.lock");
        RejectReparsePoints(path);
        FileStream file;
        try { file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        { throw new IOException("token_store_running", error); }
        try { _secureFile(path); return file; }
        catch { file.Dispose(); throw; }
    }
    private static SecurityIdentifier CurrentUser()
    { using var identity = WindowsIdentity.GetCurrent(); return identity.User ?? throw new UnauthorizedAccessException("token_store_denied"); }
    private static void PrivateFile(string path)
    {
        var security = new FileSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
    private static void RejectReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("token_store_invalid"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
