namespace WinShell.Core.Files;

public enum RemoteFileFailureKind
{
    Authentication, Certificate, Network, Permission, Protocol, Timeout
}
public sealed class RemoteFileException(RemoteFileFailureKind kind, string message, Exception? innerException = null) : IOException(message, innerException)
{
    public RemoteFileFailureKind Kind { get; } = kind;
}
