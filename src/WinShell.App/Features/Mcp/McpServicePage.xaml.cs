using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace WinShell.App.Features.Mcp;

public sealed partial class McpServicePage
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
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(ViewModel.LoadAsync);
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private void Owner_Changed() => DispatcherQueue.TryEnqueue(() => { if (!_closing) ViewModel.RefreshOwner(); });
    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(ViewModel.Settings): UpdateSettings(); break;
            case nameof(ViewModel.HasSaveFailure): RetrySave.Visibility = ViewModel.HasSaveFailure ? Visibility.Visible : Visibility.Collapsed; break;
        }
    }
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
    {
        try
        {
            if (TransportSelector.SelectedIndex >= 0)
                await ChangeAsync(ViewModel.Settings with { Transport = (McpServiceTransport)TransportSelector.SelectedIndex });
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Port_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        try
        {
            if (_updating)
                return;
            if (double.IsInteger(args.NewValue) && args.NewValue is >= 1024 and <= 65535)
                await ChangeAsync(ViewModel.Settings with { Port = (int)args.NewValue });
            else
                UpdateSettings();
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Sse_Toggled(object sender, RoutedEventArgs args)
    {
        try
        {
            await ChangeAsync(ViewModel.Settings with { EnableLegacySse = LegacySseSelector.IsOn });
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Start_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(ViewModel.StartAsync);
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Stop_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            if (_closing)
                return;
            await (_stopAction = ViewModel.StopAsync());
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Generate_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(ViewModel.GenerateTokenAsync);
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Rotate_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(async () =>
            {
                if (await _dialogs.ConfirmAsync(XamlRoot, "轮换 MCP HTTP 令牌？", "现有客户端配置需要更新。使用该配置的其他服务必须先在所属窗口停止。", "轮换令牌", _lifetime.Token) && !_closing)
                    await ViewModel.RotateTokenAsync();
            });
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void CopyToken_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(() => ViewModel.CopyTokenAsync(_copyText));
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void CopyConfiguration_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(() => ViewModel.CopyConfigurationAsync(false, _copyText));
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void CopySse_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(() => ViewModel.CopyConfigurationAsync(true, _copyText));
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Reload_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(ViewModel.LoadAsync);
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            await ExecuteAsync(ViewModel.SaveAsync);
        }
        catch (Exception)
        {
            ViewModel.ReportActionFailure();
        }
    }
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
        ViewModel.PropertyChanged -= Model_PropertyChanged;
        List<Exception> failures = [];
        try { await _lifetime.CancelAsync(); }
        catch (Exception error) { failures.Add(error); }
        var model = Task.CompletedTask;
        try { model = ViewModel.DisposeAsync().AsTask(); }
        catch (Exception error) { failures.Add(error); }
        try { await Task.WhenAll(_action, _stopAction, model); }
        catch (Exception error) { failures.Add(error); }
        finally { _lifetime.Dispose(); }
        var failure = failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("服务页面关闭失败，已等待所属操作结束。", failures)
        };
        if (failure is null) return;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
