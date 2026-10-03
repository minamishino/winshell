using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using WinShell.App.Features.Connections;
using WinShell.Core.Credentials;
using WinShell.Core.Profiles;
using WinShell.Infrastructure.Windows.Credentials;
using WinShell.Infrastructure.Windows.Profiles;

namespace WinShell.App;

public partial class App : Application
{
    private IHost? _host;
    private Window? _mainWindow;

    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            builder.Services.AddSingleton<IConnectionProfileRepository>(_ => new JsonConnectionProfileRepository(GetConfigurationDirectory()));
            builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
            builder.Services.AddSingleton<ConnectionProfilesViewModel>();
            builder.Services.AddSingleton<ConnectionProfilesPage>();
            builder.Services.AddSingleton<MainWindow>();
            _host = builder.Build();
            await _host.StartAsync();
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.SetShutdownHandler(ShutdownAsync);
            _mainWindow = mainWindow;
            mainWindow.Activate();
        }
        catch (Exception)
        {
            _mainWindow = new Window { Title = "WinShell — 启动失败", Content = new Microsoft.UI.Xaml.Controls.TextBlock
                { Text = "应用初始化失败，请检查配置目录和 Windows 运行环境。", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap } };
            _mainWindow.Closed += StartupFailureWindow_Closed;
            _mainWindow.Activate();
        }
    }

    private async void StartupFailureWindow_Closed(object sender, WindowEventArgs args) => await ShutdownAsync();

    private async Task ShutdownAsync()
    {
        if (_host is null) return;
        await _host.StopAsync();
        if (_host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync();
        else _host.Dispose();
        _host = null;
    }

    private static string GetConfigurationDirectory()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinShell");
    }
}
