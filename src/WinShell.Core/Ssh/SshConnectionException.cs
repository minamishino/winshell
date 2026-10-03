namespace WinShell.Core.Ssh;

public enum SshFailureKind
{
    InvalidProfile,
    MissingCredential,
    CredentialAccess,
    Authentication,
    HostKeyChanged,
    Storage,
    Network,
    Permission,
    Protocol,
    Timeout,
    Capacity,
    PrivateKey
}

/// <summary>Classified connection failure. Cancellation remains OperationCanceledException.</summary>
public sealed class SshConnectionException(SshFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public SshFailureKind Kind { get; } = kind;
}
