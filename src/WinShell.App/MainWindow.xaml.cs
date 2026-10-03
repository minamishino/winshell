using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinShell.App.Features.Connections;

namespace WinShell.App;

public sealed partial class MainWindow : Window
{
    private readonly ConnectionProfilesPage _connectionsPage;
    private Func<Task>? _shutdownAsync;
    private bool _closing, _closeReady;

    public MainWindow(ConnectionProfilesPage connectionsPage)
    {
        _connectionsPage = connectionsPage;
        InitializeComponent();
        Navigation.Content = connectionsPage;
        Navigation.SelectedItem = ConnectionsItem;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 820));
        AppWindow.Closing += Window_Closing;
    }

    public void SetShutdownHandler(Func<Task> shutdownAsync) => _shutdownAsync = shutdownAsync;

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
            _closing = false;
            // The window remains available to communicate shutdown failure.
            Navigation.Header = "关闭失败，请重试关闭窗口。";
        }
    }
}
