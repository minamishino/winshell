namespace WinShell.Core.Profiles;

public static class ConnectionProfileValidator
{
    public static IReadOnlyList<ProfileValidationError> Validate(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        List<ProfileValidationError> errors = [];
        if (profile.Id == Guid.Empty)
        {
            errors.Add(new(nameof(profile.Id), "连接标识不能为空。"));
        }

        if (profile.DisplayName is null || profile.DisplayName.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            errors.Add(new(nameof(profile.DisplayName), "连接名称不能为空且最多为 128 个字符。"));
        }

        if (profile.GroupName is null || profile.GroupName.Length > 128)
        {
            errors.Add(new(nameof(profile.GroupName), "分组不能为 null 且最多为 128 个字符。"));
        }

        if (profile.Host is null || profile.Host.Length is 0 or > 253 ||
            profile.Host.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            errors.Add(new(nameof(profile.Host), "主机地址不能为空，最多为 253 个字符，且不能包含空白或控制字符。"));
        }

        if (profile.UserName is null || profile.UserName.Length > 128 ||
            string.IsNullOrWhiteSpace(profile.UserName))
        {
            errors.Add(new(nameof(profile.UserName), "用户名不能为空且最多为 128 个字符。"));
        }

        if (profile.Port is < 1 or > 65535)
        {
            errors.Add(new(nameof(profile.Port), "端口必须介于 1 和 65535 之间。"));
        }

        if (!Enum.IsDefined(profile.Protocol))
        {
            errors.Add(new(nameof(profile.Protocol), "不支持此连接协议。"));
        }

        if (!Enum.IsDefined(profile.AuthenticationMethod))
        {
            errors.Add(new(nameof(profile.AuthenticationMethod), "不支持此认证方式。"));
        }
        else if (profile.AuthenticationMethod == AuthenticationMethod.PrivateKey)
        {
            if (profile.Protocol is ConnectionProtocol.Ftp or ConnectionProtocol.Ftps)
            {
                errors.Add(new(nameof(profile.AuthenticationMethod), "FTP 和 FTPS 不支持私钥认证。"));
            }

            if (profile.Protocol == ConnectionProtocol.Ssh &&
                (string.IsNullOrWhiteSpace(profile.PrivateKeyPath) ||
                 !Path.IsPathFullyQualified(profile.PrivateKeyPath)))
            {
                errors.Add(new(nameof(profile.PrivateKeyPath), "SSH 私钥认证需要完整的绝对路径。"));
            }
        }

        if (profile.CredentialReference is not null &&
            !string.Equals(profile.CredentialReference, $"WinShell/connection/{profile.Id:D}", StringComparison.Ordinal))
        {
            errors.Add(new(nameof(profile.CredentialReference), "凭据引用必须与此连接的标识匹配。"));
        }

        return errors;
    }
}
