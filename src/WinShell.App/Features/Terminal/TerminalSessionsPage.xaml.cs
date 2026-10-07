using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinShell.App.Controls;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Terminal;
using WinShell.Infrastructure.Windows.Terminal;

namespace WinShell.App.Features.Terminal;

public sealed partial class TerminalSessionsPage : Page
{
    private readonly RendererJob _job = new();
    private readonly LocalShellSessionFactory _local = new();
    private readonly WslDistributionReader _wsl = new();
    private readonly NativeDialogService _dialogs;
    private readonly Dictionary<TerminalTabViewModel, NativeTerminalControl> _controls = [];
    private readonly Dictionary<TerminalTabViewModel, LocalShellLaunchRequest> _localRequests = [];
    private readonly HashSet<Task> _actions = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Exception> _shutdownFailures = [];
    private bool _visible, _dialogVisible, _closing, _refreshingWsl;
    private Task? _shutdown;
    public TerminalSessionsViewModel ViewModel { get; }
    public NativeTerminalControl? SelectedControl => ViewModel.SelectedTab is { } selected && _controls.TryGetValue(selected, out var control) ? control : null;

    public TerminalSessionsPage(ISshSessionFactory factory, IKnownHostRepository hosts, NativeDialogService dialogs)
    {
        _dialogs = dialogs;
        ViewModel = new(factory, hosts, (challenge, token) => _dialogs.ConfirmAsync(XamlRoot, "确认 SSH 主机公钥",
            $"主机：{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n公钥类型：{challenge.Key.Algorithm}\n完整指纹：{challenge.Key.Fingerprint}\n\n尚未认证，请通过可信渠道核实后再信任。", "信任并连接", token));
        InitializeComponent(); DataContext = ViewModel;
        ViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ViewModel.SelectedTab)) UpdatePresentation(); };
    }
    public Task ConnectSavedProfileAsync(ConnectionProfile profile) => RunActionAsync(() => OpenAsync(profile.DisplayName, profile, null));
    public Task OpenLocalAsync(LocalShellLaunchRequest request) => RunActionAsync(() => OpenAsync(
        request is LocalShellLaunchRequest.Wsl wsl ? wsl.Distribution.Name : "PowerShell 7.6.6", null, request));
    private async Task OpenAsync(string title, ConnectionProfile? profile, LocalShellLaunchRequest? local)
    {
        var tab = ViewModel.Open(() =>
        {
            var control = new NativeTerminalControl(_job, Path.Combine(AppContext.BaseDirectory, "Terminal", "WinShell.Terminal.NativeHost.exe"));
            var model = new TerminalTabViewModel(title, profile,
                token => profile is not null ? ViewModel.ConnectSshAsync(profile, token) : Task.FromResult<IShellSession>(_local.Create(local!)),
                control.StartAsync, () => control.OutputCompletion, () => control.CloseAsync(CancellationToken.None));
            control.Faulted += (_, error) => model.RecordFailure(error);
            _controls.Add(model, control);
            if (local is not null) _localRequests.Add(model, local);
            TerminalHosts.Children.Add(control);
            return model;
        });
        try
        {
            UpdatePresentation();
            var view = _controls[tab];
            if (!view.IsLoaded)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
                view.Loaded += OnLoaded;
                try { if (!view.IsLoaded) await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), _stop.Token); }
                finally { view.Loaded -= OnLoaded; }
            }
            if (!_closing) _ = tab.StartAsync();
            UpdatePresentation();
        }
        catch (Exception initial)
        {
            try { await CloseTabAsync(tab); } catch (Exception cleanup) { initial.Data["TerminalLoadCleanup"] = cleanup; }
            throw;
        }
    }
    public void SetWorkspaceVisible(bool visible) { _visible = visible; UpdatePresentation(); }
    public void SetDialogVisible(bool visible) { _dialogVisible = visible; UpdatePresentation(); }
    private void UpdatePresentation()
    {
        if (EmptyMessage is null) return;
        EmptyMessage.Visibility = ViewModel.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var pair in _controls)
        {
            var selected = ReferenceEquals(ViewModel.SelectedTab, pair.Key);
            pair.Value.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            pair.Value.SetPresentationVisible(_visible && !_dialogVisible && selected && !_closing && !pair.Key.IsClosing);
        }
        if (_visible && !_dialogVisible && !_closing) SelectedControl?.Focus(FocusState.Programmatic);
    }
    public Task CloseProfileAsync(Guid profileId) => RunActionAsync(async () =>
    { foreach (var tab in ViewModel.Tabs.Where(tab => tab.Profile?.Id == profileId).ToArray()) await CloseTabAsync(tab); }, closingOwners: true);
    public async Task CloseTabAsync(TerminalTabViewModel tab)
    {
        try { await ViewModel.CloseTabAsync(tab); }
        finally
        {
            if (_controls.Remove(tab, out var control)) TerminalHosts.Children.Remove(control);
            _localRequests.Remove(tab); UpdatePresentation();
        }
    }
    private async Task RunActionAsync(Func<Task> action, bool closingOwners = false)
    {
        if (_closing) return;
        if (!closingOwners && _actions.Count >= 8) { Feedback.Text = "终端操作正在处理中，请等待当前操作完成。"; return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _actions.Add(completion.Task);
        try { await action(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { if (_closing) { if (_shutdownFailures.Count < 8) _shutdownFailures.Add(error); } else Feedback.Text = error.Message; }
        finally { _actions.Remove(completion.Task); completion.TrySetResult(); }
    }
    private async void PowerShell_Click(object sender, RoutedEventArgs args) => await OpenLocalAsync(new LocalShellLaunchRequest.PowerShell());
    private async void Wsl_Click(object sender, RoutedEventArgs args)
    { if (Distributions.SelectedItem is WslDistribution distribution) await OpenLocalAsync(new LocalShellLaunchRequest.Wsl(distribution)); else Feedback.Text = "请先刷新并选择 WSL 发行版。"; }
    private async void RefreshWsl_Click(object sender, RoutedEventArgs args)
    {
        if (_refreshingWsl) return;
        _refreshingWsl = true;
        try { await RunActionAsync(async () => { Distributions.ItemsSource = await _wsl.ReadAsync(_stop.Token); Feedback.Text = "WSL 列表已更新。"; }); }
        finally { _refreshingWsl = false; }
    }
    private async void Close_Click(object sender, RoutedEventArgs args)
    { if (ViewModel.SelectedTab is { } tab) await RunActionAsync(() => CloseTabAsync(tab)); }
    private async void Tabs_CloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    { if (args.Item is TerminalTabViewModel tab) await RunActionAsync(() => CloseTabAsync(tab)); }
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdatePresentation();
    private async void Reconnect_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedTab is not { } tab) return;
        _localRequests.TryGetValue(tab, out var local);
        await RunActionAsync(async () => { await CloseTabAsync(tab); if (!_closing) await OpenAsync(tab.Title, tab.Profile, local); });
    }
    private async void Copy_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (SelectedControl is not { } control) return;
        var text = await control.CopySelectionAsync(_stop.Token);
        var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content);
    });
    private async void Paste_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (SelectedControl is not { } control) return;
        await control.PasteAsync(TerminalClipboard.ReadBoundedText(), _stop.Token);
    });
    public Task BeginShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _closing = true; IsEnabled = false; _stop.Cancel(); UpdatePresentation();
        return _shutdown = ShutdownCoreAsync();
    }
    private async Task ShutdownCoreAsync()
    {
        try { await ViewModel.DisposeAsync(); } catch (Exception error) { _shutdownFailures.Add(error); }
        await Task.WhenAll(_actions.ToArray());
        TerminalHosts.Children.Clear(); _controls.Clear(); _localRequests.Clear();
        _job.Dispose(); _stop.Dispose();
        if (_shutdownFailures.Count != 0) throw new AggregateException("终端页面清理失败。", _shutdownFailures);
    }
}
