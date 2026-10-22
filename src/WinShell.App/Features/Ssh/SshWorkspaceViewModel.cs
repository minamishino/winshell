using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Ssh;

// UI-affine; owners includes hidden tabs until every close has settled.
public sealed partial class SshWorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISshSessionFactory _factory;
    private readonly IKnownHostRepository _knownHosts;
    private readonly Func<SshConnectResult.UnknownHostKey, CancellationToken, Task<bool>> _confirm;
    private readonly SynchronizationContext _context;
    private readonly ObservableCollection<SshSessionViewModel> _tabs = [];
    private readonly HashSet<SshSessionViewModel> _owners = [];
    private readonly Dictionary<SshSessionViewModel, Task> _closing = [];
    private readonly List<Exception> _cleanupFailures = [];

    private bool _disposed;
    private Task? _disposal;
    public SshWorkspaceViewModel(ISshSessionFactory factory, IKnownHostRepository knownHosts,
        Func<SshConnectResult.UnknownHostKey, CancellationToken, Task<bool>> confirm, SynchronizationContext context)
    { _factory = factory; _knownHosts = knownHosts; _confirm = confirm; _context = context; Tabs = new ReadOnlyObservableCollection<SshSessionViewModel>(_tabs); }
    public ReadOnlyObservableCollection<SshSessionViewModel> Tabs { get; }
    public SshSessionViewModel? SelectedTab
    {
        get;
        set
        {
            if (value is not null && !_tabs.Contains(value)) throw new ArgumentException("SSH 标签不属于此工作区。");
            SetProperty(ref field, value);
        }
    }
    public SshSessionViewModel OpenProfile(ConnectionProfile profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (profile.Protocol != ConnectionProtocol.Ssh) throw new ArgumentException("此操作仅支持 SSH 配置。");
        var tab = _owners.FirstOrDefault(owner => owner.ProfileId == profile.Id);
        if (tab is not null && _closing.ContainsKey(tab)) throw new InvalidOperationException("此 SSH 会话仍在关闭，请等待资源清理完成。");
        if (tab is null)
        {
            if (_owners.Count >= 16) throw new InvalidOperationException("最多拥有 16 个 SSH 会话（包括正在关闭的会话），请等待或先关闭一个。");
            tab = new SshSessionViewModel(profile, _factory, _knownHosts, _confirm, _context);
            _owners.Add(tab);
            _tabs.Add(tab);
        }
        else tab.ApplySavedProfile(profile);
        SelectedTab = tab;
        return tab;
    }
    public Task CloseTabAsync(SshSessionViewModel tab)
    {
        if (_closing.TryGetValue(tab, out var pending)) return pending;
        if (!_owners.Contains(tab)) return Task.CompletedTask;
        var completion = new TaskCompletionSource();
        _closing.Add(tab, completion.Task);
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        if (ReferenceEquals(SelectedTab, tab)) SelectedTab = _tabs.Count == 0 ? null : _tabs[Math.Min(Math.Max(index, 0), _tabs.Count - 1)];
        _ = CloseAsync();
        return completion.Task;
        async Task CloseAsync()
        {
            Exception? failure = null;
            try { await tab.DisposeAsync(); }
            catch (Exception exception) { failure = exception; if (_cleanupFailures.Count < 16) _cleanupFailures.Add(exception); }
            finally { _owners.Remove(tab); _closing.Remove(tab); }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
        }
    }
    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new ValueTask(_disposal);
        _disposed = true;
        _disposal = DisposeCoreAsync();
        return new ValueTask(_disposal);
    }
    private async Task DisposeCoreAsync()
    {
        var closes = _owners.ToArray().Select(CloseTabAsync).ToArray();
        try { await Task.WhenAll(closes); }
        catch { /* Each owner has retained its failure, and WhenAll joined every close. */ }
        if (_cleanupFailures.Count != 0) throw new AggregateException("部分 SSH 会话清理失败，其他会话已尝试关闭。", _cleanupFailures);
    }
}
