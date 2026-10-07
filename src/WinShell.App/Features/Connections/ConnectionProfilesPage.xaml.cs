using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

public sealed partial class ConnectionProfilesPage : Page
{
    public ConnectionProfilesViewModel ViewModel { get; }
    public event EventHandler<ConnectionProfile>? OpenProfileRequested;
    public event EventHandler<ConnectionProfile>? ConnectSshRequested;
    private ConnectionProfileEditorViewModel _editor = new(null);
    private readonly HashSet<Task> _pendingActions = [];
    private readonly List<Exception> _shutdownFailures = [];
    private Guid? _savedProfileId;
    private bool _loaded, _closing, _updatingSelectors;
    private readonly NativeDialogService _dialogs;
    private readonly CancellationTokenSource _dialogCancellation = new();
    private Task? _shutdown;

    public ConnectionProfilesPage(ConnectionProfilesViewModel viewModel, NativeDialogService dialogs)
    {
        _dialogs = dialogs;
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        ProtocolBox.SelectionChanged += ProtocolBox_SelectionChanged;
        AuthenticationBox.SelectionChanged += AuthenticationBox_SelectionChanged;
        SetEditor(null);
        Loaded += Page_Loaded;
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        if (_loaded || _closing) return;
        _loaded = true;
        await RunActionAsync(() => ViewModel.LoadAsync(CancellationToken.None));
    }

    // Each UI action, including a dialog continuation, settles before Host disposal.
    private async Task RunActionAsync(Func<Task> action)
    {
        if (_closing) return;
        var completion = new TaskCompletionSource();
        _pendingActions.Add(completion.Task);
        try { await action(); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception exception)
        {
            if (_closing) _shutdownFailures.Add(exception);
            else ViewModel.ReportValidationError("页面操作未能完成，请重试。");
        }
        finally
        {
            _pendingActions.Remove(completion.Task);
            completion.SetResult();
        }
    }

    public Task BeginShutdownAsync()
    {
        if (_shutdown is not null) return _shutdown;
        _closing = true;
        IsEnabled = false;
        Loaded -= Page_Loaded;
        ProtocolBox.SelectionChanged -= ProtocolBox_SelectionChanged;
        AuthenticationBox.SelectionChanged -= AuthenticationBox_SelectionChanged;
        SecretInput.Password = "";
        _dialogCancellation.Cancel();
        _shutdown = SettleActionsAsync();
        return _shutdown;
    }

    private async Task SettleActionsAsync()
    {
        await Task.WhenAll(_pendingActions.ToArray());
        _dialogCancellation.Dispose();
        if (_shutdownFailures.Count != 0) throw new AggregateException("关闭期间有页面操作未能完成。", _shutdownFailures);
    }

    private void SetEditor(ConnectionProfile? profile)
    {
        if (_closing) return;
        SecretInput.Password = "";
        _editor = new(profile);
        EditorPanel.DataContext = _editor;
        _updatingSelectors = true;
        try
        {
            ProtocolBox.SelectedItem = _editor.Protocol switch
            {
                ConnectionProtocol.Ssh => SshProtocolItem,
                ConnectionProtocol.Ftp => FtpProtocolItem,
                ConnectionProtocol.Ftps => FtpsProtocolItem,
                _ => null
            };
            AuthenticationBox.SelectedItem = _editor.AuthenticationMethod switch
            {
                AuthenticationMethod.Password => PasswordAuthenticationItem,
                AuthenticationMethod.PrivateKey => PrivateKeyAuthenticationItem,
                AuthenticationMethod.Anonymous => AnonymousAuthenticationItem,
                _ => null
            };
        }
        finally { _updatingSelectors = false; }
        UpdateProtocolControls();
        _savedProfileId = profile?.Id;
        EditorHeading.Text = profile is null ? "新建连接" : "编辑连接";
        DeleteProfileButton.IsEnabled = SaveCredentialButton.IsEnabled = CheckCredentialButton.IsEnabled = DeleteCredentialButton.IsEnabled = profile is not null;
    }
    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_closing || _updatingSelectors || !ViewModel.CanEdit) return;
        if (ReferenceEquals(ProtocolBox.SelectedItem, SshProtocolItem)) _editor.Protocol = ConnectionProtocol.Ssh;
        else if (ReferenceEquals(ProtocolBox.SelectedItem, FtpProtocolItem)) _editor.Protocol = ConnectionProtocol.Ftp;
        else if (ReferenceEquals(ProtocolBox.SelectedItem, FtpsProtocolItem)) _editor.Protocol = ConnectionProtocol.Ftps;
        UpdateProtocolControls();
    }
    private void AuthenticationBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_closing || _updatingSelectors || !ViewModel.CanEdit) return;
        if (ReferenceEquals(AuthenticationBox.SelectedItem, PasswordAuthenticationItem)) _editor.AuthenticationMethod = AuthenticationMethod.Password;
        else if (ReferenceEquals(AuthenticationBox.SelectedItem, PrivateKeyAuthenticationItem)) _editor.AuthenticationMethod = AuthenticationMethod.PrivateKey;
        else if (ReferenceEquals(AuthenticationBox.SelectedItem, AnonymousAuthenticationItem)) _editor.AuthenticationMethod = AuthenticationMethod.Anonymous;
    }
    private void UpdateProtocolControls()
    {
        FtpsModeBox.Visibility = _editor.Protocol == ConnectionProtocol.Ftps ? Visibility.Visible : Visibility.Collapsed;
        FtpsModeBox.SelectedItem = _editor.FtpsMode == FtpsMode.Implicit ? ImplicitFtpsItem : ExplicitFtpsItem;
        AnonymousAuthenticationItem.IsEnabled = _editor.Protocol != ConnectionProtocol.Ssh;
        PrivateKeyAuthenticationItem.IsEnabled = _editor.Protocol == ConnectionProtocol.Ssh;
    }
    private void FtpsModeBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_closing || _updatingSelectors || !ViewModel.CanEdit) return;
        _editor.FtpsMode = ReferenceEquals(FtpsModeBox.SelectedItem, ImplicitFtpsItem) ? FtpsMode.Implicit : FtpsMode.Explicit;
    }
    private void NewProfile_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        ViewModel.SelectedProfile = null;
        SetEditor(null);
    }
    private void OpenDetails_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (ViewModel.SelectedProfile is { } profile) OpenProfileRequested?.Invoke(this, profile);
        else ViewModel.ReportValidationError("请先从列表选择一个已保存的连接配置。");
    }
    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        // Filtering can clear and restore the same selection; keep its unsaved input.
        if (!_closing && ProfilesList.SelectedItem is ConnectionProfile profile && profile.Id != _savedProfileId) SetEditor(profile);
    }
    private async void Reload_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        await ViewModel.LoadAsync(CancellationToken.None);
        if (!_closing && ViewModel.ErrorMessage is null) SetEditor(ViewModel.SelectedProfile);
    });
    private async void SaveProfile_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (ProtocolBox.SelectedItem is null || AuthenticationBox.SelectedItem is null)
        { ViewModel.ReportValidationError("请选择协议和认证方式后再保存。"); return; }
        ConnectionProfile profile;
        try { profile = _editor.BuildProfile(); }
        catch (ArgumentException exception) { ViewModel.ReportValidationError(exception.Message); return; }
        await ViewModel.SaveProfileAsync(profile, CancellationToken.None);
        if (!_closing && ViewModel.ErrorMessage is null && ViewModel.SelectedProfile?.Id == profile.Id) SetEditor(profile);
    });
    private Task<bool> ConfirmDeletionAsync(string title, string content) =>
        _dialogs.ConfirmAsync(XamlRoot, title, content, "删除", _dialogCancellation.Token);
    private void ConnectSsh_Click(object sender, RoutedEventArgs args)
    {
        if (_closing) return;
        if (ViewModel.SelectedProfile is not { } profile)
            ViewModel.ReportValidationError("请先从列表选择一个已保存的 SSH 配置。");
        else if (profile.Protocol != ConnectionProtocol.Ssh)
            ViewModel.ReportValidationError("连接 SSH 仅支持 SSH 配置，FTP / FTPS 请使用远端文件页。");
        else ConnectSshRequested?.Invoke(this, profile);
    }
    private async void DeleteProfile_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (_savedProfileId is not Guid profileId || !await ConfirmDeletionAsync("删除此连接配置？", "将先删除此连接的凭据，再删除配置。此操作无法撤销。") || _closing) return;
        await ViewModel.DeleteProfileAsync(profileId, CancellationToken.None);
        if (!_closing && ViewModel.ErrorMessage is null) SetEditor(null);
    });
    private async void SaveCredential_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (_savedProfileId is not Guid profileId) return;
        byte[]? secretBytes = null;
        try
        {
            // PasswordBox exposes a transient string; never persist it in a VM.
            var password = SecretInput.Password;
            SecretInput.Password = "";
            if (Encoding.UTF8.GetByteCount(password) > 2560)
            { ViewModel.ReportValidationError("凭据的 UTF-8 长度不能超过 2560 字节。"); return; }
            secretBytes = Encoding.UTF8.GetBytes(password);
            password = "";
            await ViewModel.SaveCredentialAsync(profileId, secretBytes, CancellationToken.None);
        }
        finally { if (secretBytes is not null) CryptographicOperations.ZeroMemory(secretBytes); }
    });
    private async void CheckCredential_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (_savedProfileId is Guid profileId) await ViewModel.CheckCredentialAsync(profileId, CancellationToken.None);
    });
    private async void DeleteCredential_Click(object sender, RoutedEventArgs args) => await RunActionAsync(async () =>
    {
        if (_savedProfileId is not Guid profileId || !await ConfirmDeletionAsync("删除此连接的凭据？", "连接配置将保留。此操作无法撤销。") || _closing) return;
        SecretInput.Password = "";
        await ViewModel.DeleteCredentialAsync(profileId, CancellationToken.None);
    });

    public void FocusProfiles() => ProfilesList.Focus(FocusState.Programmatic);
}
