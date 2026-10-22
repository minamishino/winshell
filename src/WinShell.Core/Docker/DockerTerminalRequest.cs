using WinShell.Core.Profiles;

namespace WinShell.Core.Docker;

public enum DockerTerminalShell { Sh, Bash }

/// <summary>A captured target; subsequent page selection cannot change its profile, container or shell.</summary>
public sealed record DockerTerminalRequest
{
    public ConnectionProfile Profile { get; }
    public string ContainerId { get; }
    public DockerTerminalShell Shell { get; }
    public DockerTerminalRequest(ConnectionProfile profile, string containerId, DockerTerminalShell shell)
    {
        if (profile is null || profile.Protocol != ConnectionProtocol.Ssh)
            throw new DockerException(DockerFailureKind.InvalidInput, "容器终端必须使用保存的 SSH 配置。");
        if (containerId is null || containerId.Length != 64 || containerId.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DockerException(DockerFailureKind.InvalidInput, "容器标识必须为完整的 64 位小写十六进制 ID。");
        if (shell is not (DockerTerminalShell.Sh or DockerTerminalShell.Bash))
            throw new DockerException(DockerFailureKind.InvalidInput, "请选择 /bin/sh 或 /bin/bash。");
        Profile = profile;
        ContainerId = containerId;
        Shell = shell;
    }
}
