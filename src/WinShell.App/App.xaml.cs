using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WinShell.App.Features.Connections;
using WinShell.App.Features.Workspace;
using WinShell.App.Features.Ssh;
using WinShell.Core.Ssh;
using WinShell.Infrastructure.Ssh;
using WinShell.Infrastructure.Windows.Ssh;
using WinShell.Core.Credentials;
using WinShell.Core.Profiles;
using WinShell.Infrastructure.Windows.Credentials;
using WinShell.Infrastructure.Windows.Profiles;

namespace WinShell.App;

public partial class App : Application
{
    private IHost? _host;
    private Window? _mainWindow;
    private ConnectionProfilesViewModel? _profiles;
    private WorkspaceViewModel? _workspace;
    private ConnectionProfilesPage? _connectionsPage;
    private SshSessionsPage? _sshPage;
    private Task? _shutdown;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddSingleton<IConnectionProfileRepository>(_ => new JsonConnectionProfileRepository(GetConfigurationDirectory()));
            builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
            builder.Services.AddSingleton<ConnectionProfilesViewModel>();
            builder.Services.AddSingleton<WorkspaceViewModel>();
            builder.Services.AddSingleton<ConnectionProfilesPage>();
            builder.Services.AddSingleton<IKnownHostRepository>(_ => new JsonKnownHostRepository(GetConfigurationDirectory()));
            builder.Services.AddSingleton<ISshSessionFactory, SshSessionFactory>();
            builder.Services.AddSingleton<NativeDialogService>();
            builder.Services.AddSingleton<SshSessionsPage>();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();
            await _host.StartAsync();
            _profiles = _host.Services.GetRequiredService<ConnectionProfilesViewModel>();
            _workspace = _host.Services.GetRequiredService<WorkspaceViewModel>();
            _connectionsPage = _host.Services.GetRequiredService<ConnectionProfilesPage>();
            _sshPage = _host.Services.GetRequiredService<SshSessionsPage>();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.SetShutdownHandler(ShutdownAsync);
            _mainWindow = mainWindow;
            mainWindow.Activate();
        }
        catch (Exception)
        {
            var failureMessage = "应用初始化失败，请检查配置目录和 Windows 运行环境。";
            try { await ShutdownAsync(); }
            catch (Exception) { failureMessage += "部分资源清理失败，其他资源已尝试清理。"; }
            _mainWindow = new Window { Title = "WinShell — 启动失败", Content = new Microsoft.UI.Xaml.Controls.TextBlock
                { Text = failureMessage, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap } };
            _mainWindow.Activate();
        }
    }

    private Task ShutdownAsync() => _shutdown ??= ShutdownOwnersAsync();

    private async Task ShutdownOwnersAsync()
    {
        List<Exception> failures = [];
        // Start every owner before awaiting: a pending page load/save needs VM cancellation to settle.
        var windowActions = AttemptAsync(() => (_mainWindow as MainWindow)?.BeginShutdownAsync() ?? Task.CompletedTask);
        var pageActions = AttemptAsync(() => _connectionsPage?.BeginShutdownAsync() ?? Task.CompletedTask);
        var workspace = AttemptAsync(() => _workspace?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        var profiles = AttemptAsync(() => _profiles?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        var ssh = AttemptAsync(() => _sshPage?.BeginShutdownAsync() ?? Task.CompletedTask);
        await Task.WhenAll(windowActions, pageActions, workspace, profiles, ssh);
        if (_host is { } host)
        {
            await AttemptAsync(() => host.StopAsync());
            await AttemptAsync(async () =>
            {
                if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
                else host.Dispose();
            });
            _host = null;
        }
        if (failures.Count != 0) throw new AggregateException("关闭期间部分资源清理失败。", failures);

        async Task AttemptAsync(Func<Task> cleanup)
        {
            try { await cleanup(); }
            catch (Exception exception) { failures.Add(exception); }
        }
    }

    private static string GetConfigurationDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShell");
    }
}
