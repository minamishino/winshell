using System.Net;
using System.Text;
namespace WinShell.Core.Tunnels;
public static class TunnelDefinitionValidator
{
    public static void Validate(TunnelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Id == Guid.Empty || definition.ProfileId == Guid.Empty || !Enum.IsDefined(definition.Kind))
            throw new ArgumentException("隧道标识、配置或类型无效。");
        ValidateText(definition.Name, 128, "名称");
        if (definition.Kind == TunnelKind.Dynamic)
        {
            if (definition.TargetHost != "" || definition.TargetPort != 0) throw new ArgumentException("动态隧道不保存目标地址。");
            return;
        }
        ValidateText(definition.TargetHost, 255, "目标主机");
        if (definition.TargetPort == 0) throw new ArgumentException("目标端口必须介于 1 与 65535 之间。");
        if (definition.Kind == TunnelKind.Remote && !IPAddress.TryParse(definition.TargetHost, out _))
            throw new ArgumentException("远程转发的本机目标必须使用 IPv4 或 IPv6 地址。");
    }
    private static void ValidateText(string value, int maximumBytes, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumBytes || value.Any(char.IsControl)) throw new ArgumentException($"{label}无效或过长。");
        try { if (new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes) throw new ArgumentException($"{label}过长。"); }
        catch (EncoderFallbackException error) { throw new ArgumentException($"{label}包含非法 Unicode。", error); }
    }
}
