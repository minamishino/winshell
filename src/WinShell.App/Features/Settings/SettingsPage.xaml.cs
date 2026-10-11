using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinShell.App.Features.Mcp;
using WinShell.Core.Settings;
namespace WinShell.App.Features.Settings;
public enum SettingsNavigationTarget { Connections, Files }
public sealed partial class SettingsPage : Page
{
    private sealed record Category(string Key, string Name, string Keywords);
    private static readonly Category[] AllCategories =
    [
        new("general", "常规", "启动 页面 默认"), new("appearance", "外观", "主题 浅色 深色 系统"),
        new("terminal", "终端", "字体 字号 光标 WSL PowerShell 默认 关闭 确认"), new("input", "输入", "粘贴 多行 换行 复制"),
        new("connections", "连接", "SSH 主机 端口 认证 密码 凭据 配置 SFTP FTP FTPS"),
        new("files", "文件", "传输 下载 上传 SFTP FTP FTPS"), new("mcp", "AI / MCP", "权限 stdio 授权 客户端 AI 服务")
    ];
    private bool _updatingSelection = true;
    private Action<SettingsNavigationTarget>? _navigate;
    public SettingsViewModel ViewModel { get; }
    public SettingsPage(SettingsViewModel viewModel, McpPermissionsPage permissionsPage)
    {
        ViewModel = viewModel; InitializeComponent(); DataContext = viewModel;
        PermissionsHost.Content = permissionsPage;
        Categories.ItemsSource = AllCategories; Categories.SelectedIndex = 0;
        ViewModel.PropertyChanged += Model_PropertyChanged;
        ConfigureNavigation(null); UpdateSelection(); UpdateRetry();
    }
    public void ConfigureNavigation(Action<SettingsNavigationTarget>? navigation)
    {
        _navigate = navigation;
        ConnectionsNavigation.IsEnabled = FilesNavigation.IsEnabled = navigation is not null;
    }
    public void SetMcpServiceContent(object? content)
    { McpServiceHost.Content = content; McpServiceHost.Visibility = content is null ? Visibility.Collapsed : Visibility.Visible; }
    public void ShowPermissions()
    { SettingsSearch.Text = ""; Categories.SelectedItem = AllCategories.Single(category => category.Key == "mcp"); }
    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModel.Settings)) UpdateSelection();
        if (args.PropertyName == nameof(ViewModel.HasSaveFailure)) UpdateRetry();
    }
    private void UpdateRetry() => RetrySave.Visibility = ViewModel.HasSaveFailure ? Visibility.Visible : Visibility.Collapsed;
    private void UpdateSelection()
    {
        _updatingSelection = true;
        try
        {
            var settings = ViewModel.Settings;
            ThemeSelector.SelectedIndex = (int)settings.Theme; StartupSelector.SelectedIndex = (int)settings.StartupPage;
            FontSelector.SelectedIndex = (int)settings.Terminal.FontFamily; FontSizeSelector.Value = settings.Terminal.FontSize;
            CursorSelector.SelectedIndex = (int)settings.Terminal.CursorStyle - 1;
            DefaultShellSelector.SelectedIndex = (int)settings.DefaultShell; WslDistributionText.Text = settings.WslDistribution;
            MultilineSwitch.IsOn = settings.ConfirmMultilinePaste; CloseSwitch.IsOn = settings.ConfirmTabClose;
        }
        finally { _updatingSelection = false; }
    }
    private async Task ChangeAsync(Func<ApplicationSettings, ApplicationSettings> update)
    {
        if (_updatingSelection) return;
        if (!ViewModel.CanEdit) { UpdateSelection(); return; }
        try { ValidationFeedback.Text = ""; await ViewModel.SetSettingsAsync(update(ViewModel.Settings)); }
        catch (ArgumentException error) { ValidationFeedback.Text = error.Message; UpdateSelection(); }
    }
    private async void Theme_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (ThemeSelector.SelectedIndex >= 0) await ChangeAsync(settings => settings with { Theme = (AppearanceTheme)ThemeSelector.SelectedIndex }); }
    private async void Startup_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (StartupSelector.SelectedIndex >= 0) await ChangeAsync(settings => settings with { StartupPage = (StartupPage)StartupSelector.SelectedIndex }); }
    private async void Font_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (FontSelector.SelectedIndex >= 0) await ChangeAsync(settings => settings with { Terminal = settings.Terminal with { FontFamily = (TerminalFontFamily)FontSelector.SelectedIndex } }); }
    private async void FontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingSelection) return;
        if (double.IsFinite(args.NewValue) && args.NewValue is >= 8 and <= 32)
            await ChangeAsync(settings => settings with { Terminal = settings.Terminal with { FontSize = (int)Math.Round(args.NewValue) } });
        UpdateSelection();
    }
    private async void Cursor_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (CursorSelector.SelectedIndex >= 0) await ChangeAsync(settings => settings with { Terminal = settings.Terminal with { CursorStyle = (TerminalCursorStyle)(CursorSelector.SelectedIndex + 1) } }); }
    private async void DefaultShell_SelectionChanged(object sender, SelectionChangedEventArgs args)
    { if (DefaultShellSelector.SelectedIndex >= 0) await ChangeAsync(settings => settings with { DefaultShell = (DefaultShell)DefaultShellSelector.SelectedIndex, WslDistribution = WslDistributionText.Text }); }
    private async void WslDistribution_LostFocus(object sender, RoutedEventArgs args)
    { if (!_updatingSelection && WslDistributionText.Text != ViewModel.Settings.WslDistribution) await ChangeAsync(settings => settings with { WslDistribution = WslDistributionText.Text }); }
    private async void WslDistribution_KeyDown(object sender, KeyRoutedEventArgs args)
    { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await ChangeAsync(settings => settings with { WslDistribution = WslDistributionText.Text }); } }
    private async void Multiline_Toggled(object sender, RoutedEventArgs args) => await ChangeAsync(settings => settings with { ConfirmMultilinePaste = MultilineSwitch.IsOn });
    private async void Close_Toggled(object sender, RoutedEventArgs args) => await ChangeAsync(settings => settings with { ConfirmTabClose = CloseSwitch.IsOn });
    private async void Reload_Click(object sender, RoutedEventArgs args) => await ViewModel.LoadAsync();
    private async void Save_Click(object sender, RoutedEventArgs args) => await ViewModel.SaveAsync();
    private void Connections_Click(object sender, RoutedEventArgs args) => _navigate?.Invoke(SettingsNavigationTarget.Connections);
    private void Files_Click(object sender, RoutedEventArgs args) => _navigate?.Invoke(SettingsNavigationTarget.Files);
    private void Search_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (Categories is null) return;
        var query = SettingsSearch.Text.Trim();
        var matches = AllCategories.Where(category => (category.Name + " " + category.Key + " " + category.Keywords).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        Categories.ItemsSource = matches; Categories.SelectedItem = matches.FirstOrDefault();
        NoResults.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Category_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var key = (Categories.SelectedItem as Category)?.Key;
        CategoryTitle.Text = (Categories.SelectedItem as Category)?.Name ?? "";
        GeneralPanel.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = key == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        TerminalPanel.Visibility = key == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        InputPanel.Visibility = key == "input" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPanel.Visibility = key == "connections" ? Visibility.Visible : Visibility.Collapsed;
        FilesPanel.Visibility = key == "files" ? Visibility.Visible : Visibility.Collapsed;
        McpPanel.Visibility = key == "mcp" ? Visibility.Visible : Visibility.Collapsed;
    }
    public Task BeginShutdownAsync()
    { IsEnabled = false; ConfigureNavigation(null); ViewModel.PropertyChanged -= Model_PropertyChanged; return ViewModel.DisposeAsync().AsTask(); }
}
