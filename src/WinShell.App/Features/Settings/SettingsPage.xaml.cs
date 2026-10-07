using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.App.Features.Mcp;
using WinShell.Core.Settings;

namespace WinShell.App.Features.Settings;

public sealed partial class SettingsPage : Page
{
    private bool _updatingSelection;
    public SettingsViewModel ViewModel { get; }
    public SettingsPage(SettingsViewModel viewModel, McpPermissionsPage permissionsPage)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        PermissionsHost.Content = permissionsPage;
        ViewModel.PropertyChanged += Model_PropertyChanged;
        UpdateSelection();
    }
    public void ShowPermissions() => Sections.SelectedIndex = 1;
    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.Theme)) UpdateSelection();
    }
    private void UpdateSelection()
    {
        _updatingSelection = true;
        try { ThemeSelector.SelectedIndex = (int)ViewModel.Theme; }
        finally { _updatingSelection = false; }
    }
    private async void Theme_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingSelection && ThemeSelector.SelectedIndex >= 0)
            await ViewModel.SetThemeAsync((AppearanceTheme)ThemeSelector.SelectedIndex);
    }
    private async void Reload_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadAsync();
    private async void Save_Click(object sender, RoutedEventArgs args) => await ViewModel.SaveAsync();
    public Task BeginShutdownAsync()
    {
        IsEnabled = false;
        ViewModel.PropertyChanged -= Model_PropertyChanged;
        return ViewModel.DisposeAsync().AsTask();
    }
}
