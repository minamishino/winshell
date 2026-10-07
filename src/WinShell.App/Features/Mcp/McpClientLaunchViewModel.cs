using CommunityToolkit.Mvvm.ComponentModel;

namespace WinShell.App.Features.Mcp;

public sealed class McpClientLaunchViewModel : ObservableObject
{
    private string _json = "";
    private string _status = "";
    private bool _isAvailable;
    public string Json { get => _json; private set => SetProperty(ref _json, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsAvailable { get => _isAvailable; private set => SetProperty(ref _isAvailable, value); }

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
