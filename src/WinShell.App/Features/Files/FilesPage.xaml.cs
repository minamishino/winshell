using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;
using WinShell.Core.Files;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.App.Features.Files;

public sealed partial class FilesPage : Page
{
    private readonly IConnectionProfileRepository _profiles;
    private readonly NativeDialogService _dialogs;
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _stop = new();
    private WindowId _windowId;
    private bool _closing, _acting;
    private Task _action = Task.CompletedTask;
    private Task? _shutdown;
    public FilesViewModel ViewModel { get; }
    public FilesPage(ISftpSessionFactory factory, IKnownHostRepository knownHosts, IConnectionProfileRepository profiles, NativeDialogService dialogs)
    {
        _profiles = profiles; _dialogs = dialogs;
        ViewModel = new(factory, knownHosts, ConfirmAsync);
        InitializeComponent(); DataContext = ViewModel;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(100); _timer.Tick += Tick;
        _timer.Start();
    }
    public void SetOwnerWindow(Window window) => _windowId = window.AppWindow.Id;
    private void Tick(DispatcherQueueTimer sender, object args) => ViewModel.PublishProgress();
    private Task<bool> ConfirmAsync(SftpConnectResult.UnknownHostKey challenge, CancellationToken token) =>
        _dialogs.ConfirmAsync(XamlRoot, "确认 SFTP 主机公钥", $"主机：{challenge.Endpoint.Host}:{challenge.Endpoint.Port}\n公钥类型：{challenge.Key.Algorithm}\n完整指纹：{challenge.Key.Fingerprint}\n\n尚未认证。请通过可信渠道核实指纹，再选择信任并连接。", "信任并连接", token);
    public Task LoadProfilesAsync() => RunAsync(async () =>
    {
        if (!ViewModel.CanConnect) return;
        var saved = await _profiles.ReadAsync(_stop.Token);
        _stop.Token.ThrowIfCancellationRequested();
        Profiles.ItemsSource = saved.Profiles.Where(profile => profile.Protocol == ConnectionProtocol.Ssh).ToArray();
        Profiles.SelectedIndex = Profiles.Items.Count == 0 ? -1 : 0;
        Feedback.Text = Profiles.Items.Count == 0 ? "请先在连接配置页保存 SSH 配置。" : "";
    });
    private async void Reload_Click(object sender, RoutedEventArgs args) => await LoadProfilesAsync();
    private async void Connect_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    { if (Profiles.SelectedItem is ConnectionProfile profile) await ViewModel.ConnectAsync(profile); else Feedback.Text = "请选择已保存的 SSH 配置。"; });
    private async void Disconnect_Click(object sender, RoutedEventArgs args) => await RunAsync(ViewModel.DisconnectAsync);
    private async void Go_Click(object sender, RoutedEventArgs args) => await RunAsync(() => ViewModel.BrowseAsync(ViewModel.Path));
    private async void Up_Click(object sender, RoutedEventArgs args) => await RunAsync(ViewModel.UpAsync);
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await RunAsync(ViewModel.RefreshAsync);
    private void Cancel_Click(object sender, RoutedEventArgs args) => ViewModel.Cancel();
    private async void Path_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == VirtualKey.Enter) { args.Handled = true; await RunAsync(() => ViewModel.BrowseAsync(ViewModel.Path)); } }
    private async void Entries_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == VirtualKey.Enter) { args.Handled = true; await OpenSelectedAsync(); } }
    private async void Entries_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args) => await OpenSelectedAsync();
    private async void Open_Click(object sender, RoutedEventArgs args) => await OpenSelectedAsync();
    private Task OpenSelectedAsync() => RunAsync(async () =>
    { if (Entries.SelectedItem is RemoteFileEntry { IsDirectory: true } entry) await ViewModel.BrowseAsync(entry.FullPath); else Feedback.Text = "请选择目录。"; });
    private async void Download_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        if (Entries.SelectedItem is not RemoteFileEntry { IsRegularFile: true } entry) { Feedback.Text = "请选择普通文件。"; return; }
        // Windows App SDK 2.x returns a path without creating a placeholder file.
        // Never feed an untrusted remote filename into a Windows path or picker suggestion.
        var picker = new FileSavePicker(_windowId) { SuggestedFileName = "download", ShowOverwritePrompt = false, CommitButtonText = "选择下载路径" };
        var selected = await picker.PickSaveFileAsync().AsTask(_stop.Token);
        _stop.Token.ThrowIfCancellationRequested();
        if (selected is not null) await ViewModel.DownloadAsync(entry, selected.Path);
    });
    private async void Upload_Click(object sender, RoutedEventArgs args) => await RunAsync(async () =>
    {
        var selected = await new FileOpenPicker(_windowId).PickSingleFileAsync().AsTask(_stop.Token);
        _stop.Token.ThrowIfCancellationRequested();
        if (selected is not null) await ViewModel.UploadAsync(selected.Path, string.IsNullOrWhiteSpace(UploadName.Text) ? System.IO.Path.GetFileName(selected.Path) : UploadName.Text);
    });
    public Task CloseProfileAsync(Guid profileId) => ViewModel.ConnectedProfileId == profileId ? CancelAndDisconnectAsync() : Task.CompletedTask;
    private async Task CancelAndDisconnectAsync() { ViewModel.Cancel(); await _action; if (!_closing) await RunAsync(ViewModel.DisconnectAsync); }
    private Task RunAsync(Func<Task> action)
    {
        if (_closing || _acting) return _action;
        _acting = true; Feedback.Text = "";
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); _action = completion.Task;
        _ = CompleteAsync(); return completion.Task;
        async Task CompleteAsync()
        {
            try { await action(); }
            catch (OperationCanceledException) { if (!_closing) Feedback.Text = "操作已取消。"; }
            catch (Exception error) { Feedback.Text = error is ArgumentException or IOException or InvalidOperationException ? error.Message : "SFTP 页面操作失败。"; }
            finally { _acting = false; completion.SetResult(); }
        }
    }
    public Task BeginShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _closing = true; IsEnabled = false; _timer.Stop(); _timer.Tick -= Tick; _stop.Cancel();
        _shutdown = CloseAsync(); return _shutdown;
    }
    private async Task CloseAsync()
    {
        var closing = ViewModel.DisposeAsync().AsTask();
        try { await Task.WhenAll(_action, closing); }
        finally { _stop.Dispose(); }
    }
}

public sealed class DirectoryLabelConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "目录" : "文件 / 其他";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}