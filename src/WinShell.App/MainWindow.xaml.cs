using System.ComponentModel;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinShell.App.Features.Connections;
using WinShell.App.Features.Workspace;
using WinShell.Core.Profiles;

namespace WinShell.App;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionProfilesPage _connectionsPage;
    private readonly WorkspaceViewModel _workspace;
    private readonly HashSet<Task> _pendingTabActions = [];
    private Func<Task>? _shutdownAsync;
    private bool _closing, _closeReady;

    public MainWindow(ConnectionProfilesPage connectionsPage, WorkspaceViewModel workspace)
    {
        _connectionsPage = connectionsPage;
        _workspace = workspace;
        InitializeComponent();
        ConnectionsHost.Content = connectionsPage;
        WorkspaceHost.DataContext = workspace;
        Navigation.SelectedItem = ConnectionsItem;
        _connectionsPage.OpenProfileRequested += Connections_OpenProfileRequested;
        _connectionsPage.ViewModel.ProfileDeleted += Connections_ProfileDeleted;
        _workspace.PropertyChanged += Workspace_PropertyChanged;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
        AppWindow.Closing += Window_Closing;
    }

    public void SetShutdownHandler(Func<Task> shutdownAsync) => _shutdownAsync = shutdownAsync;

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

    private void Connections_ProfileDeleted(object? sender, Guid profileId)
    {
        if (_closing) return;
        var tab = _workspace.Tabs.FirstOrDefault(item => item.ProfileId == profileId);
        if (tab is not null) _ = CloseDetailsAsync(tab.TabId);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (ConnectionsHost is null || WorkspaceHost is null) return;
        var showWorkspace = ReferenceEquals(args.SelectedItem, WorkspaceItem);
        ConnectionsHost.Visibility = showWorkspace ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceHost.Visibility = showWorkspace ? Visibility.Visible : Visibility.Collapsed;
        if (showWorkspace) FocusSelectedTab();
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
        _connectionsPage.OpenProfileRequested -= Connections_OpenProfileRequested;
        _connectionsPage.ViewModel.ProfileDeleted -= Connections_ProfileDeleted;
        _workspace.PropertyChanged -= Workspace_PropertyChanged;
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
