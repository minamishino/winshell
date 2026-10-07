using System.Security.Cryptography;
using System.Text.Json;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.Core.Mcp;

/// <summary>A grant binds explicit scopes to one saved profile and previously trusted full host key.</summary>
public sealed record McpGrant(Guid ProfileId, string ProfileBinding, string HostKeyBinding, bool ListProfiles, bool RunScript,
    bool DockerRead = false, bool DockerControl = false, bool DockerExec = false)
{
    public const string ProfileListScope = "ssh.list_profiles";
    public const string ScriptScope = "ssh.run_script";
    public const string DockerReadScope = "docker.read";
    public const string DockerControlScope = "docker.control";
    public const string DockerExecScope = "docker.exec";

    public static McpGrant Create(ConnectionProfile profile, SshHostKey key, bool listProfiles, bool runScript,
        bool dockerRead = false, bool dockerControl = false, bool dockerExec = false)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(profile.Id, BindProfile(profile), BindKey(key), listProfiles, runScript, dockerRead, dockerControl, dockerExec);
    }
    public bool Allows(ConnectionProfile profile, SshHostKey key, string scope) =>
        ProfileId == profile.Id && profile.Protocol == ConnectionProtocol.Ssh && ConnectionProfileValidator.Validate(profile).Count == 0 && ProfileBinding == BindProfile(profile) && HostKeyBinding == BindKey(key) &&
        (scope == ProfileListScope && ListProfiles || scope == ScriptScope && RunScript ||
         scope == DockerReadScope && DockerRead || scope == DockerControlScope && DockerControl || scope == DockerExecScope && DockerExec);

    public static string BindProfile(ConnectionProfile profile)
    {
        if (profile.Id == Guid.Empty || profile.Protocol != ConnectionProtocol.Ssh || ConnectionProfileValidator.Validate(profile).Count != 0)
            throw new InvalidDataException("授权必须使用有效的已保存 SSH 配置。");
        var endpoint = new SshEndpointIdentity(profile.Host, profile.Port);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            profile.Id, profile.Protocol, endpoint.Host, endpoint.Port, profile.UserName,
            profile.AuthenticationMethod, profile.PrivateKeyPath, profile.CredentialReference
        })));
    }
    private static string BindKey(SshHostKey key) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { key.Algorithm, Blob = Convert.ToBase64String(key.CopyPublicKeyBlob()) })));
}
