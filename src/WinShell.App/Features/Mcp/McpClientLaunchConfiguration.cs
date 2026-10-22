using System.Text;
using System.Text.Json;

namespace WinShell.App.Features.Mcp;

public sealed record McpClientLaunchConfiguration(string? Json, string Status)
{
    private const int MaximumPathBytes = 4096;
    private const int MaximumJsonBytes = 32 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions ConfigurationJsonOptions = new() { WriteIndented = true };
    public bool IsAvailable => Json is not null;

    public static McpClientLaunchConfiguration Create(string? applicationDirectory, string? runtimeDirectory)
    {
        try
        {
            if (applicationDirectory is null || runtimeDirectory is null)
                return new McpClientLaunchConfiguration(null, "路径超过 4096 UTF-8 字节；请将应用和 .NET 安装到较短路径后重新加载。");
            foreach (var directory in new[] { applicationDirectory, runtimeDirectory })
            {
                if (directory.Length > MaximumPathBytes || StrictUtf8.GetByteCount(directory) > MaximumPathBytes)
                    return new McpClientLaunchConfiguration(null, "路径超过 4096 UTF-8 字节；请将应用和 .NET 安装到较短路径后重新加载。");
                if (!Path.IsPathFullyQualified(directory) || directory.Any(character => character < 32 || "\"<>|?*".Contains(character)) || directory.AsSpan(2).Contains(':'))
                    return new McpClientLaunchConfiguration(null, "应用或 .NET 运行时路径无效；请使用完整的本机安装路径后重新加载。");
            }

            var mcpDirectory = Path.Combine(Path.GetFullPath(applicationDirectory), "Mcp");
            foreach (var fileName in new[] { "WinShell.Mcp.dll", "WinShell.Mcp.runtimeconfig.json", "WinShell.Mcp.deps.json" })
                if (!File.Exists(Path.Combine(mcpDirectory, fileName)))
                    return new McpClientLaunchConfiguration(null, $"应用部署缺少 {fileName}；请重新部署完整的 WinShell 应用目录后重新加载。");

            using var runtimeConfigurationFile = File.OpenRead(Path.Combine(mcpDirectory, "WinShell.Mcp.runtimeconfig.json"));
            if (runtimeConfigurationFile.Length > MaximumJsonBytes)
                return new McpClientLaunchConfiguration(null, "MCP 运行时配置超过 32 KiB；请重新部署完整的 WinShell 应用目录后重新加载。");
            using var runtimeConfiguration = JsonDocument.Parse(runtimeConfigurationFile);
            if (runtimeConfiguration.RootElement.ValueKind != JsonValueKind.Object ||
                !runtimeConfiguration.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions) || runtimeOptions.ValueKind != JsonValueKind.Object)
                return new McpClientLaunchConfiguration(null, "MCP 运行时配置无效；请重新部署完整的 WinShell 应用目录后重新加载。");

            string command;
            string[] arguments;
            if (runtimeOptions.TryGetProperty("includedFrameworks", out var includedFrameworks))
            {
                if (includedFrameworks.ValueKind != JsonValueKind.Array ||
                    !includedFrameworks.EnumerateArray().Any(framework => framework.ValueKind == JsonValueKind.Object &&
                        framework.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == "Microsoft.NETCore.App") ||
                    runtimeOptions.TryGetProperty("framework", out _) || runtimeOptions.TryGetProperty("frameworks", out _))
                    return new McpClientLaunchConfiguration(null, "MCP 自包含运行时配置无效；请重新部署完整的 WinShell 应用目录后重新加载。");
                foreach (var fileName in new[] { "WinShell.Mcp.exe", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll" })
                    if (!File.Exists(Path.Combine(mcpDirectory, fileName)))
                        return new McpClientLaunchConfiguration(null, $"应用部署缺少 {fileName}；请重新部署完整的 WinShell 应用目录后重新加载。");
                command = Path.Combine(mcpDirectory, "WinShell.Mcp.exe");
                arguments = [];
            }
            else
            {
                var runtime = new DirectoryInfo(Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeDirectory)));
                if (runtime.Parent?.Name != "Microsoft.NETCore.App" || runtime.Parent.Parent?.Name != "shared" ||
                    !Version.TryParse(runtime.Name, out var version) || version.Major < 10 || runtime.Parent.Parent.Parent is not { } dotnetRoot)
                    return new McpClientLaunchConfiguration(null, "当前运行时不是受支持的共享 .NET 安装；请使用 .NET 10 共享运行时版本的 WinShell 后重新加载。");
                command = Path.Combine(dotnetRoot.FullName, "dotnet.exe");
                if (!File.Exists(command))
                    return new McpClientLaunchConfiguration(null, "共享 .NET 安装缺少 dotnet.exe；请修复该 .NET 安装后重新加载。");
                arguments = [Path.Combine(mcpDirectory, "WinShell.Mcp.dll")];
            }

            var json = JsonSerializer.Serialize(new
            {
                mcpServers = new { winshell = new { command, args = arguments } }
            }, ConfigurationJsonOptions);
            return StrictUtf8.GetByteCount(json) <= MaximumJsonBytes
                ? new McpClientLaunchConfiguration(json, "将此 JSON 合并到支持 stdio 的 MCP 客户端配置中。客户端按需启动服务；工具仍需在下方单独授权。移动应用或 .NET 后请重新复制。")
                : new McpClientLaunchConfiguration(null, "配置超过 32 KiB；请将应用和 .NET 安装到较短路径后重新加载。");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException)
        {
            return new McpClientLaunchConfiguration(null, "应用或 .NET 运行时路径无效或无法访问；请检查本机安装路径及读取权限后重新加载。");
        }
    }
}
