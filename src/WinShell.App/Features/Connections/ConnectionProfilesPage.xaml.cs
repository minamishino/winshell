using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

public sealed partial class ConnectionProfilesPage
{
    public ConnectionProfilesViewModel ViewModel { get; }
    public event EventHandler<ConnectionProfile>? OpenProfileRequested;
    public event EventHandler<ConnectionProfile>? ConnectSshRequested;
    public event EventHandler<ConnectionProfile>? OpenFilesRequested;
    private readonly HashSet<Task> _pendingActions = [];
    private readonly List<Exception> _shutdownFailures = [];
    private readonly NativeDialogService _dialogs;
    private readonly CancellationTokenSource _dialogCancellation = new();
    private bool _loaded, _closing, _synchronizingSelection;
    private ConnectionProfileEditorDialog? _editorDialog;
    private Task? _shutdown;

    public ConnectionProfilesPage(ConnectionProfilesViewModel viewModel, NativeDialogService dialogs)
    {
        _dialogs = dialogs;
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        GroupedProfilesSource.Source = viewModel.ProfileGroups;
        ProfilesList.ItemsSource = GroupedProfilesSource.View;
        viewModel.PropertyChanged += ViewModel_Changed;
        Loaded += Page_Loaded;
        UpdateFeedback();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_loaded || _closing)
                return;
            _loaded = true;
            await RunActionAsync(() => ViewModel.LoadAsync(_dialogCancellation.Token));
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }

    private void ViewModel_Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (_closing) return;
        if (args.PropertyName is nameof(ConnectionProfilesViewModel.SelectedProfile) or nameof(ConnectionProfilesViewModel.ProfileGroups))
            SynchronizeSelection();
        UpdateFeedback();
    }

    private void SynchronizeSelection()
    {
        _synchronizingSelection = true;
        try { ProfilesList.SelectedItem = ViewModel.ProfileGroups.SelectMany(group => group).FirstOrDefault(row => row.Profile.Id == ViewModel.SelectedProfile?.Id); }
        finally { _synchronizingSelection = false; }
    }

    private void UpdateFeedback()
    {
        EmptyLibrary.Visibility = ViewModel.Profiles.Count == 0 && !ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        var searching = !string.IsNullOrWhiteSpace(ViewModel.SearchText);
        EmptyTitle.Text = searching ? "没有匹配的连接" : "还没有连接";
        EmptyDescription.Text = searching ? "换个关键词试试" : "新建连接";
        BusyProgress.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        OperationError.Message = ViewModel.ErrorMessage ?? "";
        OperationError.IsOpen = !string.IsNullOrEmpty(ViewModel.ErrorMessage);
    }

    // Dialogs and their asynchronous save continuations settle before Host disposal.
    private async Task RunActionAsync(Func<Task> action)
    {
        if (_closing || !ViewModel.CanEdit) return;
        var completion = new TaskCompletionSource();
        _pendingActions.Add(completion.Task);
        try { await action(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception exception)
        {
            if (_closing) _shutdownFailures.Add(exception);
            else ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
        finally { _pendingActions.Remove(completion.Task); completion.SetResult(); }
    }

    public Task BeginShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _closing = true;
        IsEnabled = false;
        Loaded -= Page_Loaded;
        ViewModel.PropertyChanged -= ViewModel_Changed;
        _dialogCancellation.Cancel();
        _shutdown = SettleActionsAsync();
        return _shutdown;
    }

    private async Task SettleActionsAsync()
    {
        await Task.WhenAll(_pendingActions.ToArray());
        _dialogCancellation.Dispose();
        if (_shutdownFailures.Count != 0) throw new AggregateException("关闭期间有页面操作未能完成。", _shutdownFailures);
    }

    private async Task EditAsync(ConnectionProfile? profile)
    {
        if (_editorDialog is not null || _closing) return;
        var dialog = new ConnectionProfileEditorDialog(ViewModel, profile, _dialogCancellation.Token);
        _editorDialog = dialog;
        try { await _dialogs.ShowAsync(XamlRoot, dialog, _dialogCancellation.Token); }
        finally
        {
            _editorDialog = null;
            if (!_closing) { SynchronizeSelection(); FocusProfiles(); }
        }
    }

    private static ConnectionProfile? RowProfile(object sender) => (sender as FrameworkElement)?.Tag as ConnectionProfile;
    private async void NewProfile_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await RunActionAsync(() => EditAsync(null));
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }
    private async void EditProfile_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (RowProfile(sender)is { } profile)
                await RunActionAsync(() => EditAsync(profile));
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }
    private async void Reload_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await RunActionAsync(() => ViewModel.LoadAsync(_dialogCancellation.Token));
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_closing && !_synchronizingSelection && ProfilesList.SelectedItem is ConnectionProfileListItemViewModel row)
            ViewModel.SelectedProfile = row.Profile;
    }

    private void ProfileRow_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid row) return;
        var compact = args.NewSize.Width < 540;
        if (row.FindName("OpenLabel") is TextBlock label) label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (row.FindName("ProtocolBadge") is Border badge) badge.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SearchToolbar_SizeChanged(object sender, SizeChangedEventArgs args) =>
        SearchInput.Width = Math.Max(0, Math.Min(460, args.NewSize.Width - 44));

    private void OpenRow_Click(object sender, RoutedEventArgs args)
    { if (RowProfile(sender) is { } profile) OpenProfile(profile); }

    private void OpenProfile(ConnectionProfile profile)
    {
        if (_closing || !ViewModel.CanEdit || _editorDialog is not null) return;
        ViewModel.SelectedProfile = profile;
        if (profile.Protocol == ConnectionProtocol.Ssh) ConnectSshRequested?.Invoke(this, profile);
        else OpenFilesRequested?.Invoke(this, profile);
    }

    private bool IsRowButton(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, ProfilesList))
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void ProfilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (IsRowButton(args.OriginalSource as DependencyObject)) return;
        var source = args.OriginalSource as DependencyObject;
        while (source is not null && !ReferenceEquals(source, ProfilesList))
        {
            if (source is ListViewItem { Content: ConnectionProfileListItemViewModel row })
            { args.Handled = true; OpenProfile(row.Profile); return; }
            source = VisualTreeHelper.GetParent(source);
        }
    }

    private void ProfilesList_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Enter || IsRowButton(args.OriginalSource as DependencyObject)) return;
        if (ProfilesList.SelectedItem is not ConnectionProfileListItemViewModel row) return;
        args.Handled = true;
        OpenProfile(row.Profile);
    }

    private void OpenDetails_Click(object sender, RoutedEventArgs args)
    {
        if (_closing || !ViewModel.CanEdit || RowProfile(sender) is not { } profile) return;
        ViewModel.SelectedProfile = profile;
        OpenProfileRequested?.Invoke(this, profile);
    }

    private async void CheckCredential_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (RowProfile(sender)is { } profile)
                await RunActionAsync(() => ViewModel.CheckCredentialAsync(profile.Id, _dialogCancellation.Token));
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }

    private async void DeleteCredential_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (RowProfile(sender)is not { } profile)
                return;
            await RunActionAsync(async () =>
            {
                if (await _dialogs.ConfirmAsync(XamlRoot, "删除此连接的凭据？", "连接配置将保留。此操作无法撤销。", "删除", _dialogCancellation.Token) && !_closing)
                    await ViewModel.DeleteCredentialAsync(profile.Id, _dialogCancellation.Token);
            });
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (RowProfile(sender)is not { } profile)
                return;
            await RunActionAsync(async () =>
            {
                if (await _dialogs.ConfirmAsync(XamlRoot, "删除此连接配置？", "将先删除此连接的凭据，再删除配置。此操作无法撤销。", "删除", _dialogCancellation.Token) && !_closing)
                    await ViewModel.DeleteProfileAsync(profile.Id, _dialogCancellation.Token);
            });
        }
        catch (Exception error)
        {
            if (_closing)
                _shutdownFailures.Add(error);
            else
                ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
    }

    public void FocusProfiles() => ProfilesList.Focus(FocusState.Programmatic);
}
