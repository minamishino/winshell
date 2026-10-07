using WinShell.App.Features.Mcp;
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

public sealed partial class MainWindow : Window
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
    private readonly HashSet<Task> _pendingTabActions = [];
    private Func<Task>? _shutdownAsync;
    private bool _closing, _closeReady;

    public MainWindow(ConnectionProfilesPage connectionsPage, WorkspaceViewModel workspace, SshSessionsPage sshPage, FilesPage filesPage, McpPermissionsPage mcpPage, TunnelsPage tunnelsPage, DockerPage dockerPage, TerminalSessionsPage terminalPage, NativeDialogService dialogs)
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
        _dialogs.PresentationChanged = _terminalPage.SetDialogVisible;
        _filesPage = filesPage;
        _tunnelsPage = tunnelsPage;
        InitializeComponent();
        ConnectionsHost.Content = connectionsPage;
        SshHost.Content = sshPage;
        TerminalHost.Content = terminalPage;
        FilesHost.Content = filesPage;
        TunnelsHost.Content = tunnelsPage;
        filesPage.SetOwnerWindow(this);
        McpHost.Content = mcpPage;
        DockerHost.Content = dockerPage;
        WorkspaceHost.DataContext = workspace;
        Navigation.SelectedItem = ConnectionsItem;
        _connectionsPage.ConnectSshRequested += Connections_ConnectSshRequested;
        _connectionsPage.OpenProfileRequested += Connections_OpenProfileRequested;
        _connectionsPage.ViewModel.ProfileDeleted += Connections_ProfileDeleted;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
        AppWindow.Closing += Window_Closing;
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
        if (_closing) return;
        Navigation.SelectedItem = SshItem;
        await _terminalPage.ConnectSavedProfileAsync(profile);
    }

    private async void Connections_ProfileDeleted(object? sender, Guid profileId)
    {
        if (_closing) return;
        _ = _sshPage.CloseProfileAsync(profileId);
        _ = _terminalPage.CloseProfileAsync(profileId);
        _ = _filesPage.CloseProfileAsync(profileId);
        var tab = _workspace.Tabs.FirstOrDefault(item => item.ProfileId == profileId);
        if (tab is not null) _ = CloseDetailsAsync(tab.TabId);
        try {await _tunnelsPage.CloseProfileAsync(profileId);}
        catch(Exception) { /* The page displays and retains the first cleanup failure for shutdown. */ }
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ConnectionsHost is null || WorkspaceHost is null || SshHost is null || FilesHost is null || TunnelsHost is null || McpHost is null || DockerHost is null || TerminalHost is null) return;
        var showWorkspace = ReferenceEquals(args.SelectedItem, WorkspaceItem);
        var showTunnels = ReferenceEquals(args.SelectedItem, TunnelsItem);
        var showFiles = ReferenceEquals(args.SelectedItem, FilesItem);
        var showMcp = ReferenceEquals(args.SelectedItem, McpItem);
        var showTerminal = ReferenceEquals(args.SelectedItem, SshItem);
        TerminalHost.Visibility = showTerminal ? Visibility.Visible : Visibility.Collapsed;
        _terminalPage.SetWorkspaceVisible(showTerminal);
        McpHost.Visibility = showMcp ? Visibility.Visible : Visibility.Collapsed;
        var showDocker = ReferenceEquals(args.SelectedItem, DockerItem);
        var cancelDocker = !_dockerHandoffNavigation ? _dockerPage.ViewModel.CancelPendingAsync() : Task.CompletedTask;
        DockerHost.Visibility = showDocker ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsHost.Visibility = showWorkspace || showTerminal || showFiles || showTunnels || showMcp || showDocker ? Visibility.Collapsed : Visibility.Visible;
        SshHost.Visibility = Visibility.Collapsed;
        WorkspaceHost.Visibility = showWorkspace ? Visibility.Visible : Visibility.Collapsed;
        FilesHost.Visibility = showFiles ? Visibility.Visible : Visibility.Collapsed;
        TunnelsHost.Visibility = showTunnels ? Visibility.Visible : Visibility.Collapsed;
        await cancelDocker;
        if (showTunnels && !_closing) await _tunnelsPage.LoadAsync();
        if (showWorkspace) FocusSelectedTab();
        if (showFiles && !_closing) await _filesPage.LoadProfilesAsync();
    }

    private void Workspace_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_closing && args.PropertyName == nameof(WorkspaceViewModel.SelectedTab)) UpdateWorkspaceState();
    }

    private void UpdateWorkspaceState() => EmptyWorkspace.Visibility = _workspace.Tabs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void DetailsTabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (!_closing && args.Item is ProfileTabViewModel tab) await CloseDetailsAsync(tab.TabId);
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
        catch (Exception) { Navigation.Header = "详情标签清理失败，已尝试释放资源。"; }
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
        if (sender is not TabViewItem tab || tab.DataContext is not ProfileTabViewModel viewModel) return;
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
        Navigation.IsEnabled = false;
        _connectionsPage.ConnectSshRequested -= Connections_ConnectSshRequested;
        _connectionsPage.OpenProfileRequested -= Connections_OpenProfileRequested;
        _connectionsPage.ViewModel.ProfileDeleted -= Connections_ProfileDeleted;
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
        _dialogs.PresentationChanged = null;
        _dockerPage.OpenTerminalAsync = null;
        _terminalPage.ViewModel.PropertyChanged -= Terminal_PropertyChanged;
        _dockerPage.ViewModel.Cancel();
        return Task.WhenAll(_pendingTabActions.ToArray());
    }

    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeReady) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            if (_shutdownAsync is not null) await _shutdownAsync();
            _closeReady = true;
            AppWindow.Closing -= Window_Closing;
            Close();
        }
        catch (Exception)
        {
            // All cleanup owners have been attempted. Keep this failure visible until the user closes again.
            Navigation.Header = "关闭时部分资源清理失败；已继续清理其他资源。请再次关闭窗口退出。";
            _closeReady = true;
        }
    }
}
