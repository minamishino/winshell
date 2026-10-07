using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Docker;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Docker;

public sealed class DockerViewModel(IDockerService service, IConnectionProfileRepository profiles, IKnownHostRepository knownHosts,
    Func<string, string, CancellationToken, Task<bool>> confirm) : ObservableObject, IAsyncDisposable
{
    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public ObservableCollection<DockerContainerSummary> Containers { get; } = [];
    private ConnectionProfile? _selectedProfile;
    private DockerContainerSummary? _selectedContainer;
    private string _commandText = "", _output = "", _status = "选择保存的 SSH 配置并验证 Docker。";
    private bool _isBusy, _disposed;
    private int _generation;
    private CancellationTokenSource? _operationCancellation;
    private Task _pending = Task.CompletedTask;
    private DockerTerminalShell _selectedShell;
    private bool _terminalCapacityAvailable = true;
    public IReadOnlyList<DockerTerminalShell> TerminalShells { get; } = [DockerTerminalShell.Sh, DockerTerminalShell.Bash];
    public DockerTerminalShell SelectedShell
    {
        get => _selectedShell;
        set { if (SetProperty(ref _selectedShell, value)) InvalidateSelection(); }
    }
    public bool TerminalCapacityAvailable
    {
        get => _terminalCapacityAvailable;
        set { if (SetProperty(ref _terminalCapacityAvailable, value)) OnPropertyChanged(nameof(CanOpenTerminal)); }
    }
    public bool CanOpenTerminal => CanAct && TerminalCapacityAvailable && SelectedProfile is not null && SelectedContainer is not null && Containers.Any(item => ReferenceEquals(item, SelectedContainer));
    public ConnectionProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value)) return;
            InvalidateSelection(); Containers.Clear(); SelectedContainer = null;
        }
    }
    public DockerContainerSummary? SelectedContainer
    {
        get => _selectedContainer;
        set { if (SetProperty(ref _selectedContainer, value)) InvalidateSelection(); }
    }
    private void InvalidateSelection()
    {
        _generation++; Cancel(); Output = ""; Status = "目标已变更，请验证或刷新。"; OnPropertyChanged(nameof(CanOpenTerminal));
    }
    public string CommandText { get => _commandText; set => SetProperty(ref _commandText, value); }
    public string Output { get => _output; private set => SetProperty(ref _output, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanAct)); OnPropertyChanged(nameof(CanOpenTerminal)); } }
    }
    public bool CanAct => !IsBusy && !_disposed;
    public Task LoadProfilesAsync() => OwnAsync(async (generation, token) =>
    {
        var result = await profiles.ReadAsync(token);
        if (!Current(generation)) return;
        var selected = SelectedProfile;
        Profiles.Clear();
        foreach (var profile in result.Profiles.Where(item => item.Protocol == ConnectionProtocol.Ssh)) Profiles.Add(profile);
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == selected?.Id) ?? Profiles.FirstOrDefault();
    });
    public Task RefreshAsync()
    {
        var profile = SelectedProfile;
        return OwnAsync(async (generation, token) =>
        {
            if (profile is null) { Status = "请选择 SSH 配置。"; return; }
            var result = await service.ListContainersAsync(profile, token);
            if (!Current(generation)) return;
            if (result.HostKeyChallenge is { } challenge)
            {
                var accepted = await confirm("确认 SSH 主机公钥", $"{profile.DisplayName}\n{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n{challenge.Key.Algorithm}\n{challenge.Key.Fingerprint}\n请通过可信渠道核对指纹。", token);
                if (!accepted || !Current(generation)) { if (Current(generation)) Status = "未信任主机公钥。"; return; }
                if (!await knownHosts.TrustIfAbsentOrSameAsync(challenge.Endpoint, challenge.Key, token))
                    throw new SshConnectionException(SshFailureKind.HostKeyChanged, "可信公钥已改变，拒绝连接。");
                if (!Current(generation)) return;
                result = await service.ListContainersAsync(profile, token);
                if (!Current(generation)) return;
            }
            if (!ShowResult(result)) return;
            // Update the selected row without canceling this completed list operation.
            var selectedId = _selectedContainer?.Id;
            Containers.Clear();
            foreach (var container in result.Containers) Containers.Add(container);
            _selectedContainer = Containers.FirstOrDefault(item => item.Id == selectedId);
            OnPropertyChanged(nameof(SelectedContainer));
            Status = $"已验证 Docker；显示最近 {Containers.Count} 个容器（最多 32 个）。每次操作重新连接。";
        });
    }
    public Task LogsAsync() => WithContainerAsync(false, "", (profile, container, token) => service.GetContainerLogsAsync(profile, container.Id, token));
    public Task OpenTerminalAsync(Func<DockerTerminalRequest, CancellationToken, Task<bool>> dispatch)
    {
        var profile = SelectedProfile; var container = SelectedContainer; var shell = SelectedShell;
        return OwnAsync(async (generation, token) =>
        {
            if (profile is null || container is null || !Containers.Any(item => ReferenceEquals(item, container))) { Status = "请刷新并选择容器。"; return; }
            if (!TerminalCapacityAvailable) { Status = "终端名额已满，请先关闭一个终端并等待资源释放。"; return; }
            var request = new DockerTerminalRequest(profile, container.Id, shell);
            if (!Current(generation)) return;
            var started = await dispatch(request, token);
            if (Current(generation)) Status = started ? "容器终端已打开。关闭连接不保证容器内任务已停止。" : "终端未能启动，请查看终端标签中的诊断。";
        });
    }
    public Task CancelPendingAsync() { Cancel(); return _pending; }
    public Task ChangeAsync(DockerContainerAction action) => WithContainerAsync(action != DockerContainerAction.Start,
        action == DockerContainerAction.Stop ? "停止容器" : "重启容器", (profile, container, token) => service.ChangeContainerStateAsync(profile, container.Id, action, token));
    public Task RunCommandAsync()
    {
        var command = CommandText;
        return WithContainerAsync(true, "执行一次性容器命令", (profile, container, token) => service.RunContainerCommandAsync(profile, container.Id, command, token));
    }
    private Task WithContainerAsync(bool needsConfirmation, string title, Func<ConnectionProfile, DockerContainerSummary, CancellationToken, Task<DockerResult>> execute)
    {
        var profile = SelectedProfile; var container = SelectedContainer;
        return OwnAsync(async (generation, token) =>
        {
            if (profile is null || container is null || !Containers.Contains(container)) { Status = "请刷新并选择容器。"; return; }
            if (needsConfirmation && !await confirm(title, $"服务器：{profile.DisplayName} ({profile.Host}:{profile.Port})\n容器：{container.Names}\n完整 ID：{container.Id}\n此操作可能中断工作或修改容器数据。", token)) return;
            if (!Current(generation)) return;
            var result = await execute(profile, container, token);
            if (Current(generation) && ShowResult(result)) Status = "操作已完成，请刷新查看容器当前状态。";
        });
    }
    private bool ShowResult(DockerResult result)
    {
        if (result.HostKeyChallenge is not null) { Status = "主机公钥尚未信任，请先验证并刷新 Docker 列表。操作未执行。"; return false; }
        Output = result.StandardOutput + (result.StandardError.Length == 0 ? "" : "\n标准错误：\n" + result.StandardError);
        if (result.ExitStatus != 0) { Status = $"Docker 返回退出码 {result.ExitStatus}；请检查输出、CLI 安装及 Unix socket 权限。"; return false; }
        return true;
    }
    private Task OwnAsync(Func<int, CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return Task.CompletedTask;
        IsBusy = true;
        var cancellation = new CancellationTokenSource(); _operationCancellation = cancellation;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _pending = completion.Task;
        var generation = _generation;
        _ = ExecuteAsync();
        return completion.Task;
        async Task ExecuteAsync()
        {
            try { await action(generation, cancellation.Token); }
            catch (OperationCanceledException) { if (Current(generation)) Status = "操作已取消。"; }
            catch (Exception exception)
            {
                if (Current(generation)) Status = exception switch
                {
                    DockerException docker => docker.Message,
                    SshConnectionException ssh => $"SSH {ssh.Kind}：{ssh.Message}",
                    _ => "Docker 操作失败，请检查配置存储与运行环境。"
                };
            }
            finally
            {
                _operationCancellation = null; cancellation.Dispose(); IsBusy = false; completion.SetResult();
            }
        }
    }
    private bool Current(int generation) => !_disposed && generation == _generation;
    public void Cancel() => _operationCancellation?.Cancel();
    public ValueTask DisposeAsync()
    {
        _disposed = true; _generation++; Cancel(); OnPropertyChanged(nameof(CanAct)); OnPropertyChanged(nameof(CanOpenTerminal));
        return new(_pending);
    }
}
