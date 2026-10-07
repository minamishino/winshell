using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Terminal;
using WinShell.Core.Docker;
using WinShell.Infrastructure.Ssh.Docker;

namespace WinShell.App.Features.Terminal;

public sealed class TerminalSessionsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISshSessionFactory _factory;
    private readonly IKnownHostRepository _knownHosts;
    private readonly IConnectionProfileRepository _profiles;
    private readonly HashSet<Guid> _mutatingProfiles = [];
    private readonly Func<SshConnectResult.UnknownHostKey, CancellationToken, Task<bool>> _confirm;
    public TerminalSessionsViewModel(ISshSessionFactory factory, IKnownHostRepository knownHosts,
        Func<SshConnectResult.UnknownHostKey, CancellationToken, Task<bool>> confirm, IConnectionProfileRepository profiles)
    { _factory = factory; _knownHosts = knownHosts; _confirm = confirm; _profiles = profiles; Tabs = new(_tabs); }
    private readonly ObservableCollection<TerminalTabViewModel> _tabs = [];
    private readonly HashSet<TerminalTabViewModel> _owners = [];
    private readonly Dictionary<TerminalTabViewModel, Task> _closing = [];
    private TerminalTabViewModel? _selected;
    private Task? _shutdown;
    private bool _disposed;
    public ReadOnlyObservableCollection<TerminalTabViewModel> Tabs { get; }
    public int OwnedCount => _owners.Count;
    public TerminalTabViewModel? SelectedTab
    {
        get => _selected;
        set
        {
            if (value is not null && !_tabs.Contains(value)) throw new ArgumentException("终端标签不属于此工作区。");
            SetProperty(ref _selected, value);
        }
    }
    public TerminalTabViewModel Open(Func<TerminalTabViewModel> createTab, Guid? profileId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (profileId is { } id) RequireProfileAvailable(id);
        if (_owners.Count >= 2) throw new InvalidOperationException("最多同时拥有两个终端（包括启动中和关闭中），请等待一个终端完成关闭。");
        var tab = createTab();
        _owners.Add(tab); _tabs.Add(tab); SelectedTab = tab;
        OnPropertyChanged(nameof(OwnedCount));
        return tab;
    }
    public Task<IShellSession> ConnectDockerAsync(DockerTerminalRequest request, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = DockerScriptBuilder.Terminal(request.ContainerId, request.Shell);
        return ConnectCoreAsync(request.Profile, token, command);
    }
    public Task<IShellSession> ConnectSshAsync(ConnectionProfile profile, CancellationToken token) => ConnectCoreAsync(profile, token, null);
    private async Task<IShellSession> ConnectCoreAsync(ConnectionProfile profile, CancellationToken token, string? startupCommand)
    {
        await ValidateSavedProfileAsync(profile, token);
        var result = await _factory.ConnectAsync(profile, token);
        if (result is SshConnectResult.UnknownHostKey challenge)
        {
            if (!await _confirm(challenge, token)) throw new OperationCanceledException("已取消主机公钥确认。", token);
            token.ThrowIfCancellationRequested();
            await ValidateSavedProfileAsync(profile, token);
            if (!await _knownHosts.TrustIfAbsentOrSameAsync(challenge.Endpoint, challenge.Key, token))
                throw new SshConnectionException(SshFailureKind.HostKeyChanged, "可信主机记录已变化，连接已拒绝。");
            await ValidateSavedProfileAsync(profile, token);
            result = await _factory.ConnectAsync(profile, token);
        }
        if (result is not SshConnectResult.Connected connected)
            throw new SshConnectionException(SshFailureKind.Protocol, "重新连接仍未能确认主机身份。");
        try
        {
            token.ThrowIfCancellationRequested();
            await ValidateSavedProfileAsync(profile, token);
            if (connected.Session is not ISshTerminalSession terminal)
                throw new SshConnectionException(SshFailureKind.Protocol, "SSH 连接不支持交互终端。");
            return terminal.CreateTerminalSession(80, 24, startupCommand);
        }
        catch (Exception initial)
        {
            try { await connected.Session.DisposeAsync(); }
            catch (Exception cleanup) { initial.Data["TerminalCleanupFailure"] = cleanup; }
            throw;
        }
    }
    public async Task ValidateSavedProfileAsync(ConnectionProfile profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RequireProfileAvailable(profile.Id);
        var saved = (await _profiles.ReadAsync(token)).Profiles.FirstOrDefault(item => item.Id == profile.Id);
        token.ThrowIfCancellationRequested();
        RequireProfileAvailable(profile.Id);
        if (saved is null || saved with { DisplayName = profile.DisplayName, GroupName = profile.GroupName } != profile)
            throw new InvalidOperationException("连接配置已更改或删除，请从保存的连接配置重新打开终端。");
    }
    private void RequireProfileAvailable(Guid id)
    {
        if (_mutatingProfiles.Contains(id)) throw new InvalidOperationException("正在更新此连接的配置或凭据，请等待保存完成后重新打开终端。");
    }
    public IDisposable BeginProfileMutation(Guid profileId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_mutatingProfiles.Add(profileId)) throw new InvalidOperationException("此连接已有配置或凭据操作。");
        return new ProfileMutation(this, profileId);
    }
    private sealed class ProfileMutation(TerminalSessionsViewModel owner, Guid profileId) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { if (_disposed) return; _disposed = true; owner._mutatingProfiles.Remove(profileId); }
    }
    public Task CloseTabAsync(TerminalTabViewModel tab)
    {
        if (_closing.TryGetValue(tab, out var current)) return current;
        if (!_owners.Contains(tab)) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _closing.Add(tab, completion.Task);
        _ = CloseAsync();
        return completion.Task;
        async Task CloseAsync()
        {
            Exception? failure = null;
            try { await tab.CloseAsync(); }
            catch (Exception error) { failure = error; }
            finally
            {
                var index = _tabs.IndexOf(tab);
                _tabs.Remove(tab); _owners.Remove(tab); _closing.Remove(tab);
                if (ReferenceEquals(SelectedTab, tab)) SelectedTab = _tabs.Count == 0 ? null : _tabs[Math.Clamp(index, 0, _tabs.Count - 1)];
                OnPropertyChanged(nameof(OwnedCount));
            }
            if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
        }
    }
    public ValueTask DisposeAsync()
    {
        if (_shutdown is not null) return new(_shutdown);
        _disposed = true;
        return new(_shutdown = Task.WhenAll(_owners.ToArray().Select(CloseTabAsync)));
    }
}
