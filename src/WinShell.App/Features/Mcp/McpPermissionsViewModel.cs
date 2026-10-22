using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Mcp;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Mcp;

// Dispatcher-affine. Every save rechecks the exact profile/key the user saw before writing.
public sealed partial class McpPermissionsViewModel(IConnectionProfileRepository profiles, IKnownHostRepository hosts, IMcpGrantRepository grants) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task _pending = Task.CompletedTask;
    private Task? _close;
    private bool _busy, _closed, _loaded;
    [ObservableProperty]
    public partial IReadOnlyList<McpProfileGrantViewModel> Profiles { get; private set; } = [];
    public bool CanEdit => !_busy && !_closed;
    [ObservableProperty]
    public partial string Status { get; private set; } = "尚未读取权限。";

    public void ReportDialogFailure() => Status = "权限确认对话框未能完成，未保存授权。";
    public Task LoadAsync() => RunAsync(async token =>
    {
        _loaded = false; Profiles = [];
        var saved = await profiles.ReadAsync(token);
        if (saved.RecoveredFromBackup) throw new InvalidDataException("配置从备份恢复，请先在连接配置中明确保存，再重新加载权限。");
        if (saved.Profiles.Select(profile => profile.Id).Distinct().Count() != saved.Profiles.Count) throw new InvalidDataException("配置标识重复。");
        var permissions = await grants.ReadAsync(token);
        var rows = new List<McpProfileGrantViewModel>();
        foreach (var profile in saved.Profiles.Where(profile => profile.Protocol == ConnectionProtocol.Ssh))
        {
            var key = await hosts.ReadAsync(new SshEndpointIdentity(profile.Host, profile.Port), token);
            var grant = permissions.SingleOrDefault(item => item.ProfileId == profile.Id);
            rows.Add(new McpProfileGrantViewModel(profile, key, grant));
        }
        Profiles = rows; // One collection replacement keeps dispatcher updates bounded.
        _loaded = true;
        Status = "仅点击保存才更改授权；没有可信主机公钥的配置不能授权。";
    });
    public Task SaveAsync() => RunAsync(async token =>
    {
        if (!_loaded) throw new InvalidDataException("请先重新加载配置和权限。");
        var selected = Profiles.Where(row => row.ListProfiles || row.RunScript || row.DockerRead || row.DockerControl || row.DockerExec).ToArray();
        if (selected.Length > 64) throw new InvalidDataException("最多允许 64 个配置，请减少授权数量。");
        var current = await profiles.ReadAsync(token);
        if (current.RecoveredFromBackup) throw new InvalidDataException("配置从备份恢复，请明确保存后重新加载权限。");
        var result = new List<McpGrant>();
        foreach (var row in selected)
        {
            var matches = current.Profiles.Where(profile => profile.Id == row.Profile.Id).ToArray();
            if (matches.Length != 1 || row.Key is null) throw new InvalidDataException("配置已删除或主机尚未信任，请重新加载。");
            var key = await hosts.ReadAsync(new SshEndpointIdentity(matches[0].Host, matches[0].Port), token);
            if (key is null || !key.Equals(row.Key) || McpGrant.BindProfile(matches[0]) != McpGrant.BindProfile(row.Profile))
                throw new InvalidDataException("配置或主机公钥已变化，请重新加载并重新选择授权。");
            result.Add(McpGrant.Create(row.Profile, row.Key, row.ListProfiles, row.RunScript, row.DockerRead, row.DockerControl, row.DockerExec));
        }
        await grants.ReplaceAsync(result, token);
        Status = "权限已保存。撤销对新调用生效；已开始的操作仍需取消或等待结束。";
    });
    public Task RevokeAllAsync() => RunAsync(async token =>
    {
        await grants.ReplaceAsync([], token);
        foreach (var row in Profiles) { row.ListProfiles = false; row.RunScript = false; row.DockerRead = false; row.DockerControl = false; row.DockerExec = false; }
        Status = "已撤销全部权限。已开始的操作仍需取消或等待结束。";
    });
    private Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (!CanEdit) return Task.CompletedTask;
        _busy = true; OnPropertyChanged(nameof(CanEdit));
        return _pending = ExecuteAsync();
        async Task ExecuteAsync()
        {
            try { await operation(_lifetime.Token); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) { Status = error switch
            {
                InvalidDataException => error.Message,
                TimeoutException => "等待配置锁超时，请稍后重试。",
                UnauthorizedAccessException => "没有访问授权或配置的权限。",
                _ => "权限操作失败。未确认保存成功，请重新加载检查。"
            }; }
            finally { _busy = false; OnPropertyChanged(nameof(CanEdit)); }
        }
    }
    public ValueTask DisposeAsync()
    {
        if (_close is not null) return new ValueTask(_close);
        _closed = true; OnPropertyChanged(nameof(CanEdit));
        return new ValueTask(_close = CloseAsync());
    }
    private async Task CloseAsync()
    {
        List<Exception> failures = [];
        try { await _lifetime.CancelAsync(); }
        catch (Exception error) { failures.Add(error); }
        try { await _pending; }
        catch (Exception error) { failures.Add(error); }
        finally { _lifetime.Dispose(); }
        var failure = failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("权限操作关闭失败，已等待保存结束。", failures)
        };
        if (failure is null) return;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

public sealed partial class McpProfileGrantViewModel : ObservableObject
{
    private bool _listProfiles, _runScript, _dockerRead, _dockerControl, _dockerExec;
    public McpProfileGrantViewModel(ConnectionProfile profile, SshHostKey? key, McpGrant? grant)
    {
        Profile = profile; Key = key;
        _dockerRead = key is not null && grant?.Allows(profile, key, McpGrant.DockerReadScope) == true;
        _dockerControl = key is not null && grant?.Allows(profile, key, McpGrant.DockerControlScope) == true;
        _dockerExec = key is not null && grant?.Allows(profile, key, McpGrant.DockerExecScope) == true;
        _listProfiles = key is not null && grant?.Allows(profile, key, McpGrant.ProfileListScope) == true;
        _runScript = key is not null && grant?.Allows(profile, key, McpGrant.ScriptScope) == true;
    }
    public ConnectionProfile Profile { get; }
    public SshHostKey? Key { get; }
    public bool CanGrant => Key is not null;
    public string Name => Profile.DisplayName;
    public string ProfileIdentifier => Profile.Id.ToString("D");
    public string DockerReadLabel => $"允许读取 Docker 容器和日志：{Name}";
    public string DockerControlLabel => $"允许启停和重启 Docker 容器：{Name}";
    public string DockerExecLabel => $"允许在 Docker 容器执行任意命令：{Name}";
    public bool DockerRead { get => _dockerRead; set => SetProperty(ref _dockerRead, value && CanGrant); }
    public bool DockerControl { get => _dockerControl; set => SetProperty(ref _dockerControl, value && CanGrant); }
    public bool DockerExec { get => _dockerExec; set => SetProperty(ref _dockerExec, value && CanGrant); }
    public string Target => $"{Profile.UserName}@{Profile.Host}:{Profile.Port}";
    public string KeyDescription => Key?.Fingerprint ?? "尚未信任主机公钥，请先通过 SSH 连接核验。";
    public string MetadataLabel => $"允许列出配置：{Name}";
    public string ScriptLabel => $"允许执行任意 Shell 脚本：{Name}";
    public bool ListProfiles { get => _listProfiles; set => SetProperty(ref _listProfiles, value && CanGrant); }
    public bool RunScript { get => _runScript; set => SetProperty(ref _runScript, value && CanGrant); }
}
