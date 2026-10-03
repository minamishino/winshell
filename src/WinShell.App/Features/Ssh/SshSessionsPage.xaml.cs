using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Ssh;

public sealed partial class SshSessionsPage : Page
{
    private readonly NativeDialogService _dialogs;
    private readonly HashSet<Task> _actions = [];
    private readonly List<Exception> _shutdownFailures = [];
    private bool _closing;
    private Task? _shutdown;
    public SshWorkspaceViewModel ViewModel { get; }
    public SshSessionsPage(ISshSessionFactory factory, IKnownHostRepository knownHosts, NativeDialogService dialogs)
    {
        _dialogs = dialogs;
        ViewModel = new(factory, knownHosts, ConfirmHostKeyAsync,
            SynchronizationContext.Current ?? throw new InvalidOperationException("SSH 页面必须在 UI 上下文中创建。"));
        InitializeComponent();
        DataContext = ViewModel;
    }
    private Task<bool> ConfirmHostKeyAsync(SshConnectResult.UnknownHostKey challenge, CancellationToken cancellationToken) =>
        _dialogs.ConfirmAsync(XamlRoot, "确认 SSH 主机公钥", $"主机：{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n公钥类型：{challenge.Key.Algorithm}\n完整指纹：{challenge.Key.Fingerprint}\n\n尚未认证。请通过可信渠道核实指纹，再选择信任并连接。", "信任并连接", cancellationToken);

    public Task ConnectSavedProfileAsync(ConnectionProfile profile) => RunActionAsync(async () =>
    {
        var tab = ViewModel.OpenProfile(profile);
        Feedback.Text = tab.Profile == profile ? "连接使用当前已保存的配置。" : "已切换到此配置的活动会话；它仍使用下方显示的原连接目标。断开后从配置列表连接可使用新设置。";
        UpdateEmptyState();
        FocusSelectedTab();
        await tab.ConnectAsync();
    });
    public Task CloseProfileAsync(Guid profileId) => RunActionAsync(async () =>
    {
        var tab = ViewModel.Tabs.FirstOrDefault(item => item.ProfileId == profileId);
        if (tab is not null) await ViewModel.CloseTabAsync(tab);
        UpdateEmptyState();
    });
    private async void SessionTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is SshSessionViewModel tab) await RunActionAsync(async () =>
        {
            var close = ViewModel.CloseTabAsync(tab);
            UpdateEmptyState();
            Feedback.Text = "正在关闭会话；协议阶段等待期间会话容量仍被保留。";
            await close;
            Feedback.Text = "SSH 会话已关闭。";
            FocusSelectedTab();
        });
    }
    private async Task RunActionAsync(Func<Task> action)
    {
        if (_closing) return;
        var completion = new TaskCompletionSource();
        _actions.Add(completion.Task);
        try { await action(); }
        catch (Exception exception)
        {
            if (_closing) { if (_shutdownFailures.Count < 16) _shutdownFailures.Add(exception); }
            else Feedback.Text = exception is InvalidOperationException or ArgumentException ? exception.Message : "SSH 操作或清理失败，已尝试释放所属资源。";
        }
        finally { _actions.Remove(completion.Task); completion.SetResult(); }
    }
    public Task BeginShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _closing = true;
        IsEnabled = false;
        _shutdown = ShutdownCoreAsync();
        return _shutdown;
    }
    private async Task ShutdownCoreAsync()
    {
        try { await ViewModel.DisposeAsync(); }
        catch (Exception exception) { if (_shutdownFailures.Count < 16) _shutdownFailures.Add(exception); }
        await Task.WhenAll(_actions.ToArray());
        if (_shutdownFailures.Count != 0) throw new AggregateException("SSH 页面关闭失败。", _shutdownFailures);
    }
    private void UpdateEmptyState() => EmptyMessage.Visibility = ViewModel.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void SessionTabs_SelectionChanged(object sender, SelectionChangedEventArgs args) => FocusSelectedTab();
    private void FocusSelectedTab()
    {
        if (!_closing && ViewModel.SelectedTab is { } selected && SessionTabs?.ContainerFromItem(selected) is TabViewItem tab) tab.Focus(FocusState.Programmatic);
    }
    private void SessionView_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is SshSessionView view) view.RunOwnedActionAsync = RunActionAsync;
    }
    private void SessionTab_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TabViewItem tab || tab.DataContext is not SshSessionViewModel session) return;
        NameCloseButton(tab, $"关闭 SSH 会话：{session.Title}");
        if (ReferenceEquals(ViewModel.SelectedTab, session)) FocusSelectedTab();
    }
    private static void NameCloseButton(DependencyObject element, string name)
    {
        if (element is Button { Name: "CloseButton" } button) AutomationProperties.SetName(button, name);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++) NameCloseButton(VisualTreeHelper.GetChild(element, index), name);
    }
}
