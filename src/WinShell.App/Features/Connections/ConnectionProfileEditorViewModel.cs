using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

public sealed class ConnectionProfileEditorViewModel : ObservableObject
{
    private readonly Guid _profileId;
    private readonly string? _credentialReference;
    private string _displayName, _host, _userName, _groupName, _portText, _privateKeyPath;
    private ConnectionProtocol _protocol;
    private AuthenticationMethod _authenticationMethod;

    public ConnectionProfileEditorViewModel(ConnectionProfile? initialProfile)
    {
        _profileId = initialProfile?.Id ?? Guid.NewGuid();
        _credentialReference = initialProfile?.CredentialReference;
        _displayName = initialProfile?.DisplayName ?? "";
        _host = initialProfile?.Host ?? "";
        _userName = initialProfile?.UserName ?? "";
        _groupName = initialProfile?.GroupName ?? "";
        _portText = (initialProfile?.Port ?? 22).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _privateKeyPath = initialProfile?.PrivateKeyPath ?? "";
        _protocol = initialProfile?.Protocol ?? ConnectionProtocol.Ssh;
        _authenticationMethod = initialProfile?.AuthenticationMethod ?? AuthenticationMethod.Password;
    }

    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value); }
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public string GroupName { get => _groupName; set => SetProperty(ref _groupName, value); }
    public string PortText { get => _portText; set => SetProperty(ref _portText, value); }
    public string PrivateKeyPath { get => _privateKeyPath; set => SetProperty(ref _privateKeyPath, value); }
    public ConnectionProtocol Protocol { get => _protocol; set => SetProperty(ref _protocol, value); }
    public AuthenticationMethod AuthenticationMethod { get => _authenticationMethod; set => SetProperty(ref _authenticationMethod, value); }

    public ConnectionProfile BuildProfile()
    {
        if (!int.TryParse(PortText, out var port))
            throw new ArgumentException("端口必须是 1 至 65535 的整数。");
        var profile = new ConnectionProfile(_profileId, DisplayName.Trim(), Protocol, Host.Trim(), port,
            UserName.Trim(), GroupName.Trim(), AuthenticationMethod,
            string.IsNullOrWhiteSpace(PrivateKeyPath) ? null : PrivateKeyPath.Trim(), _credentialReference);
        var errors = ConnectionProfileValidator.Validate(profile);
        if (errors.Count != 0)
            throw new ArgumentException(string.Join(Environment.NewLine, errors.Select(error => error.Message)));
        return profile;
    }
}
