using WinShell.Mcp.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json;
namespace WinShell.App.Features.Mcp;
// Dispatcher-affine. Owner's background notifications are marshalled by the native page.
public sealed class McpServiceViewModel(McpServiceSettingsStore store, LocalMcpHttpTokenStore tokens, McpHttpProcessOwner owner) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private McpServiceSettings _settings = new();
    private bool _busy, _closing, _loaded, _hasToken, _hasSaveFailure;
    private string _status = "尚未读取服务设置。";
    private Task _pending = Task.CompletedTask;
    private Task? _stop, _close;
    public McpServiceSettings Settings { get => _settings; private set => SetProperty(ref _settings, value); }
    public bool HasToken { get => _hasToken; private set => SetProperty(ref _hasToken, value); }
    public bool HasSaveFailure { get => _hasSaveFailure; private set => SetProperty(ref _hasSaveFailure, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public McpClientLaunchViewModel ClientLaunch { get; } = new();
    public void ReportActionFailure() => Status = "服务操作或剪贴板写入未能完成，请重试。";
    public McpHttpProcessSnapshot Process => owner.Snapshot;
    private bool Active => Process.State is McpHttpProcessState.Starting or McpHttpProcessState.Running or McpHttpProcessState.Stopping;
    public bool CanEdit => !_busy && !_closing && !Active;
    public bool CanStart => CanEdit && _loaded && !HasSaveFailure && HasToken && Settings.Transport == McpServiceTransport.StreamableHttp;
    public bool CanStop => !_closing && Active;
    public bool CanGenerate => CanEdit && !HasToken;
    public bool CanRotate => CanEdit && HasToken;
    public bool CanCopyToken => !_busy && !_closing && HasToken;
    public bool CanCopyConfiguration => !_busy && !_closing && (Settings.Transport == McpServiceTransport.Stdio ? ClientLaunch.IsAvailable : HasToken);
    public bool CanCopySse => CanCopyConfiguration && Settings.Transport == McpServiceTransport.StreamableHttp && Settings.EnableLegacySse;
    public string EndpointDescription => Settings.Transport == McpServiceTransport.Stdio
        ? "stdio 由 AI 客户端按需启动；此窗口不持续运行该服务。"
        : Process.Endpoint is { } endpoint ? "本窗口服务已就绪：" + endpoint : "下次启动 / 配置示例：" + Settings.ConfiguredEndpoint;
    public string StateDescription => Process.State switch
    {
        McpHttpProcessState.Starting => "正在等待真实监听就绪…", McpHttpProcessState.Running => "本窗口 HTTP 服务运行中。",
        McpHttpProcessState.Stopping => "正在停止并等待子进程退出…",
        McpHttpProcessState.Failed => FailureMessage(Process.FailureCode),
        _ => Process.Forced ? "服务已停止并回收；超时后使用了强制终止。" : "本窗口 HTTP 服务未运行。"
    };
    public void RefreshOwner()
    {
        foreach (var name in new[] { nameof(Process), nameof(CanEdit), nameof(CanStart), nameof(CanStop), nameof(CanGenerate), nameof(CanRotate), nameof(CanCopyToken), nameof(CanCopyConfiguration), nameof(CanCopySse), nameof(StateDescription), nameof(EndpointDescription) }) OnPropertyChanged(name);
    }
    public Task LoadAsync() => RunAsync(async token =>
    {
        _loaded = false;
        Settings = await store.ReadAsync(token);
        using var secret = await tokens.ReadAsync(token); HasToken = secret is not null;
        _loaded = true; HasSaveFailure = false; Status = "读取设置不会启动服务或创建令牌。";
    }, mutate: true);
    public Task SetSettingsAsync(McpServiceSettings settings)
    {
        settings.Validate();
        if (!CanEdit) return Task.CompletedTask;
        Settings = settings; RefreshOwner(); return SaveAsync();
    }
    public Task SaveAsync() => RunAsync(async _ =>
    {
        await store.SaveAsync(Settings, CancellationToken.None); HasSaveFailure = false; _loaded = true; Status = "服务设置已保存。";
    }, mutate: true, saving: true);
    public Task StartAsync()
    {
        if (!CanStart) return Task.CompletedTask;
        _stop = null;
        return RunAsync(async token => { await owner.StartAsync(Settings, token); RefreshOwner(); }, mutate: true);
    }
    public Task StopAsync() => _stop ??= StopCoreAsync();
    private async Task StopCoreAsync()
    { await owner.StopAsync(); RefreshOwner(); }
    public Task GenerateTokenAsync() => !CanGenerate ? Task.CompletedTask : RunAsync(async token =>
    {
        using var existing = await tokens.ReadAsync(token);
        if (existing is not null) { HasToken = true; Status = "令牌已经存在；如需更改，请明确选择轮换。"; return; }
        using var secret = await tokens.RotateAsync(token); HasToken = true; Status = "令牌已生成；仅明确点击复制才写入剪贴板。";
    }, mutate: true);
    public Task RotateTokenAsync() => !CanRotate ? Task.CompletedTask : RunAsync(async token =>
    {
        using var secret = await tokens.RotateAsync(token); HasToken = true; Status = "令牌已轮换，请重新复制客户端配置。";
    }, mutate: true);
    public Task CopyTokenAsync(Action<string> copy) => !CanCopyToken ? Task.CompletedTask : CopySecretAsync(copy, false, false);
    public Task CopyConfigurationAsync(bool legacySse, Action<string> copy)
    {
        if (!CanCopyConfiguration || (legacySse && !CanCopySse)) return Task.CompletedTask;
        if (Settings.Transport != McpServiceTransport.Stdio) return CopySecretAsync(copy, true, legacySse);
        return RunAsync(_ => { copy(ClientLaunch.Json); Status = "已复制 stdio 配置示例。"; return Task.CompletedTask; }, mutate: false);
    }
    private Task CopySecretAsync(Action<string> copy, bool configuration, bool legacySse) => RunAsync(async token =>
    {
        using var secret = await tokens.ReadAsync(token);
        if (secret is null) { HasToken = false; Status = "令牌尚未生成。"; return; }
        var characters = new char[44];
        try
        {
            if (!Convert.TryToBase64Chars(secret.Bytes.Span, characters, out var count)) throw new InvalidDataException("token_store_invalid");
            var bearer = new string(characters, 0, count);
            if (configuration)
            {
                var endpoint = Process.Endpoint ?? Settings.ConfiguredEndpoint;
                var json = JsonSerializer.Serialize(new { mcpServers = new { winshell = new { url = endpoint + (legacySse ? "/sse" : ""), headers = new { Authorization = "Bearer " + bearer } } } }, new JsonSerializerOptions { WriteIndented = true });
                copy(json);
            }
            else copy(bearer);
            Status = "已复制；请在可信客户端中使用，工具权限仍须单独保存。";
        }
        finally { Array.Clear(characters); }
    }, mutate: false);
    private Task RunAsync(Func<CancellationToken, Task> operation, bool mutate, bool saving = false)
    {
        if (_busy || _closing || (mutate && Active)) return Task.CompletedTask;
        _busy = true; RefreshOwner();
        return _pending = ExecuteAsync();
        async Task ExecuteAsync()
        {
            await Task.Yield();
            try { await operation(_lifetime.Token); }
            catch (OperationCanceledException) when (_closing) { }
            catch (Exception error)
            {
                if (saving) HasSaveFailure = true;
                Status = error switch
                {
                    IOException when error.Message == "token_store_running" => "其他 WinShell 窗口或命令行服务正在使用令牌；请在其所属窗口停止后再轮换。",
                    InvalidDataException when error.Message == "token_store_invalid" => "令牌文件损坏或不受支持，原文件已保留。",
                    InvalidDataException => "服务设置损坏或版本不受支持，原文件已保留。",
                    UnauthorizedAccessException => "没有访问服务设置或令牌的权限。",
                    TimeoutException or OperationCanceledException => "等待服务配置锁超时，请稍后重试。",
                    _ => "服务操作失败，请重试；未确认保存成功。"
                };
            }
            finally { _busy = false; RefreshOwner(); }
        }
    }
    private static string FailureMessage(string? code) => code switch
    {
        "MCP_HTTP_TOKEN_MISSING" => "令牌缺失，请明确生成后再启动。",
        "MCP_HTTP_ALREADY_RUNNING" => "其他窗口或命令行服务已占用同一配置；此窗口不能控制它，也无法确定其端口。",
        "MCP_HTTP_INVALID_CONFIGURATION" => "服务启动参数无效，请检查端口。",
        "MCP_HTTP_STARTUP_OR_SHUTDOWN_FAILED" => "服务启动或关闭失败；端口可能已被占用。",
        "READY_TIMEOUT" => "等待服务就绪超时，已尝试回收本窗口子进程。",
        "START_CANCELED" => "启动已取消，子进程已回收。",
        "CHILD_START_FAILED" => "无法启动伴随服务，请检查完整部署。",
        "OUTPUT_LIMIT" => "子进程输出超过限制，已停止本窗口服务。",
        _ => "子进程协议或退出状态异常，已尝试回收本窗口服务。"
    };
    public ValueTask DisposeAsync()
    {
        if (_close is not null) return new(_close);
        _closing = true; _lifetime.Cancel(); RefreshOwner();
        return new(_close = CloseAsync());
    }
    private async Task CloseAsync()
    {
        var child = owner.DisposeAsync().AsTask();
        try { await Task.WhenAll(_pending, _stop ?? Task.CompletedTask, child); }
        finally { _lifetime.Dispose(); }
    }
}
