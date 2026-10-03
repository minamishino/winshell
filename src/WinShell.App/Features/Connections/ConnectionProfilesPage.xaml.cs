using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

public sealed partial class ConnectionProfilesPage : Page
{
    public ConnectionProfilesViewModel ViewModel { get; }
    private ConnectionProfileEditorViewModel _editor = new(null);
    private Guid? _savedProfileId;
    private bool _loaded;

    public ConnectionProfilesPage(ConnectionProfilesViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        ProtocolBox.ItemsSource = new[] { new Choice<ConnectionProtocol>(ConnectionProtocol.Ssh, "SSH"), new(ConnectionProtocol.Ftp, "FTP"), new(ConnectionProtocol.Ftps, "FTPS") };
        AuthenticationBox.ItemsSource = new[] { new Choice<AuthenticationMethod>(AuthenticationMethod.Password, "密码"), new(AuthenticationMethod.PrivateKey, "私钥") };
        SetEditor(null);
        Loaded += Page_Loaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        await ViewModel.LoadAsync(CancellationToken.None);
    }
    private void SetEditor(ConnectionProfile? profile)
    {
        SecretInput.Password = "";
        _editor = new(profile);
        EditorPanel.DataContext = _editor;
        _savedProfileId = profile?.Id;
        EditorHeading.Text = profile is null ? "新建连接" : "编辑连接";
        DeleteProfileButton.IsEnabled = SaveCredentialButton.IsEnabled = CheckCredentialButton.IsEnabled = DeleteCredentialButton.IsEnabled = profile is not null;
    }
    private void NewProfile_Click(object sender, RoutedEventArgs args)
    {
        ViewModel.SelectedProfile = null;
        SetEditor(null);
    }
    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (ProfilesList.SelectedItem is ConnectionProfile profile) SetEditor(profile);
    }
    private async void Reload_Click(object sender, RoutedEventArgs args)
    {
        await ViewModel.LoadAsync(CancellationToken.None);
        if (ViewModel.ErrorMessage is null) SetEditor(ViewModel.SelectedProfile);
    }
    private async void SaveProfile_Click(object sender, RoutedEventArgs args)
    {
        ConnectionProfile profile;
        try { profile = _editor.BuildProfile(); }
        catch (ArgumentException exception) { ViewModel.ReportValidationError(exception.Message); return; }
        await ViewModel.SaveProfileAsync(profile, CancellationToken.None);
        if (ViewModel.ErrorMessage is null && ViewModel.SelectedProfile?.Id == profile.Id) SetEditor(profile);
    }
    private async Task<bool> ConfirmDeletionAsync(string title, string content)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content,
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async void DeleteProfile_Click(object sender, RoutedEventArgs args)
    {
        if (_savedProfileId is not Guid profileId || !await ConfirmDeletionAsync("删除此连接配置？", "将先删除此连接的凭据，再删除配置。此操作无法撤销。")) return;
        await ViewModel.DeleteProfileAsync(profileId, CancellationToken.None);
        if (ViewModel.ErrorMessage is null) SetEditor(null);
    }
    private async void SaveCredential_Click(object sender, RoutedEventArgs args)
    {
        if (_savedProfileId is not Guid profileId) return;
        byte[]? secretBytes = null;
        try
        {
            // PasswordBox necessarily exposes a transient string; never copy it to a persistent VM property.
            var password = SecretInput.Password;
            SecretInput.Password = "";
            if (Encoding.UTF8.GetByteCount(password) > 2560)
            { ViewModel.ReportValidationError("凭据的 UTF-8 长度不能超过 2560 字节。"); return; }
            secretBytes = Encoding.UTF8.GetBytes(password);
            password = "";
            await ViewModel.SaveCredentialAsync(profileId, secretBytes, CancellationToken.None);
        }
        finally { if (secretBytes is not null) CryptographicOperations.ZeroMemory(secretBytes); }
    }
    private async void CheckCredential_Click(object sender, RoutedEventArgs args)
    {
        if (_savedProfileId is Guid profileId) await ViewModel.CheckCredentialAsync(profileId, CancellationToken.None);
    }
    private async void DeleteCredential_Click(object sender, RoutedEventArgs args)
    {
        if (_savedProfileId is not Guid profileId || !await ConfirmDeletionAsync("删除此连接的凭据？", "连接配置将保留。此操作无法撤销。")) return;
        SecretInput.Password = "";
        await ViewModel.DeleteCredentialAsync(profileId, CancellationToken.None);
    }

    private sealed record Choice<T>(T Value, string Label);
}
