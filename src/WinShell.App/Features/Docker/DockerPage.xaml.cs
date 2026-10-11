using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.Core.Docker;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Docker;

public sealed partial class DockerPage : Page
{
    public DockerViewModel ViewModel { get; }
    public Func<DockerTerminalRequest, CancellationToken, Task<bool>>? OpenTerminalAsync { get; set; }
    private Task? _profileLoad;
    private int _profileSelectionGeneration;
    private bool _closing;
    public DockerPage(IDockerService service, IConnectionProfileRepository profiles, IKnownHostRepository knownHosts, NativeDialogService dialogs)
    {
        ViewModel = new(service, profiles, knownHosts, (title, content, token) => dialogs.ConfirmAsync(XamlRoot, title, content, "确认", token));
        InitializeComponent(); DataContext = ViewModel;
    }
    private async void Page_Loaded(object sender, RoutedEventArgs args)
    { if (_profileLoad is null) await LoadProfilesAsync(); }
    private async void Reload_Click(object sender, RoutedEventArgs args) => await LoadProfilesAsync();
    private async Task LoadProfilesAsync()
    {
        if (_profileLoad is { IsCompleted: false }) await _profileLoad;
        if (_closing) return;
        _profileLoad = ViewModel.LoadProfilesAsync();
        await _profileLoad;
    }
    public async Task SelectProfileAsync(Guid? preferredProfileId)
    {
        var generation = ++_profileSelectionGeneration;
        await ViewModel.CancelPendingAsync();
        if (_closing || generation != _profileSelectionGeneration) return;
        await LoadProfilesAsync();
        if (!_closing && generation == _profileSelectionGeneration && preferredProfileId is Guid profileId && ViewModel.Profiles.FirstOrDefault(profile => profile.Id == profileId) is { } profile)
            ViewModel.SelectedProfile = profile;
    }
    private async void Refresh_Click(object sender, RoutedEventArgs args) => await ViewModel.RefreshAsync();
    private async void Start_Click(object sender, RoutedEventArgs args) => await ViewModel.ChangeAsync(DockerContainerAction.Start);
    private async void Stop_Click(object sender, RoutedEventArgs args) => await ViewModel.ChangeAsync(DockerContainerAction.Stop);
    private async void Restart_Click(object sender, RoutedEventArgs args) => await ViewModel.ChangeAsync(DockerContainerAction.Restart);
    private async void Logs_Click(object sender, RoutedEventArgs args) => await ViewModel.LogsAsync();
    private async void Command_Click(object sender, RoutedEventArgs args) => await ViewModel.RunCommandAsync();
    private async void Terminal_Click(object sender, RoutedEventArgs args)
    { if (OpenTerminalAsync is { } dispatch) await ViewModel.OpenTerminalAsync(dispatch); }
    private void Cancel_Click(object sender, RoutedEventArgs args) => ViewModel.Cancel();
    public Task BeginShutdownAsync() { _closing = true; _profileSelectionGeneration++; return ViewModel.DisposeAsync().AsTask(); }
}
