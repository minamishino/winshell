using WinShell.App.Features.Settings;
using WinShell.Core.Settings;

namespace WinShell.App;

public sealed partial class MainWindow
{
    internal void ApplyStartupPage() => Navigation.SelectedItem =
        _settingsPage.ViewModel.Settings.StartupPage == StartupPage.Connections ? ConnectionsItem : SshItem;

    internal void ConfigureSettingsNavigation() => _settingsPage.ConfigureNavigation(target =>
    {
        if (_closing) return;
        Navigation.SelectedItem = target == SettingsNavigationTarget.Connections ? ConnectionsItem : FilesItem;
    });
}
