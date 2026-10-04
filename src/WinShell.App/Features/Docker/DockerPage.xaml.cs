using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.Core.Docker;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Docker;

public sealed partial class DockerPage : Page
{
    public DockerViewModel ViewModel { get; }
    public DockerPage(IDockerService service, IConnectionProfileRepository profiles, IKnownHostRepository knownHosts, NativeDialogService dialogs)
    {
        ViewModel = new(service, profiles, knownHosts, (title, content, token) => dialogs.ConfirmAsync(XamlRoot, title, content, "确认", token));
        InitializeComponent(); DataContext = ViewModel;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs args) => await ViewModel.LoadProfilesAsync();
    private async void Reload_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadProfilesAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await ViewModel.RefreshAsync();
    private async void Start_Click(object sender, RoutedEventArgs args) => await ViewModel.ChangeAsync(DockerContainerAction.Start);
    private async void Stop_Click(object sender, RoutedEventArgs args) => await ViewModel.ChangeAsync(DockerContainerAction.Stop);
    private async void Restart_Click(object sender, RoutedEventArgs args) => await ViewModel.ChangeAsync(DockerContainerAction.Restart);
    private async void Logs_Click(object sender, RoutedEventArgs args) => await ViewModel.LogsAsync();
    private async void Command_Click(object sender, RoutedEventArgs args) => await ViewModel.RunCommandAsync();
    private void Cancel_Click(object sender, RoutedEventArgs args) => ViewModel.Cancel();
    public Task BeginShutdownAsync() => ViewModel.DisposeAsync().AsTask();
}
