using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
namespace WinShell.App.Features.Mcp;

public sealed partial class McpPermissionsPage : Page
{
    private readonly NativeDialogService _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _action = Task.CompletedTask;
    private Task? _close;
    private bool _busy, _closing;
    public McpPermissionsViewModel ViewModel { get; }
    public McpClientLaunchViewModel ClientLaunch { get; } = new();
    public McpPermissionsPage(McpPermissionsViewModel viewModel, NativeDialogService dialogs)
    {
        ViewModel = viewModel; _dialogs = dialogs;
        InitializeComponent(); DataContext = viewModel;
        Loaded += Page_Loaded;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        ReloadClientConfiguration();
        await ExecuteAsync(ViewModel.LoadAsync);
    }
    private void ReloadClientConfiguration() => ClientLaunch.Reload(AppContext.BaseDirectory, RuntimeEnvironment.GetRuntimeDirectory());
    private void ReloadClientConfiguration_Click(object sender, RoutedEventArgs args)
    {
        if (!_closing) ReloadClientConfiguration();
    }
    private void CopyClientConfiguration_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        ReloadClientConfiguration();
        if (!ClientLaunch.IsAvailable) return;
        try
        {
            var content = new DataPackage();
            content.SetText(ClientLaunch.Json);
            Clipboard.SetContent(content);
            Clipboard.Flush();
            ClientLaunch.ReportCopySuccess();
        }
        catch (Exception) { ClientLaunch.ReportCopyFailure(); }
    }
    private async void Reload_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.LoadAsync);
    private async void Revoke_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.RevokeAllAsync);
    private async void Save_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(async () =>
    {
        if (ViewModel.Profiles.Any(row => row.RunScript || row.DockerRead || row.DockerControl || row.DockerExec) && !await _dialogs.ConfirmAsync(XamlRoot,
            "保存 AI 远端操作授权？", "Docker 授权覆盖所选 SSH 主机的全部容器：读取权限可能暴露日志中的敏感信息；控制权限可以启停或重启容器；执行权限允许容器内任意远端命令，可能通过主机挂载影响主机数据。任意 Shell 脚本权限本身也可调用 Docker，不受 Docker 工具权限限制。仅向你信任的本地 MCP 调用方授予所选权限。", "保存授权", _lifetime.Token)) return;
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
