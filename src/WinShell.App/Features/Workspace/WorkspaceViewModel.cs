using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Workspace;

// UI-affine. Detail snapshots own cancellation only; they do not start sessions.
public sealed partial class WorkspaceViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ObservableCollection<ProfileTabViewModel> _tabs = [];

    private bool _disposed;
    private Task? _disposal;

    public WorkspaceViewModel() => Tabs = new ReadOnlyObservableCollection<ProfileTabViewModel>(_tabs);
    public ReadOnlyObservableCollection<ProfileTabViewModel> Tabs { get; }
    public ProfileTabViewModel? SelectedTab
    {
        get;
        set
        {
            if (value is not null && !_tabs.Contains(value)) throw new ArgumentException("详情标签不属于此工作区。", nameof(value));
            SetProperty(ref field, value);
        }
    }

    public ProfileTabViewModel OpenProfile(ConnectionProfile profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var tab = _tabs.FirstOrDefault(item => item.ProfileId == profile.Id);
        if (tab is null)
        {
            if (_tabs.Count >= 16) throw new InvalidOperationException("最多打开 16 个详情标签，请先关闭一个标签。");
            tab = new ProfileTabViewModel(profile);
            _tabs.Add(tab);
        }
        SelectedTab = tab;
        return tab;
    }

    public async ValueTask CloseTabAsync(Guid tabId)
    {
        var tab = _tabs.FirstOrDefault(item => item.TabId == tabId);
        if (tab is null) return;
        try { await tab.DisposeAsync(); }
        finally
        {
            var index = _tabs.IndexOf(tab);
            var wasSelected = SelectedTab == tab;
            _tabs.Remove(tab);
            if (wasSelected) SelectedTab = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new ValueTask(_disposal);
        _disposed = true;
        _disposal = DisposeTabsAsync();
        return new ValueTask(_disposal);
    }

    private async Task DisposeTabsAsync()
    {
        List<Exception> failures = [];
        foreach (var tab in _tabs.ToArray())
        {
            try { await CloseTabAsync(tab.TabId); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new AggregateException("部分详情标签的取消处理失败，已继续释放其他标签。", failures);
    }
}
