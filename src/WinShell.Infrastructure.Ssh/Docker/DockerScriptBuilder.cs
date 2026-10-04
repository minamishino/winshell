using WinShell.Core.Docker;
using System.Text;

namespace WinShell.Infrastructure.Ssh.Docker;

public static class DockerScriptBuilder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // A clean child environment prevents context/TLS/API/config overrides redirecting the fixed daemon.
    private const string Prefix = "exec env -i PATH='/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin' HOME='/dev/null' docker --host 'unix:///var/run/docker.sock' ";
    public static string List() => Finish("container ls --all --last 32 --no-trunc --format '{{json .}}'");
    public static string Logs(string containerId) => Finish("container logs --tail 200 " + Identifier(containerId));
    public static string Change(string containerId, DockerContainerAction action)
    {
        var verb = action switch
        {
            DockerContainerAction.Start => "start",
            DockerContainerAction.Stop => "stop --time 10",
            DockerContainerAction.Restart => "restart --time 10",
            _ => throw new DockerException(DockerFailureKind.InvalidInput, "无效的容器操作。")
        };
        return Finish("container " + verb + " " + Identifier(containerId));
    }
    public static string Command(string containerId, string command)
    {
        var identifier = Identifier(containerId);
        ValidateText(command, 4096);
        if (string.IsNullOrWhiteSpace(command)) throw new DockerException(DockerFailureKind.InvalidInput, "请输入一次性容器命令。");
        return Finish("container exec " + identifier + " '/bin/sh' '-c' " + Quote(command));
    }
    internal static string Identifier(string containerId)
    {
        if (containerId is null || containerId.Length != 64 || containerId.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DockerException(DockerFailureKind.InvalidInput, "容器标识必须为完整的 64 位小写十六进制 ID。");
        return Quote(containerId);
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static string Finish(string arguments)
    {
        var script = Prefix + arguments + "\n";
        ValidateText(script, 8192);
        return script;
    }
    internal static void ValidateText(string value, int maximumBytes)
    {
        try
        {
            if (value is null || value.Contains('\0') || value.Length > maximumBytes || Utf8.GetByteCount(value) > maximumBytes)
                throw new DockerException(DockerFailureKind.InvalidInput, $"文本超过 {maximumBytes} UTF-8 字节或包含 NUL。");
        }
        catch (EncoderFallbackException exception) { throw new DockerException(DockerFailureKind.InvalidInput, "文本包含无效 Unicode。", exception); }
    }
}
