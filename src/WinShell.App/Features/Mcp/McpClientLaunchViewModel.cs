using CommunityToolkit.Mvvm.ComponentModel;

namespace WinShell.App.Features.Mcp;

public sealed partial class McpClientLaunchViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Json { get; private set; } = "";
    [ObservableProperty]
    public partial string Status { get; private set; } = "";
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    public void Reload(string applicationDirectory, string runtimeDirectory)
    {
        var configuration = McpClientLaunchConfiguration.Create(applicationDirectory, runtimeDirectory);
        Json = configuration.Json ?? "";
        IsAvailable = configuration.IsAvailable;
        Status = configuration.Status;
    }

    public void ReportCopySuccess() => Status = "已复制 MCP 客户端配置；请合并到客户端配置中。工具权限仍需单独保存。";
    public void ReportCopyFailure() => Status = "无法写入剪贴板；请稍后重试，或从上方只读文本中手动复制配置。";
}
