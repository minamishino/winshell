using System.Security.Cryptography;

namespace WinShell.Core.Credentials;

public sealed class CredentialSecret : IDisposable
{
    private readonly byte[] _buffer;
    private bool _disposed;

    public CredentialSecret(ReadOnlySpan<byte> secret) => _buffer = secret.ToArray();

    /// <summary>Borrowed bytes; the owner must outlive use and must not be disposed concurrently.</summary>
    public ReadOnlyMemory<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _buffer;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_buffer);
        _disposed = true;
    }
}
