using WinShell.Core.Docker;
using System.Text;
using System.Security.Cryptography;

namespace WinShell.Infrastructure.Ssh.Docker;

public static class DockerScriptBuilder
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    // A clean child environment prevents context/TLS/API/config overrides redirecting the fixed daemon.
    private const string ContainerFormat = "{\"ID\":{{json .ID}},\"Names\":{{json .Names}},\"Image\":{{json .Image}},\"State\":{{json .State}},\"Status\":{{json .Status}},\"Ports\":{{json .Ports}}}";
    public static string List() => Finish("container ls --all --last 32 --no-trunc --format " + Quote(ContainerFormat));
    public static string Logs(string containerId) => Finish("container logs --tail 200 " + Identifier(containerId));
    public static string Terminal(string containerId, DockerTerminalShell shell)
    {
        var identifier = Identifier(containerId);
        var executable = shell switch
        {
            DockerTerminalShell.Sh => "/bin/sh",
            DockerTerminalShell.Bash => "/bin/bash",
            _ => throw new DockerException(DockerFailureKind.InvalidInput, "请选择 /bin/sh 或 /bin/bash。")
        };
        return Finish("container exec --interactive --tty " + identifier + " " + Quote(executable));
    }
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
        // Read-only/control CLI operations do not create this configuration directory. A fresh
        // platform-generated 128-bit name avoids loading another user's Docker config or context.
        var configurationPath = "/tmp/.winshell-docker-config-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var script = "exec env -i PATH='/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin' HOME='/' docker --config "
            + Quote(configurationPath) + " --host 'unix:///var/run/docker.sock' " + arguments + "\n";
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
