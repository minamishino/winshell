using System.Net;

namespace WinShell.Core.Ssh;

public sealed record SshEndpointIdentity
{
    public SshEndpointIdentity(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (host.Length > 253 || host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("SSH 主机地址无效。", nameof(host));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Host = IPAddress.TryParse(host, out var address) ? address.ToString() : host.ToLowerInvariant();
        Port = port;
    }
    public string Host { get; }
    public int Port { get; }
}
