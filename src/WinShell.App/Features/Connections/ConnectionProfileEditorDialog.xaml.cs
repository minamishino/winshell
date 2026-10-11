using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

public sealed partial class ConnectionProfileEditorDialog : ContentDialog
{
    private readonly ConnectionProfilesViewModel _profiles;
    private readonly ConnectionProfileEditorViewModel _editor;
    private readonly CancellationToken _cancellation;
    private XamlRoot? _dialogRoot;
    private bool _updatingSelectors = true, _saving;

    public ConnectionProfileEditorDialog(ConnectionProfilesViewModel profiles, ConnectionProfile? profile, CancellationToken cancellation)
    {
        _profiles = profiles;
        _editor = new(profile);
        _cancellation = cancellation;
        InitializeComponent();
        Title = profile is null ? "新建连接" : "编辑连接";
        EditorPanel.DataContext = _editor;
        ProtocolBox.SelectedItem = _editor.Protocol switch
        {
            ConnectionProtocol.Ftp => FtpProtocolItem,
            ConnectionProtocol.Ftps => FtpsProtocolItem,
            _ => SshProtocolItem
        };
        FtpsModeBox.SelectedItem = _editor.FtpsMode == FtpsMode.Implicit ? ImplicitFtpsItem : ExplicitFtpsItem;
        SelectAuthentication();
        _updatingSelectors = false;
        UpdateAuthentication();
    }

    private void Dialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _dialogRoot = XamlRoot;
        _dialogRoot.Changed += Root_Changed;
        UpdateEditorSize(_dialogRoot);
        DisplayNameInput.Focus(FocusState.Programmatic);
    }

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateEditorSize(sender);
    private void UpdateEditorSize(XamlRoot root)
    {
        EditorScroll.Width = Math.Max(0, Math.Min(624, root.Size.Width - 80));
        EditorScroll.MaxHeight = Math.Max(0, root.Size.Height - 180);
    }

    private void EditorPanel_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var narrow = args.NewSize.Width < 480;
        Reflow(IdentityFields, GroupInput, narrow);
        Reflow(ConnectionFields, UserNameInput, narrow);
        Reflow(AuthenticationFields, SecretInput, narrow);
    }

    private static void Reflow(Grid grid, FrameworkElement second, bool narrow)
    {
        grid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(second, narrow ? 0 : 1);
        Grid.SetRow(second, narrow ? 1 : 0);
    }

    private int DefaultPort => _editor.Protocol == ConnectionProtocol.Ssh ? 22
        : _editor.Protocol == ConnectionProtocol.Ftps && _editor.FtpsMode == FtpsMode.Implicit ? 990 : 21;

    private void Protocol_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingSelectors || _saving) return;
        var previousPort = DefaultPort;
        _editor.Protocol = ReferenceEquals(ProtocolBox.SelectedItem, FtpProtocolItem) ? ConnectionProtocol.Ftp
            : ReferenceEquals(ProtocolBox.SelectedItem, FtpsProtocolItem) ? ConnectionProtocol.Ftps : ConnectionProtocol.Ssh;
        if (_editor.PortText == previousPort.ToString()) _editor.PortText = DefaultPort.ToString();
        if ((_editor.Protocol == ConnectionProtocol.Ssh && _editor.AuthenticationMethod == AuthenticationMethod.Anonymous) ||
            (_editor.Protocol != ConnectionProtocol.Ssh && _editor.AuthenticationMethod == AuthenticationMethod.PrivateKey))
        {
            _editor.AuthenticationMethod = AuthenticationMethod.Password;
            SecretInput.Password = "";
            SelectAuthentication();
        }
        UpdateAuthentication();
    }

    private void FtpsMode_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingSelectors || _saving) return;
        var previousPort = DefaultPort;
        _editor.FtpsMode = ReferenceEquals(FtpsModeBox.SelectedItem, ImplicitFtpsItem) ? FtpsMode.Implicit : FtpsMode.Explicit;
        if (_editor.PortText == previousPort.ToString()) _editor.PortText = DefaultPort.ToString();
    }

    private void SelectAuthentication()
    {
        var updating = _updatingSelectors;
        _updatingSelectors = true;
        AuthenticationBox.SelectedItem = _editor.AuthenticationMethod switch
        {
            AuthenticationMethod.PrivateKey => PrivateKeyAuthenticationItem,
            AuthenticationMethod.Anonymous => AnonymousAuthenticationItem,
            _ => PasswordAuthenticationItem
        };
        _updatingSelectors = updating;
    }

    private void Authentication_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingSelectors || _saving) return;
        _editor.AuthenticationMethod = ReferenceEquals(AuthenticationBox.SelectedItem, PrivateKeyAuthenticationItem) ? AuthenticationMethod.PrivateKey
            : ReferenceEquals(AuthenticationBox.SelectedItem, AnonymousAuthenticationItem) ? AuthenticationMethod.Anonymous : AuthenticationMethod.Password;
        SecretInput.Password = "";
        UpdateAuthentication();
    }

    private void UpdateAuthentication()
    {
        var ssh = _editor.Protocol == ConnectionProtocol.Ssh;
        FtpsModeBox.Visibility = _editor.Protocol == ConnectionProtocol.Ftps ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyAuthenticationItem.IsEnabled = ssh;
        AnonymousAuthenticationItem.IsEnabled = !ssh;
        PrivateKeyInput.Visibility = _editor.AuthenticationMethod == AuthenticationMethod.PrivateKey ? Visibility.Visible : Visibility.Collapsed;
        SecretInput.Visibility = _editor.AuthenticationMethod == AuthenticationMethod.Anonymous ? Visibility.Collapsed : Visibility.Visible;
        SecretInput.Header = _editor.AuthenticationMethod == AuthenticationMethod.PrivateKey ? "私钥口令（可选）" : "密码（可选）";
    }

    private void ReportError(string message)
    {
        SaveError.Message = message;
        SaveError.IsOpen = true;
    }

    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (_saving || !_profiles.CanEdit || _cancellation.IsCancellationRequested) return;
        ConnectionProfile profile;
        try { profile = _editor.BuildProfile(); }
        catch (ArgumentException exception) { ReportError(exception.Message); return; }
        if (Encoding.UTF8.GetByteCount(SecretInput.Password) > 2560)
        { ReportError("凭据的 UTF-8 长度不能超过 2560 字节。"); return; }

        var deferral = args.GetDeferral();
        byte[]? secret = null;
        _saving = true;
        EditorScroll.IsEnabled = IsPrimaryButtonEnabled = false;
        SaveError.IsOpen = false;
        try
        {
            secret = Encoding.UTF8.GetBytes(SecretInput.Password);
            await _profiles.SaveProfileWithCredentialAsync(profile, secret, _cancellation);
            if (_cancellation.IsCancellationRequested) return;
            if (_profiles.ErrorMessage is { } error) ReportError(error);
            else { SecretInput.Password = ""; args.Cancel = false; }
        }
        catch (Exception) { if (!_cancellation.IsCancellationRequested) ReportError("保存未能完成，请重试。"); }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            _saving = false;
            EditorScroll.IsEnabled = IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (_saving && !_cancellation.IsCancellationRequested) args.Cancel = true;
    }

    private void Dialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        if (_dialogRoot is not null) _dialogRoot.Changed -= Root_Changed;
        _dialogRoot = null;
        SecretInput.Password = "";
    }
}
