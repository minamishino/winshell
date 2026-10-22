using WinShell.App.Features.Settings;
using WinShell.Core.Settings;
using WinShell.App.Features.Tunnels;
using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinShell.App.Features.Connections;
using WinShell.App.Features.Workspace;
using WinShell.App.Features.Ssh;
using WinShell.App.Features.Docker;
using WinShell.App.Features.Terminal;
using WinShell.Core.Profiles;
using WinShell.Core.Docker;
using WinShell.App.Features.Files;

namespace WinShell.App;

public sealed partial class MainWindow
{
    private readonly ConnectionProfilesPage _connectionsPage;
    private readonly WorkspaceViewModel _workspace;
    private readonly SshSessionsPage _sshPage;
    private readonly TerminalSessionsPage _terminalPage;
    private readonly NativeDialogService _dialogs;
    private readonly FilesPage _filesPage;
    private readonly TunnelsPage _tunnelsPage;
    private readonly DockerPage _dockerPage;
    private bool _dockerHandoffNavigation;
    private bool _nativeDialogVisible, _navigationPaneVisible;
    private bool _navigationFocusQueued;
    private Guid? _requestedFilesProfileId;
    private int _navigationGeneration;
    private readonly SettingsPage _settingsPage;
    private readonly HashSet<Task> _pendingTabActions = [];
    private Func<Task>? _shutdownAsync;
    private bool _closing, _closeReady;

    public MainWindow(ConnectionProfilesPage connectionsPage, WorkspaceViewModel workspace, SshSessionsPage sshPage, FilesPage filesPage, SettingsPage settingsPage, TunnelsPage tunnelsPage, DockerPage dockerPage, TerminalSessionsPage terminalPage, NativeDialogService dialogs)
    {
        _connectionsPage = connectionsPage;
        _workspace = workspace;
        _sshPage = sshPage;
        _terminalPage = terminalPage;
        _dockerPage = dockerPage;
        _dockerPage.OpenTerminalAsync = OpenDockerTerminalAsync;
        _terminalPage.ViewModel.PropertyChanged += Terminal_PropertyChanged;
        _connectionsPage.ViewModel.BeginTerminalProfileMutationAsync = _terminalPage.BeginProfileMutationAsync;
        _dialogs = dialogs;
        _dialogs.PresentationChanged = DialogPresentationChanged;
        _filesPage = filesPage;
        _tunnelsPage = tunnelsPage;
        _settingsPage = settingsPage;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "winshell.ico"));
        ConnectionsHost.Content = connectionsPage;
        SshHost.Content = sshPage;
        TerminalHost.Content = terminalPage;
        FilesHost.Content = filesPage;
        TunnelsHost.Content = tunnelsPage;
        filesPage.SetOwnerWindow(this);
        SettingsHost.Content = settingsPage;
        _settingsPage.ViewModel.PropertyChanged += Settings_PropertyChanged;
        RootLayout.ActualThemeChanged += RootTheme_Changed;
        ApplyTheme();
        if (Navigation.SettingsItem is NavigationViewItem settingsItem)
        {
            settingsItem.Content = "设置";
            AutomationProperties.SetName(settingsItem, "设置");
        }
        DockerHost.Content = dockerPage;
        WorkspaceHost.DataContext = workspace;
        Navigation.SelectedItem = SshItem;
        _connectionsPage.ConnectSshRequested += Connections_ConnectSshRequested;
        _connectionsPage.OpenFilesRequested += Connections_OpenFilesRequested;
        _connectionsPage.OpenProfileRequested += Connections_OpenProfileRequested;
        _connectionsPage.ViewModel.ProfileDeleted += Connections_ProfileDeleted;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
        AppWindow.Closing += Window_Closing;
    }

    private void DialogPresentationChanged(bool visible)
    {
        _nativeDialogVisible = visible;
        UpdateTerminalOverlay();
    }
    private void Navigation_PaneOpening(NavigationView sender, object args)
    {
        _navigationPaneVisible = true;
        UpdateTerminalOverlay();
    }
    private void Navigation_PaneClosed(NavigationView sender, object args)
    {
        _navigationPaneVisible = false;
        UpdateTerminalOverlay();
    }
    private void UpdateTerminalOverlay()
    {
        _terminalPage.SetDialogVisible(_nativeDialogVisible || _navigationPaneVisible);
        if (_closing || _nativeDialogVisible || !_navigationPaneVisible) return;
        FocusNavigationPane();
        // Pane opening and ContentDialog dismissal can restore focus later in the current turn.
        // Keep one deferred request and recheck ownership so a closed/background pane cannot steal it.
        if (_navigationFocusQueued) return;
        _navigationFocusQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _navigationFocusQueued = false;
            if (!_closing && !_nativeDialogVisible && _navigationPaneVisible && Navigation.IsPaneOpen)
                FocusNavigationPane();
        });
    }
    private void FocusNavigationPane()
    {
        var item = Navigation.SelectedItem is NavigationViewItem { Visibility: Visibility.Visible, IsEnabled: true } selectedItem
            ? selectedItem
            : ConnectionsItem;
        item.Focus(FocusState.Keyboard);
    }
    private void HideTerminalFlyouts()
    {
        if (_terminalPage.FindName("TerminalCommands") is not CommandBar commands) return;
        foreach (var command in commands.PrimaryCommands.Concat(commands.SecondaryCommands).OfType<AppBarButton>()) command.Flyout?.Hide();
    }
    private Guid? PreferredProfileId => _terminalPage.ViewModel.SelectedTab?.Profile?.Id ?? _connectionsPage.ViewModel.SelectedProfile?.Id;
    private void Connections_OpenFilesRequested(object? sender, ConnectionProfile profile)
    {
        if (_closing) return;
        _requestedFilesProfileId = profile.Id;
        Navigation.SelectedItem = FilesItem;
    }

    public void SetShutdownHandler(Func<Task> shutdownAsync) => _shutdownAsync = shutdownAsync;
    private void Terminal_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TerminalSessionsViewModel.OwnedCount))
            _dockerPage.ViewModel.TerminalCapacityAvailable = !_closing && _terminalPage.ViewModel.OwnedCount < 2;
    }
    private async Task<bool> OpenDockerTerminalAsync(DockerTerminalRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_closing) return false;
        // Only this synchronous navigation belongs to the handoff. Later navigation cancels pending startup.
        _dockerHandoffNavigation = true;
        try { Navigation.SelectedItem = SshItem; }
        finally { _dockerHandoffNavigation = false; }
        return await _terminalPage.OpenDockerAsync(request, token);
    }

    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_closing && args.PropertyName == nameof(SettingsViewModel.Theme)) ApplyTheme();
    }
    private void ApplyTheme()
    {
        RootLayout.RequestedTheme = _settingsPage.ViewModel.Theme switch
        {
            AppearanceTheme.Light => ElementTheme.Light,
            AppearanceTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateTitleBarTheme();
    }
    private void RootTheme_Changed(FrameworkElement sender, object args) => UpdateTitleBarTheme();
    private void UpdateTitleBarTheme()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        AppWindow.TitleBar.PreferredTheme = RootLayout.ActualTheme == ElementTheme.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
    }


    private void Connections_OpenProfileRequested(object? sender, ConnectionProfile profile)
    {
        if (_closing) return;
        try
        {
            _workspace.OpenProfile(profile);
            Navigation.SelectedItem = WorkspaceItem;
            UpdateWorkspaceState();
            FocusSelectedTab();
        }
        catch (InvalidOperationException exception) { _connectionsPage.ViewModel.ReportValidationError(exception.Message); }
    }

    private async void Connections_ConnectSshRequested(object? sender, ConnectionProfile profile)
    {
        try
        {
            if (_closing) return;
            Navigation.SelectedItem = SshItem;
            await _terminalPage.ConnectSavedProfileAsync(profile);
        }
        catch (Exception)
        {
            Navigation.Header = new TextBlock { Text = "无法打开 SSH 终端，请检查连接状态后重试。", TextWrapping = TextWrapping.Wrap };
            Navigation.AlwaysShowHeader = true;
        }
    }

    private async void Connections_ProfileDeleted(object? sender, Guid profileId)
    {
        try
        {
            if (_closing) return;
            var tab = _workspace.Tabs.FirstOrDefault(item => item.ProfileId == profileId);
            await Task.WhenAll(_sshPage.CloseProfileAsync(profileId),
                _terminalPage.CloseProfileAsync(profileId), _filesPage.CloseProfileAsync(profileId),
                tab is not null ? CloseDetailsAsync(tab.TabId) : Task.CompletedTask,
                _tunnelsPage.CloseProfileAsync(profileId));
        }
        catch (Exception)
        {
            Navigation.Header = new TextBlock { Text = "连接已删除，但部分会话清理失败。关闭窗口时会继续清理。", TextWrapping = TextWrapping.Wrap };
            Navigation.AlwaysShowHeader = true;
        }
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        try
        {
            if (ConnectionsHost is null || WorkspaceHost is null || SshHost is null || FilesHost is null || TunnelsHost is null || SettingsHost is null || DockerHost is null || TerminalHost is null) return;
            var generation = ++_navigationGeneration;
            var preferredFilesProfileId = _requestedFilesProfileId ?? PreferredProfileId;
            _requestedFilesProfileId = null;
            var showWorkspace = ReferenceEquals(args.SelectedItem, WorkspaceItem);
            var showTunnels = ReferenceEquals(args.SelectedItem, TunnelsItem);
            var showFiles = ReferenceEquals(args.SelectedItem, FilesItem);
            var showSettings = args.IsSettingsSelected;
            var showTerminal = ReferenceEquals(args.SelectedItem, SshItem);
            Navigation.IsPaneOpen = false;
            if (!showTerminal) HideTerminalFlyouts();
            TerminalHost.Visibility = showTerminal ? Visibility.Visible : Visibility.Collapsed;
            _terminalPage.SetWorkspaceVisible(showTerminal);
            SettingsHost.Visibility = showSettings ? Visibility.Visible : Visibility.Collapsed;
            var showDocker = ReferenceEquals(args.SelectedItem, DockerItem);
            var cancelDocker = !_dockerHandoffNavigation ? _dockerPage.ViewModel.CancelPendingAsync() : Task.CompletedTask;
            DockerHost.Visibility = showDocker ? Visibility.Visible : Visibility.Collapsed;
            ConnectionsHost.Visibility = showWorkspace || showTerminal || showFiles || showTunnels || showSettings || showDocker ? Visibility.Collapsed : Visibility.Visible;
            SshHost.Visibility = Visibility.Collapsed;
            WorkspaceHost.Visibility = showWorkspace ? Visibility.Visible : Visibility.Collapsed;
            FilesHost.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
            TunnelsHost.Visibility = showTunnels ? Visibility.Visible : Visibility.Collapsed;
            await cancelDocker;
            if (generation != _navigationGeneration || _closing) return;
            if (showTunnels && !_closing) await _tunnelsPage.LoadAsync();
            if (showWorkspace) FocusSelectedTab();
            if (showFiles && !_closing) await _filesPage.LoadProfilesAsync(preferredFilesProfileId);
            if (showDocker && !_closing) await _dockerPage.SelectProfileAsync(PreferredProfileId);
        }
        catch (Exception)
        {
            Navigation.Header = new TextBlock { Text = "页面切换失败，请重试。", TextWrapping = TextWrapping.Wrap };
            Navigation.AlwaysShowHeader = true;
        }
    }

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_closing && args.PropertyName == nameof(WorkspaceViewModel.SelectedTab)) UpdateWorkspaceState();
    }

    private void UpdateWorkspaceState() => EmptyWorkspace.Visibility = _workspace.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void DetailsTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        try
        {
            if (!_closing && args.Item is ProfileTabViewModel tab) await CloseDetailsAsync(tab.TabId);
        }
        catch (Exception)
        {
            Navigation.Header = new TextBlock { Text = "关闭详情标签失败，请重试。", TextWrapping = TextWrapping.Wrap };
            Navigation.AlwaysShowHeader = true;
        }
    }

    private async Task CloseDetailsAsync(Guid tabId)
    {
        var completion = new TaskCompletionSource();
        _pendingTabActions.Add(completion.Task);
        try
        {
            await _workspace.CloseTabAsync(tabId);
            if (_closing) return;
            UpdateWorkspaceState();
            if (_workspace.SelectedTab is not null) FocusSelectedTab();
            else
            {
                Navigation.SelectedItem = ConnectionsItem;
                _connectionsPage.FocusProfiles();
            }
        }
        catch (Exception)
        {
            Navigation.Header = new TextBlock { Text = "详情标签清理失败，已尝试释放资源。", TextWrapping = TextWrapping.Wrap };
            Navigation.AlwaysShowHeader = true;
        }
        finally
        {
            _pendingTabActions.Remove(completion.Task);
            completion.SetResult();
        }
    }

    private void DetailsTabs_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_closing) FocusSelectedTab();
    }

    private void FocusSelectedTab()
    {
        if (_closing || DetailsTabs is null || _workspace.SelectedTab is null) return;
        if (DetailsTabs.ContainerFromItem(_workspace.SelectedTab) is TabViewItem tab) tab.Focus(FocusState.Programmatic);
    }

    private void DetailTab_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is not TabViewItem { DataContext: ProfileTabViewModel viewModel } tab) return;
        // Keep the platform template, but give its native close button a profile-specific name.
        SetCloseButtonName(tab, $"关闭详情标签：{viewModel.Title}");
        if (ReferenceEquals(_workspace.SelectedTab, viewModel)) FocusSelectedTab();
    }

    private static void SetCloseButtonName(DependencyObject element, string label)
    {
        if (element is Button { Name: "CloseButton" } button) AutomationProperties.SetName(button, label);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            SetCloseButtonName(VisualTreeHelper.GetChild(element, index), label);
    }

    public Task BeginShutdownAsync()
    {
        _closing = true;
        HideTerminalFlyouts();
        Navigation.IsEnabled = false;
        _connectionsPage.ConnectSshRequested -= Connections_ConnectSshRequested;
        _connectionsPage.OpenFilesRequested -= Connections_OpenFilesRequested;
        _connectionsPage.OpenProfileRequested -= Connections_OpenProfileRequested;
        _connectionsPage.ViewModel.ProfileDeleted -= Connections_ProfileDeleted;
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
        _settingsPage.ViewModel.PropertyChanged -= Settings_PropertyChanged;
        RootLayout.ActualThemeChanged -= RootTheme_Changed;
        _dialogs.PresentationChanged = null;
        _dockerPage.OpenTerminalAsync = null;
        _terminalPage.ViewModel.PropertyChanged -= Terminal_PropertyChanged;
        _dockerPage.ViewModel.Cancel();
        return Task.WhenAll(_pendingTabActions.ToArray());
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            if (_closeReady) return;
            args.Cancel = true;
            if (_closing) return;
            _closing = true;
            if (_shutdownAsync is not null) await _shutdownAsync();
            _closeReady = true;
            AppWindow.Closing -= Window_Closing;
            Close();
        }
        catch (Exception)
        {
            // All cleanup owners have been attempted. Keep this failure visible until the user closes again.
            Navigation.Header = new TextBlock { Text = "关闭时部分资源清理失败；已继续清理其他资源。请再次关闭窗口退出。", TextWrapping = TextWrapping.Wrap };
            Navigation.AlwaysShowHeader = true;
            _closeReady = true;
        }
    }
}
