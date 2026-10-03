namespace WinShell.Core.Ssh;

public abstract class SshConnectResult
{
    private SshConnectResult() { }

    public sealed class Connected : SshConnectResult
    {
        public Connected(ISshSession session, SshHostKey key)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(key);
            Session = session;
            Key = key;
        }
        public ISshSession Session { get; }
        public SshHostKey Key { get; }
    }

    public sealed class UnknownHostKey : SshConnectResult
    {
        public UnknownHostKey(SshEndpointIdentity endpoint, SshHostKey key)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(key);
            Endpoint = endpoint;
            Key = key;
        }
        public SshEndpointIdentity Endpoint { get; }
        public SshHostKey Key { get; }
    }
}
