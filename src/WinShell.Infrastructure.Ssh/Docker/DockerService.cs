using WinShell.Core.Docker;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.Infrastructure.Ssh.Docker;

public sealed class DockerService(ISshSessionFactory factory, IConnectionProfileRepository profiles) : IDockerService
{
    private readonly object _gate = new();
    private int _operations;
    public Task<DockerResult> ListContainersAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        // A shell request can emit login startup text before executing our script. Frame only
        // this command's stdout; the preamble still consumes the existing capture byte budget.
        var marker = "WINSHELL_DOCKER_LIST_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var script = "printf '\\n%s\\n' '" + marker + "'\n" + DockerScriptBuilder.List();
        DockerScriptBuilder.ValidateText(script, 8192);
        return RunAsync(profile, script, false, marker, cancellationToken);
    }
    public Task<DockerResult> GetContainerLogsAsync(ConnectionProfile profile, string containerId, CancellationToken cancellationToken) => RunAsync(profile, DockerScriptBuilder.Logs(containerId), false, null, cancellationToken);
    public Task<DockerResult> ChangeContainerStateAsync(ConnectionProfile profile, string containerId, DockerContainerAction action, CancellationToken cancellationToken) => RunAsync(profile, DockerScriptBuilder.Change(containerId, action), true, null, cancellationToken);
    public Task<DockerResult> RunContainerCommandAsync(ConnectionProfile profile, string containerId, string command, CancellationToken cancellationToken) => RunAsync(profile, DockerScriptBuilder.Command(containerId, command), true, null, cancellationToken);

    private async Task<DockerResult> RunAsync(ConnectionProfile profile, string script, bool mutation, string? listMarker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.Protocol != ConnectionProtocol.Ssh) throw new SshConnectionException(SshFailureKind.InvalidProfile, "Docker 只支持 SSH 配置。");
        lock (_gate)
        {
            if (_operations == 4) throw new DockerException(DockerFailureKind.Capacity, "最多 4 个 Docker 操作，包括正在关闭的操作。");
            _operations++;
        }
        ISshSession? session = null;
        Exception? failure = null;
        var dispatched = false;
        try
        {
            var current = (await profiles.ReadAsync(cancellationToken).ConfigureAwait(false)).Profiles.SingleOrDefault(item => item.Id == profile.Id);
            if (current != profile) throw new SshConnectionException(SshFailureKind.InvalidProfile, "连接配置已更改或删除，请重新选择并确认目标。");
            var connection = await factory.ConnectAsync(current, cancellationToken).ConfigureAwait(false);
            if (connection is SshConnectResult.UnknownHostKey challenge) return new([], "", "", null, challenge);
            session = ((SshConnectResult.Connected)connection).Session;
            if (session is not ISshScriptSession scriptSession) throw new DockerException(DockerFailureKind.Unsupported, "SSH 会话不支持一次性脚本。");
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            var result = await scriptSession.RunScriptAsync(script, cancellationToken).ConfigureAwait(false);
            if (!result.RemoteEofReceived || result.ExitStatus is null || result.ExitSignal is not null)
                throw new DockerException(mutation ? DockerFailureKind.Unconfirmed : DockerFailureKind.Unsupported,
                    mutation ? "结果未确认，请刷新；不会自动重试操作。" : "未收到完整 Docker 退出状态。");
            DockerScriptBuilder.ValidateText(result.StandardOutput, 32768);
            DockerScriptBuilder.ValidateText(result.StandardError, 16384);
            var output = listMarker is null ? result.StandardOutput : ReadListOutput(result.StandardOutput, listMarker, result.ExitStatus == 0);
            return new(listMarker is not null && result.ExitStatus == 0 ? DockerMetadataReader.Read(output) : [], output, result.StandardError, result.ExitStatus);
        }
        catch (Exception exception)
        {
            failure = mutation && dispatched && exception is not DockerException { Kind: DockerFailureKind.Unconfirmed }
                ? new DockerException(DockerFailureKind.Unconfirmed, "结果未确认，请刷新；不会自动重试操作。", exception) : exception;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            try
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanup) when (failure is not null) { failure.Data["DockerCleanupFailure"] = cleanup; }
            finally { lock (_gate) _operations--; }
        }
    }

    private static string ReadListOutput(string output, string marker, bool succeeded)
    {
        var delimiter = "\n" + marker + "\n";
        var start = output.IndexOf(delimiter, StringComparison.Ordinal);
        if (start < 0 || output.IndexOf(delimiter, start + 1, StringComparison.Ordinal) >= 0)
        {
            if (succeeded) throw new DockerException(DockerFailureKind.InvalidMetadata, "Docker 列表输出起始标记缺失或重复，请检查 SSH 启动脚本；未显示不完整结果。");
            return output;
        }
        return output[(start + delimiter.Length)..];
    }
}
