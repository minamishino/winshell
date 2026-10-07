using System.Text;
using System.Text.Json;

namespace WinShell.App.Features.Mcp;

public sealed record McpClientLaunchConfiguration(string? Json, string Status)
{
    private const int MaximumPathBytes = 4096;
    private const int MaximumJsonBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public bool IsAvailable => Json is not null;

    public static McpClientLaunchConfiguration Create(string applicationDirectory, string runtimeDirectory)
    {
        try
        {
            foreach (var directory in new[] { applicationDirectory, runtimeDirectory })
            {
                if (directory is null || directory.Length > MaximumPathBytes || StrictUtf8.GetByteCount(directory) > MaximumPathBytes)
                    return new(null, "路径超过 4096 UTF-8 字节；请将应用和 .NET 安装到较短路径后重新加载。");
                if (!Path.IsPathFullyQualified(directory) || directory.Any(character => character < 32 || "\"<>|?*".Contains(character)) || directory.AsSpan(2).Contains(':'))
                    return new(null, "应用或 .NET 运行时路径无效；请使用完整的本机安装路径后重新加载。");
            }

            var runtime = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory)));
            if (runtime.Parent?.Name != "Microsoft.NETCore.App" || runtime.Parent.Parent?.Name != "shared" ||
                !Version.TryParse(runtime.Name, out var version) || version.Major < 10 || runtime.Parent.Parent.Parent is not { } dotnetRoot)
                return new(null, "当前运行时不是受支持的共享 .NET 安装；请使用 .NET 10 共享运行时版本的 WinShell 后重新加载。");

            var dotnetHostPath = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
            if (!File.Exists(dotnetHostPath))
                return new(null, "共享 .NET 安装缺少 dotnet.exe；请修复该 .NET 安装后重新加载。");

            var mcpDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "Mcp");
            foreach (var fileName in new[] { "WinShell.Mcp.dll", "WinShell.Mcp.runtimeconfig.json", "WinShell.Mcp.deps.json" })
                if (!File.Exists(Path.Combine(mcpDirectory, fileName)))
                    return new(null, $"应用部署缺少 {fileName}；请重新部署完整的 WinShell 应用目录后重新加载。");

            var json = JsonSerializer.Serialize(new
            {
                mcpServers = new { winshell = new { command = dotnetHostPath, args = new[] { Path.Combine(mcpDirectory, "WinShell.Mcp.dll") } } }
            }, new JsonSerializerOptions { WriteIndented = true });
            return StrictUtf8.GetByteCount(json) <= MaximumJsonBytes
                ? new(json, "将此 JSON 合并到支持 stdio 的 MCP 客户端配置中。客户端按需启动服务；工具仍需在下方单独授权。移动应用或 .NET 后请重新复制。")
                : new(null, "配置超过 32 KiB；请将应用和 .NET 安装到较短路径后重新加载。");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return new(null, "应用或 .NET 运行时路径无效或无法访问；请检查本机安装路径及读取权限后重新加载。");
        }
    }
}
