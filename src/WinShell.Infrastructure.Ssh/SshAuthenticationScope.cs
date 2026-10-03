using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using WinShell.Core.Credentials;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using AuthenticationMethod = Renci.SshNet.AuthenticationMethod;

namespace WinShell.Infrastructure.Ssh;

internal sealed partial class SshAuthenticationScope : IDisposable
{
    private readonly byte[]? _password;
    private readonly PrivateKeyFile? _privateKey;
    internal AuthenticationMethod Method { get; }

    private SshAuthenticationScope(AuthenticationMethod method, byte[]? password = null, PrivateKeyFile? privateKey = null)
    {
        Method = method;
        _password = password;
        _privateKey = privateKey;
    }

    internal static SshAuthenticationScope CreateProbe(string userName) => new(new NoneAuthenticationMethod(userName));

    internal static async Task<SshAuthenticationScope> CreateAsync(ConnectionProfile profile, ICredentialStore credentials, CancellationToken cancellationToken)
    {
        CredentialSecret? secret;
        try { secret = await credentials.ReadAsync(profile.Id, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new SshConnectionException(SshFailureKind.CredentialAccess, "无法读取此连接的凭据。", error); }
        using (secret)
        {
            if (profile.AuthenticationMethod == Core.Profiles.AuthenticationMethod.Password)
            {
                if (secret is null) throw new SshConnectionException(SshFailureKind.MissingCredential, "此连接尚未保存密码。");
                var password = secret.Bytes.ToArray();
                try { return new SshAuthenticationScope(new PasswordAuthenticationMethod(profile.UserName, password), password); }
                catch { CryptographicOperations.ZeroMemory(password); throw; }
            }

            byte[]? keyBytes = null;
            PrivateKeyFile? privateKey = null;
            try
            {
                keyBytes = await ReadKeyAsync(profile.PrivateKeyPath!, cancellationToken).ConfigureAwait(false);
                // Header and whole-document policy runs BEFORE the general-purpose library parser.
                ValidatePem(keyBytes);
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(keyBytes, writable: false);
                privateKey = new PrivateKeyFile(stream, secret is null ? null : Encoding.UTF8.GetString(secret.Bytes.Span));
                if (privateKey.Key.KeyLength > 8192)
                    throw new SshConnectionException(SshFailureKind.PrivateKey, "RSA 私钥最多支持 8192 位。");
                cancellationToken.ThrowIfCancellationRequested();
                var scope = new SshAuthenticationScope(new PrivateKeyAuthenticationMethod(profile.UserName, privateKey), privateKey: privateKey);
                privateKey = null;
                return scope;
            }
            catch (OperationCanceledException) { throw; }
            catch (SshConnectionException) { throw; }
            catch (UnauthorizedAccessException error) { throw new SshConnectionException(SshFailureKind.Permission, "没有读取 SSH 私钥的权限。", error); }
            catch (Exception error) { throw new SshConnectionException(SshFailureKind.PrivateKey, "无法读取 RSA 私钥；请检查文件格式和口令。", error); }
            finally
            {
                privateKey?.Dispose();
                if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes);
            }
        }
    }

    private static async Task<byte[]> ReadKeyAsync(string path, CancellationToken cancellationToken)
    {
        const int maximumBytes = 1024 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximumBytes) throw new SshConnectionException(SshFailureKind.PrivateKey, "SSH 私钥文件超过 1 MiB 限制。");
        var bytes = new byte[checked((int)stream.Length)];
        try
        {
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var sentinel = new byte[1];
            if (await stream.ReadAsync(sentinel, cancellationToken).ConfigureAwait(false) != 0)
                throw new SshConnectionException(SshFailureKind.PrivateKey, "SSH 私钥文件在读取时发生变化。");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    internal static void ValidatePem(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 16384 || bytes.ContainsAnyInRange((byte)128, byte.MaxValue) || !RsaPemPattern().IsMatch(Encoding.ASCII.GetString(bytes)))
            throw new SshConnectionException(SshFailureKind.PrivateKey,
                "首期仅支持不超过 16 KiB 的传统 RSA PEM（无口令或 AES-256-CBC 加密）；不支持 OpenSSH、PuTTY、PKCS8 或其他算法。");
    }

    [GeneratedRegex("\\A-----BEGIN RSA PRIVATE KEY-----\\r?\\n(?:Proc-Type: 4,ENCRYPTED\\r?\\nDEK-Info: AES-256-CBC,[A-Fa-f0-9]{32}\\r?\\n\\r?\\n)?(?:[A-Za-z0-9+/=]{1,80}\\r?\\n)+-----END RSA PRIVATE KEY-----(?:\\r?\\n)?\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RsaPemPattern();

    public void Dispose()
    {
        try { (Method as IDisposable)?.Dispose(); }
        finally
        {
            _privateKey?.Dispose();
            if (_password is not null) CryptographicOperations.ZeroMemory(_password);
        }
    }
}
