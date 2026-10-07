using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace WinShell.App.Features.Mcp;

public sealed partial class McpPermissionsPage : Page
{
    private readonly NativeDialogService _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _action = Task.CompletedTask;
    private Task? _close;
    private bool _busy, _closing;
    public McpPermissionsViewModel ViewModel { get; }
    public McpPermissionsPage(McpPermissionsViewModel viewModel, NativeDialogService dialogs)
    {
        ViewModel = viewModel; _dialogs = dialogs;
        InitializeComponent(); DataContext = viewModel;
        Loaded += Page_Loaded;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.LoadAsync);
    private async void Reload_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.LoadAsync);
    private async void Revoke_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.RevokeAllAsync);
    private async void Save_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(async () =>
    {
        if (ViewModel.Profiles.Any(row => row.RunScript) && !await _dialogs.ConfirmAsync(XamlRoot,
            "允许 AI 执行远端脚本？", "任意 Shell 脚本可以修改或删除所选 SSH 账户有权访问的数据。仅向你信任的本地 MCP 调用方授予此权限。", "保存授权", _lifetime.Token)) return;
        if (!_closing) await ViewModel.SaveAsync();
    });
    private Task ExecuteAsync(Func<Task> operation)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true; IsEnabled = false;
        return _action = RunAsync();
        async Task RunAsync()
        {
            try { await operation(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception) { ViewModel.ReportDialogFailure(); }
            finally { _busy = false; if (!_closing) IsEnabled = true; }
        }
    }
    public Task BeginShutdownAsync() => _close ??= CloseAsync();
    private async Task CloseAsync()
    {
        _closing = true; Loaded -= Page_Loaded; IsEnabled = false;
        _lifetime.Cancel();
        var modelClose = ViewModel.DisposeAsync().AsTask();
        try { await Task.WhenAll(_action, modelClose); } finally { _lifetime.Dispose(); }
    }
}
