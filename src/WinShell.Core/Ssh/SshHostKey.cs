using System.Security.Cryptography;

namespace WinShell.Core.Ssh;

public sealed class SshHostKey : IEquatable<SshHostKey>
{
    private readonly byte[] _publicKeyBlob;

    public SshHostKey(string algorithm, ReadOnlySpan<byte> publicKeyBlob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithm);
        if (algorithm.Length > 128 || algorithm.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("主机公钥类型必须是最多 128 字符的可打印 ASCII 名称。", nameof(algorithm));
        }
        if (publicKeyBlob.Length is 0 or > 16384)
        {
            throw new ArgumentException("主机公钥长度必须介于 1 和 16 KiB。", nameof(publicKeyBlob));
        }
        Algorithm = algorithm switch
        {
            "rsa-sha2-256" or "rsa-sha2-512" => "ssh-rsa",
            "rsa-sha2-256-cert-v01@openssh.com" or "rsa-sha2-512-cert-v01@openssh.com" => "ssh-rsa-cert-v01@openssh.com",
            _ => algorithm
        };
        _publicKeyBlob = publicKeyBlob.ToArray();
        Fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(_publicKeyBlob)).TrimEnd('=');
    }

    /// <summary>Canonical public-key type; this does not describe the negotiated signature algorithm.</summary>
    public string Algorithm { get; }
    public string Fingerprint { get; }
    public byte[] CopyPublicKeyBlob() => (byte[])_publicKeyBlob.Clone();
    public bool Equals(SshHostKey? other) => other is not null && Algorithm == other.Algorithm &&
        _publicKeyBlob.AsSpan().SequenceEqual(other._publicKeyBlob);
    public override bool Equals(object? obj) => obj is SshHostKey other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Algorithm, Fingerprint);
}
