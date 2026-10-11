using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinShell.App.Controls;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Terminal;
using WinShell.Core.Docker;
using WinShell.Infrastructure.Windows.Terminal;
using System.ComponentModel;
using Microsoft.UI.Xaml.Automation;
using WinShell.App.Features.Settings;
using WinShell.Core.Settings;

namespace WinShell.App.Features.Terminal;

public sealed partial class TerminalSessionsPage : Page
{
    private readonly RendererJob _job = new();
    private readonly LocalShellSessionFactory _local = new();
    private readonly WslDistributionReader _wsl = new();
    private readonly NativeDialogService _dialogs;
    private readonly SettingsViewModel _settings;
    private readonly AppBarButton _defaultTerminalCommand;
    private readonly HashSet<Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase> _commandFlyouts = [];
    private readonly Dictionary<TerminalTabViewModel, NativeTerminalControl> _controls = [];
    private readonly Dictionary<TerminalTabViewModel, LocalShellLaunchRequest> _localRequests = [];
    private readonly Dictionary<TerminalTabViewModel, DockerTerminalRequest> _dockerRequests = [];
    private readonly HashSet<Task> _actions = [];
    private readonly HashSet<object> _openPopups = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Exception> _shutdownFailures = [];
    private bool _visible, _dialogVisible, _closing, _refreshingWsl;
    private Task? _shutdown;
    public TerminalSessionsViewModel ViewModel { get; }
    public NativeTerminalControl? SelectedControl => ViewModel.SelectedTab is { } selected && _controls.TryGetValue(selected, out var control) ? control : null;

    public TerminalSessionsPage(ISshSessionFactory factory, IKnownHostRepository hosts, NativeDialogService dialogs, IConnectionProfileRepository profiles, SettingsViewModel settings)
    {
        _dialogs = dialogs;
        _settings = settings;
        ViewModel = new(factory, hosts, (challenge, token) => _dialogs.ConfirmAsync(XamlRoot, "确认 SSH 主机公钥",
            $"主机：{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n公钥类型：{challenge.Key.Algorithm}\n完整指纹：{challenge.Key.Fingerprint}\n\n尚未认证，请通过可信渠道核实后再信任。", "信任并连接", token), profiles);
        InitializeComponent(); DataContext = ViewModel;
        ViewModel.PropertyChanged += Terminal_PropertyChanged;
        _settings.PropertyChanged += Settings_PropertyChanged;
        _defaultTerminalCommand = new AppBarButton { Icon = new SymbolIcon(Symbol.Add), Label = "新建" };
        AutomationProperties.SetName(_defaultTerminalCommand, "新建默认终端");
        _defaultTerminalCommand.Click += DefaultTerminal_Click;
        TerminalCommands.PrimaryCommands.Insert(0, _defaultTerminalCommand);
        foreach (var command in TerminalCommands.PrimaryCommands.Concat(TerminalCommands.SecondaryCommands).OfType<AppBarButton>())
            if (command.Flyout is { } flyout && _commandFlyouts.Add(flyout))
            { flyout.Opened += Popup_Opened; flyout.Closed += Popup_Closed; }
    }
    private void Terminal_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ViewModel.SelectedTab)) UpdatePresentation(); }
    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_closing || args.PropertyName != nameof(SettingsViewModel.Settings)) return;
        foreach (var control in _controls.Values) control.ApplyPreferences(_settings.Settings.Terminal);
    }
    private async void DefaultTerminal_Click(object sender, RoutedEventArgs args) => await OpenLocalAsync(
        _settings.Settings.DefaultShell == DefaultShell.Wsl
            ? new LocalShellLaunchRequest.Wsl(new WslDistribution(_settings.Settings.WslDistribution))
            : new LocalShellLaunchRequest.PowerShell());
    private void HideCommandFlyouts()
    {
        foreach (var command in TerminalCommands.PrimaryCommands.Concat(TerminalCommands.SecondaryCommands).OfType<AppBarButton>()) command.Flyout?.Hide();
    }
    public Task ConnectSavedProfileAsync(ConnectionProfile profile) => RunActionAsync(() => OpenAsync(profile.DisplayName, profile, null));
    public async Task<bool> OpenDockerAsync(DockerTerminalRequest request, CancellationToken token)
    {
        var started = false;
        await RunActionAsync(async () => started = await OpenAsync($"{request.Profile.DisplayName} · {request.ContainerId[..12]}", request.Profile, null, request, token));
        return started;
    }
    public Task OpenLocalAsync(LocalShellLaunchRequest request) => RunActionAsync(() => OpenAsync(
        request is LocalShellLaunchRequest.Wsl wsl ? wsl.Distribution.Name : "PowerShell 7.6.6", null, request));
    private async Task<bool> OpenAsync(string title, ConnectionProfile? profile, LocalShellLaunchRequest? local, DockerTerminalRequest? docker = null, CancellationToken startupToken = default)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, startupToken);
        startup.Token.ThrowIfCancellationRequested();
        var tab = ViewModel.Open(() =>
        {
            var control = new NativeTerminalControl(_job, Path.Combine(AppContext.BaseDirectory, "Terminal", "WinShell.Terminal.NativeHost.exe"));
            control.ApplyPreferences(_settings.Settings.Terminal);
            var model = new TerminalTabViewModel(title, profile,
                token => docker is not null ? ViewModel.ConnectDockerAsync(docker, token) : profile is not null ? ViewModel.ConnectSshAsync(profile, token) : Task.FromResult<IShellSession>(_local.Create(local!)),
                control.StartAsync, () => control.OutputCompletion, () => control.CloseAsync(CancellationToken.None),
                docker is null ? "" : $"目标：{docker.Profile.Host}:{docker.Profile.Port} / {docker.ContainerId} / {docker.Shell}。关闭连接不保证容器内任务已停止。");
            control.Faulted += (_, error) => model.RecordFailure(error);
            _controls.Add(model, control);
            if (local is not null) _localRequests.Add(model, local);
            if (docker is not null) _dockerRequests.Add(model, docker);
            TerminalHosts.Children.Add(control);
            return model;
        }, profile?.Id);
        try
        {
            UpdatePresentation();
            var view = _controls[tab];
            if (!view.IsLoaded)
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
                view.Loaded += OnLoaded;
                try { if (!view.IsLoaded) await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10), startup.Token); }
                finally { view.Loaded -= OnLoaded; }
            }
            startup.Token.ThrowIfCancellationRequested();
            _ = tab.StartAsync(docker is null ? default : startup.Token);
            UpdatePresentation();
        }
        catch (Exception initial)
        {
            try { await CloseTabAsync(tab); } catch (Exception cleanup) { initial.Data["TerminalLoadCleanup"] = cleanup; }
            throw;
        }
        if (docker is null) return true;
        var started = await tab.StartupSettled;
        // Join failure/cancellation while retaining the tab's original diagnostic and reconnect target.
        if (!started) await tab.CloseAsync();
        return started;
    }
    public void SetWorkspaceVisible(bool visible)
    {
        _visible = visible;
        if (!visible) { HideCommandFlyouts(); TerminalCommands.IsOpen = false; Distributions.IsDropDownOpen = false; _openPopups.Clear(); }
        UpdatePresentation();
    }
    public void SetDialogVisible(bool visible) { _dialogVisible = visible; UpdatePresentation(); }
    private void Popup_Opened(object? sender, object args) { if (sender is not null) _openPopups.Add(sender); UpdatePresentation(); }
    private void Popup_Closed(object? sender, object args) { if (sender is not null) _openPopups.Remove(sender); UpdatePresentation(); }
    private void UpdatePresentation()
    {
        if (EmptyMessage is null) return;
        EmptyMessage.Visibility = ViewModel.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // The CommandBar overflow is windowed; other overlays still need to suppress native airspace.
        var popupObscuresTerminal = _openPopups.Any(popup => !ReferenceEquals(popup, TerminalCommands));
        foreach (var pair in _controls)
        {
            var selected = ReferenceEquals(ViewModel.SelectedTab, pair.Key);
            pair.Value.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            pair.Value.SetPresentationVisible(_visible && !_dialogVisible && !popupObscuresTerminal && selected && !_closing && !pair.Key.IsClosing,
                allowInput: _openPopups.Count == 0);
        }
        if (_visible && !_dialogVisible && _openPopups.Count == 0 && !_closing) SelectedControl?.Focus(FocusState.Programmatic);
    }
    public Task CloseProfileAsync(Guid profileId) => RunActionAsync(() => CloseProfileOwnersAsync(profileId), closingOwners: true);
    private async Task CloseProfileOwnersAsync(Guid profileId)
    {
        var closing = Task.WhenAll(ViewModel.Tabs.Where(tab => tab.Profile?.Id == profileId).ToArray().Select(CloseTabAsync));
        try { await closing; }
        catch { throw (Exception?)closing.Exception ?? new InvalidOperationException("终端关闭已取消，配置未修改。"); }
    }
    public async Task<IDisposable> BeginProfileMutationAsync(Guid profileId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_closing) throw new InvalidOperationException("窗口正在关闭，无法更新连接配置。");
        var lease = ViewModel.BeginProfileMutation(profileId);
        Feedback.Text = "正在关闭此连接的全部终端，然后更新配置或凭据。保存后请重新打开终端。";
        try { await CloseProfileOwnersAsync(profileId); token.ThrowIfCancellationRequested(); return lease; }
        catch { lease.Dispose(); throw; }
    }
    public async Task CloseTabAsync(TerminalTabViewModel tab)
    {
        try { await ViewModel.CloseTabAsync(tab); }
        finally
        {
            if (_controls.Remove(tab, out var control)) TerminalHosts.Children.Remove(control);
            _localRequests.Remove(tab); _dockerRequests.Remove(tab); UpdatePresentation();
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
    private async void PowerShell_Click(object sender, RoutedEventArgs args)
    { HideCommandFlyouts(); await OpenLocalAsync(new LocalShellLaunchRequest.PowerShell()); }
    private async void Wsl_Click(object sender, RoutedEventArgs args)
    { if (Distributions.SelectedItem is WslDistribution distribution) { HideCommandFlyouts(); await OpenLocalAsync(new LocalShellLaunchRequest.Wsl(distribution)); } else Feedback.Text = "请先刷新并选择 WSL 发行版。"; }
    private async void RefreshWsl_Click(object sender, RoutedEventArgs args)
    {
        if (_refreshingWsl) return;
        _refreshingWsl = true;
        try { await RunActionAsync(async () => { Distributions.ItemsSource = await _wsl.ReadAsync(_stop.Token); Feedback.Text = "WSL 列表已更新。"; }); }
        finally { _refreshingWsl = false; }
    }
    private async void Close_Click(object sender, RoutedEventArgs args)
    { if (ViewModel.SelectedTab is { } tab) await RunActionAsync(() => CloseUserTabAsync(tab)); }
    private async void Tabs_CloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    { if (args.Item is TerminalTabViewModel tab) await RunActionAsync(() => CloseUserTabAsync(tab)); }
    private async Task CloseUserTabAsync(TerminalTabViewModel tab)
    {
        if (_settings.Settings.ConfirmTabClose && !tab.IsClosing &&
            !await _dialogs.ConfirmAsync(XamlRoot, "关闭终端", $"关闭“{tab.Title}”将断开此会话。正在执行的任务可能中断。", "关闭终端", _stop.Token)) return;
        if (!_closing && ViewModel.Tabs.Contains(tab)) await CloseTabAsync(tab);
    }
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs args) => UpdatePresentation();
    private async void Reconnect_Click(object sender, RoutedEventArgs args)
    {
        if (ViewModel.SelectedTab is not { } tab) return;
        _localRequests.TryGetValue(tab, out var local);
        _dockerRequests.TryGetValue(tab, out var docker);
        await RunActionAsync(async () =>
        {
            await CloseTabAsync(tab);
            if (_closing) return;
            if (tab.Profile is { } profile) await ViewModel.ValidateSavedProfileAsync(profile, _stop.Token);
            await OpenAsync(tab.Title, tab.Profile, local, docker);
        });
    }
    private async void Copy_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (ViewModel.SelectedTab?.IsClosing == true) { Feedback.Text = "终端已结束，请在保留的诊断文本中选择并复制。"; return; }
        if (SelectedControl is not { } control) return;
        var text = await control.CopySelectionAsync(_stop.Token);
        var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content);
    });
    private async void Paste_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (ViewModel.SelectedTab?.IsClosing == true) { Feedback.Text = "终端已结束，请重新连接后再粘贴。"; return; }
        if (SelectedControl is not { } control) return;
        await PasteTextAsync(control, ViewModel.SelectedTab, TerminalClipboard.ReadBoundedText());
    });
    private async Task PasteTextAsync(NativeTerminalControl control, TerminalTabViewModel? tab, string text)
    {
        // Check the same budget before displaying a dialog, including developer fixture input.
        if (text.Length > 65536 || new System.Text.UTF8Encoding(false, true).GetByteCount(text) > 65536)
            throw new ArgumentException("粘贴内容不能超过64KiB。", nameof(text));
        if (_settings.Settings.ConfirmMultilinePaste && (text.Contains('\r') || text.Contains('\n')) &&
            !await _dialogs.ConfirmAsync(XamlRoot, "粘贴多行内容", "粘贴内容包含换行，可能立即执行一个或多个命令。", "继续粘贴", _stop.Token)) return;
        // Revalidate after the dialog; navigation, reconnect or close may have changed the target.
        if (!_closing && tab is { IsClosing: false } && ReferenceEquals(tab, ViewModel.SelectedTab) && ReferenceEquals(control, SelectedControl))
            await control.PasteAsync(text, _stop.Token);
    }
    public Task BeginShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _closing = true; IsEnabled = false; _stop.Cancel();
        _settings.PropertyChanged -= Settings_PropertyChanged;
        ViewModel.PropertyChanged -= Terminal_PropertyChanged;
        _defaultTerminalCommand.Click -= DefaultTerminal_Click;
        HideCommandFlyouts();
        foreach (var flyout in _commandFlyouts) { flyout.Opened -= Popup_Opened; flyout.Closed -= Popup_Closed; }
        _commandFlyouts.Clear();
        TerminalCommands.IsOpen = false; Distributions.IsDropDownOpen = false; _openPopups.Clear(); UpdatePresentation();
        return _shutdown = ShutdownCoreAsync();
    }
    private async Task ShutdownCoreAsync()
    {
        try { await ViewModel.DisposeAsync(); } catch (Exception error) { _shutdownFailures.Add(error); }
        await Task.WhenAll(_actions.ToArray());
        TerminalHosts.Children.Clear(); _controls.Clear(); _localRequests.Clear(); _dockerRequests.Clear();
        _job.Dispose(); _stop.Dispose();
        if (_shutdownFailures.Count != 0) throw new AggregateException("终端页面清理失败。", _shutdownFailures);
    }
}
