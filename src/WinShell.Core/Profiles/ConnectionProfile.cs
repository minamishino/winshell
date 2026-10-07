namespace WinShell.Core.Profiles;

public sealed record ConnectionProfile(
    Guid Id,
    string DisplayName,
    ConnectionProtocol Protocol,
    string Host,
    int Port,
    string UserName,
    string GroupName,
    AuthenticationMethod AuthenticationMethod,
    string? PrivateKeyPath,
    string? CredentialReference,
    FtpsMode FtpsMode = FtpsMode.Explicit);
