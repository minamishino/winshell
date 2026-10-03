using Renci.SshNet.Common;
using WinShell.Core.Ssh;
using SshConnectionException = WinShell.Core.Ssh.SshConnectionException;
namespace WinShell.Infrastructure.Ssh;

internal sealed class SshHostKeyPolicy(SshHostKey? trustedKey)
{
    internal SshHostKey? ObservedKey { get; private set; }
    internal SshConnectionException? Failure { get; private set; }
    internal void OnReceived(object? sender, HostKeyEventArgs args)
    {
        args.CanTrust = false;
        try
        {
            var receivedKey = new SshHostKey(args.HostKeyName, args.HostKey);
            ObservedKey = receivedKey;
            if (trustedKey is null) return;
            if (!trustedKey.Equals(receivedKey))
            {
                Failure = new SshConnectionException(SshFailureKind.HostKeyChanged, "SSH 主机公钥已改变；连接已拒绝。");
                return;
            }
            args.CanTrust = true;
        }
        catch (ArgumentException error)
        {
            Failure = new SshConnectionException(SshFailureKind.Protocol, "SSH 主机公钥无效或超过限制。", error);
        }
    }
}
