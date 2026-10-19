using System.Collections.ObjectModel;
using WinShell.Core.Profiles;

namespace WinShell.App.Features.Connections;

public sealed class ConnectionProfileGroupViewModel(string name, IEnumerable<ConnectionProfile> profiles)
    : ReadOnlyCollection<ConnectionProfileListItemViewModel>(profiles.Select(profile => new ConnectionProfileListItemViewModel(profile)).ToArray())
{
    public string Name { get; } = name;
}

public sealed class ConnectionProfileListItemViewModel(ConnectionProfile profile)
{
    public ConnectionProfile Profile { get; } = profile;
    public string DisplayName => Profile.DisplayName;
    // WinUI uses the item's string representation to name realized and virtualized list entries.
    public override string ToString() => DisplayName;
    public string ProtocolLabel => Profile.Protocol switch
    {
        ConnectionProtocol.Ssh => "SSH",
        ConnectionProtocol.Ftp => "FTP",
        ConnectionProtocol.Ftps => "FTPS",
        _ => Profile.Protocol.ToString()
    };
    public string Endpoint
    {
        get
        {
            var host = Profile.Host.Contains(':') ? $"[{Profile.Host}]" : Profile.Host;
            var user = string.IsNullOrEmpty(Profile.UserName) ? "" : Profile.UserName + "@";
            return $"{user}{host}:{Profile.Port}";
        }
    }
    public string OpenActionName => $"打开连接 {DisplayName}";
    public string EditActionName => $"编辑连接 {DisplayName}";
    public string MoreActionName => $"连接 {DisplayName} 的更多操作";
}
