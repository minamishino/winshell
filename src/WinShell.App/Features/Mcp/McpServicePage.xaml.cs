using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace WinShell.App.Features.Mcp;

public sealed partial class McpServicePage : Page
{
    private readonly McpHttpProcessOwner _owner;
    private readonly NativeDialogService _dialogs;
    private readonly CancellationTokenSource _lifetime = new();
    private Task _action = Task.CompletedTask, _stopAction = Task.CompletedTask;
    private Task? _close;
    private bool _updating, _busy, _closing;
    private Action<string> _copyText = CopyToClipboard;
    public McpServiceViewModel ViewModel { get; }
    public McpClientLaunchViewModel ClientLaunch => ViewModel.ClientLaunch;
    public McpServicePage(McpServiceViewModel viewModel, McpHttpProcessOwner owner, NativeDialogService dialogs)
    {
        ViewModel = viewModel; _owner = owner; _dialogs = dialogs;
        InitializeComponent(); DataContext = ViewModel;
        ViewModel.PropertyChanged += Model_PropertyChanged; _owner.Changed += Owner_Changed;
        Loaded += Page_Loaded; ReloadClientConfiguration(); UpdateSettings();
    }
    private async void Page_Loaded(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.LoadAsync);
    private void Owner_Changed() => DispatcherQueue.TryEnqueue(() => { if (!_closing) ViewModel.RefreshOwner(); });
    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(ViewModel.Settings)) UpdateSettings(); if (args.PropertyName == nameof(ViewModel.HasSaveFailure)) RetrySave.Visibility = ViewModel.HasSaveFailure ? Visibility.Visible : Visibility.Collapsed; }
    private void UpdateSettings()
    {
        _updating = true;
        try
        {
            TransportSelector.SelectedIndex = (int)ViewModel.Settings.Transport;
            PortSelector.Value = ViewModel.Settings.Port; LegacySseSelector.IsOn = ViewModel.Settings.EnableLegacySse;
            var http = ViewModel.Settings.Transport == McpServiceTransport.StreamableHttp;
            HttpOptions.Visibility = TokenCard.Visibility = http ? Visibility.Visible : Visibility.Collapsed;
            StdioDescription.Visibility = StdioConfiguration.Visibility = http ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _updating = false; }
    }
    private void ReloadClientConfiguration()
    { ClientLaunch.Reload(AppContext.BaseDirectory, RuntimeEnvironment.GetRuntimeDirectory()); ViewModel.RefreshOwner(); }
    private async Task ChangeAsync(McpServiceSettings settings)
    {
        if (_updating) return;
        if (_busy || _closing || !ViewModel.CanEdit) { UpdateSettings(); return; }
        await ExecuteAsync(() => ViewModel.SetSettingsAsync(settings));
    }
    private async void Transport_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (TransportSelector.SelectedIndex >= 0) await ChangeAsync(ViewModel.Settings with { Transport = (McpServiceTransport)TransportSelector.SelectedIndex }); }
    private async void Port_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updating) return;
        if (double.IsFinite(args.NewValue) && args.NewValue is >= 1024 and <= 65535 && args.NewValue == Math.Truncate(args.NewValue))
            await ChangeAsync(ViewModel.Settings with { Port = (int)args.NewValue });
        else UpdateSettings();
    }
    private async void Sse_Toggled(object sender, RoutedEventArgs args) => await ChangeAsync(ViewModel.Settings with { EnableLegacySse = LegacySseSelector.IsOn });
    private async void Start_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.StartAsync);
    private async void Stop_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        try { await (_stopAction = ViewModel.StopAsync()); }
        catch (Exception) { ViewModel.ReportActionFailure(); }
    }
    private async void Generate_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.GenerateTokenAsync);
    private async void Rotate_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(async () =>
    {
        if (await _dialogs.ConfirmAsync(XamlRoot, "轮换 MCP HTTP 令牌？", "现有客户端配置需要更新。使用该配置的其他服务必须先在所属窗口停止。", "轮换令牌", _lifetime.Token) && !_closing)
            await ViewModel.RotateTokenAsync();
    });
    private async void CopyToken_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(() => ViewModel.CopyTokenAsync(_copyText));
    private async void CopyConfiguration_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(() => ViewModel.CopyConfigurationAsync(false, _copyText));
    private async void CopySse_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(() => ViewModel.CopyConfigurationAsync(true, _copyText));
    private async void Reload_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.LoadAsync);
    private async void Save_Click(object sender, RoutedEventArgs args) => await ExecuteAsync(ViewModel.SaveAsync);
    private void ReloadClientConfiguration_Click(object sender, RoutedEventArgs args) { if (!_closing && !_busy) ReloadClientConfiguration(); }
    private Task ExecuteAsync(Func<Task> operation)
    {
        if (_busy || _closing) return Task.CompletedTask;
        _busy = true;
        return _action = RunAsync();
        async Task RunAsync()
        {
            try { await operation(); }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception) { ViewModel.ReportActionFailure(); }
            finally { _busy = false; }
        }
    }
    private static void CopyToClipboard(string text)
    { var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content); Clipboard.Flush(); }
    public Task BeginShutdownAsync() => _close ??= CloseAsync();
    private async Task CloseAsync()
    {
        _closing = true; IsEnabled = false; Loaded -= Page_Loaded; _owner.Changed -= Owner_Changed;
        ViewModel.PropertyChanged -= Model_PropertyChanged; _lifetime.Cancel();
        var model = ViewModel.DisposeAsync().AsTask();
        try { await Task.WhenAll(_action, _stopAction, model); }
        finally { _lifetime.Dispose(); }
    }
}
