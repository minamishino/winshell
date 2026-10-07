using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.Core.Docker;

public enum DockerContainerAction { Start, Stop, Restart }
public sealed record DockerContainerSummary(string Id, string Names, string Image, string State, string Status, string Ports);
public sealed record DockerResult(IReadOnlyList<DockerContainerSummary> Containers, string StandardOutput,
    string StandardError, int? ExitStatus, SshConnectResult.UnknownHostKey? HostKeyChallenge = null);
public enum DockerFailureKind { InvalidInput, InvalidMetadata, Capacity, Unsupported, Unconfirmed }
public sealed class DockerException(DockerFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public DockerFailureKind Kind { get; } = kind;
}

/// <summary>Each call rereads the saved profile and rejects a changed confirmation snapshot.</summary>
public interface IDockerService
{
    Task<DockerResult> ListContainersAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<DockerResult> GetContainerLogsAsync(ConnectionProfile profile, string containerId, CancellationToken cancellationToken);
    Task<DockerResult> ChangeContainerStateAsync(ConnectionProfile profile, string containerId, DockerContainerAction action, CancellationToken cancellationToken);
    Task<DockerResult> RunContainerCommandAsync(ConnectionProfile profile, string containerId, string command, CancellationToken cancellationToken);
}
