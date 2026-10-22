using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Files;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.App.Features.Files;

// UI-affine. Network progress writes only a latest snapshot; the page samples it every 100 ms.
public sealed partial class FilesViewModel(IRemoteFileSessionFactory factory, IKnownHostRepository knownHosts,
    Func<RemoteFileConnectResult.UnknownHostKey, CancellationToken, Task<bool>> confirm,
    Func<ConnectionProfile, CancellationToken, Task<bool>>? confirmPlaintext = null) : ObservableObject, IAsyncDisposable
{
    private IRemoteFileSession? _session;
    private ConnectionProtocol _protocol;
    public string ConnectionNotice => _protocol is ConnectionProtocol.Ftp or ConnectionProtocol.Ftps && IsReady ? "配置已验证，文件操作分别建立连接。" : "";
    public string TransferSafety => _protocol is ConnectionProtocol.Ftp or ConnectionProtocol.Ftps ? "单文件传输；已有目标拒绝覆盖。FTP 的并发创建保护取决于服务器；失败的远端临时文件不会自动删除。" : "仅单文件传输，不覆盖已有目标。双击或 Enter 打开目录。";
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private Exception? _cleanupFailure;
    private bool _disposed;
    private bool _lastNotifiedReady;

    private FileTransferProgress? _latestProgress;
    public bool IsBusy { get; private set { if (SetProperty(ref field, value)) NotifyState(); } }
    public bool IsReady => _session?.IsReady == true;
    public bool CanConnect => !_disposed && !IsBusy && !IsReady;
    public bool CanBrowse => !_disposed && !IsBusy && IsReady;
    public bool CanCancel => !_disposed && IsBusy;
    public bool ShowConnectionPrompt => !IsReady && !IsBusy;
    [ObservableProperty]
    public partial string Path { get; set; } = "/";
    [ObservableProperty]
    public partial string Status { get; private set; } = "";
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";
    [ObservableProperty]
    public partial IReadOnlyList<RemoteFileEntry> Entries { get; private set; } = [];
    public Guid? ConnectedProfileId { get; private set; }
    private string _listedPath = "/";
    public Task ConnectAsync(ConnectionProfile profile) => RunAsync(async token =>
    {
        await CloseSessionAsync(); ConnectedProfileId = profile.Id; Entries = []; _protocol = profile.Protocol; Status = "正在验证文件连接…";
        var allowPlaintext = profile.Protocol == ConnectionProtocol.Ftp && confirmPlaintext is not null && await confirmPlaintext(profile, token);
        token.ThrowIfCancellationRequested();
        if (profile.Protocol == ConnectionProtocol.Ftp && !allowPlaintext) { ConnectedProfileId = null; Status = "未确认明文传输，连接已取消。"; return; }
        var result = await factory.ConnectAsync(profile, token, allowPlaintext);
        if (result is RemoteFileConnectResult.UnknownHostKey challenge)
        {
            token.ThrowIfCancellationRequested(); Status = "等待确认主机公钥。";
            if (!await confirm(challenge, token)) { ConnectedProfileId = null; Status = "未信任主机，连接已取消。"; return; }
            token.ThrowIfCancellationRequested();
            if (!await knownHosts.TrustIfAbsentOrSameAsync(challenge.Endpoint, challenge.Key, token))
                throw new SshConnectionException(SshFailureKind.HostKeyChanged, "可信主机记录已改变；连接已拒绝。");
            token.ThrowIfCancellationRequested(); result = await factory.ConnectAsync(profile, token, allowPlaintext);
        }
        if (result is not RemoteFileConnectResult.Connected connected) throw new SshConnectionException(SshFailureKind.HostKeyChanged, "重新连接时未确认同一主机公钥。");
        if (token.IsCancellationRequested || _disposed) { await connected.Session.DisposeAsync(); token.ThrowIfCancellationRequested(); return; }
        _session = connected.Session; ConnectedProfileId = profile.Id; NotifyState();
        await ListCoreAsync("/", token);
    });
    public Task BrowseAsync(string path) => RunAsync(token => ListCoreAsync(path, token));
    public Task RefreshAsync() => BrowseAsync(_listedPath);
    public Task UpAsync()
    {
        var trimmed = _listedPath.TrimEnd('/'); var separator = trimmed.LastIndexOf('/');
        return BrowseAsync(separator <= 0 ? "/" : trimmed[..separator]);
    }
    private async Task ListCoreAsync(string path, CancellationToken token)
    {
        Entries = []; Status = "正在读取目录…";
        var page = await RequireSession().ListDirectoryAsync(path, token);
        token.ThrowIfCancellationRequested(); if (_disposed) return;
        _listedPath = Path = page.Path; Entries = page.Entries;
        Status = page.IsLimited ? $"显示 {page.Entries.Count} 项，已达到本页上限。此页为有界快照，不支持续页。" : $"已读取 {page.Entries.Count} 项。";
    }
    public Task DownloadAsync(RemoteFileEntry entry, string localPath) => RunAsync(async token =>
    {
        if (!entry.IsRegularFile) throw new ArgumentException("请选择普通文件。");
        var result = await RequireSession().DownloadAsync(entry.FullPath, localPath, new LatestProgress(this), token);
        // A completed commit remains a completed transfer even if cancellation follows its atomic move.
        Status = $"下载完成：{result.BytesTransferred:N0} 字节；SHA-256 {result.Sha256}";
    });
    public Task UploadAsync(string localPath, string fileName) => RunAsync(async token =>
    {
        var result = await RequireSession().UploadAsync(localPath, _listedPath, fileName, new LatestProgress(this), token);
        Status = $"上传完成：{result.BytesTransferred:N0} 字节；SHA-256 {result.Sha256}。刷新以查看文件。";
    });
    public Task DisconnectAsync() => RunAsync(async _ => { await CloseSessionAsync(); Entries = []; Status = "文件连接已关闭。"; });
    public void Cancel() { _cancellation?.Cancel(); }
    public void PublishProgress()
    {
        if (_disposed) return;
        var latest = Interlocked.Exchange(ref _latestProgress, null);
        if (latest is not null) ProgressText = $"{latest.BytesTransferred:N0} / {latest.TotalBytes:N0} 字节";
        if (IsReady != _lastNotifiedReady) NotifyState();
    }
    private sealed class LatestProgress(FilesViewModel owner) : IProgress<FileTransferProgress>
    { public void Report(FileTransferProgress value) => Interlocked.Exchange(ref owner._latestProgress, value); }
    private IRemoteFileSession RequireSession() => _session ?? throw new InvalidOperationException("请先选择文件连接。");
    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return _operation;
        _cancellation = new CancellationTokenSource(); IsBusy = true; ProgressText = ""; Interlocked.Exchange(ref _latestProgress, null);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _ = CompleteAsync(); return completion.Task;
        async Task CompleteAsync()
        {
            try { await action(_cancellation.Token); }
            catch (Exception error)
            {
                Status = Describe(error);
                try { await CloseSessionAsync(); }
                catch (Exception cleanup) { Status += " 会话清理失败：" + Describe(cleanup); }
            }
            finally { PublishProgress(); _cancellation.Dispose(); _cancellation = null; IsBusy = false; completion.SetResult(); }
        }
    }
    private static string Describe(Exception error)
    {
        var message = error switch { OperationCanceledException => "操作已取消；连接已弃用。", SshConnectionException or ArgumentException or IOException or InvalidOperationException => error.Message, _ => "远端文件操作失败。" };
        if (error.Data["RemoteTemporaryFile"] is string remote) message += $" 远端临时文件可能残留：{remote}";
        if (error.Data["RemoteTemporaryOwnershipConfirmed"] is false) message += " 创建结果和所有权未确认；未自动删除该路径。";
        if (error.Data["RemoteDestinationFile"] is string destination) message += $" 提交结果未确认，目标可能已创建：{destination}。请检查远端状态后再操作；未自动重试或删除目标。";
        if (error.Data["LocalTemporaryFile"] is string local) message += $" 本地临时文件清理失败：{local}";
        return message;
    }
    private async Task CloseSessionAsync()
    {
        var session = _session; _session = null; ConnectedProfileId = null;
        try { if (session is not null) await session.DisposeAsync(); }
        catch (Exception error) { _cleanupFailure = error; throw; }
        finally { NotifyState(); }
    }
    private void NotifyState()
    {
        _lastNotifiedReady = IsReady;
        OnPropertyChanged(nameof(ConnectionNotice)); OnPropertyChanged(nameof(TransferSafety));
        OnPropertyChanged(nameof(IsReady)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanBrowse)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(ShowConnectionPrompt));
    }
    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new ValueTask(_disposal);
        _disposed = true; Cancel(); NotifyState();
        _disposal = DisposeCoreAsync(); return new ValueTask(_disposal);
    }
    private async Task DisposeCoreAsync() { await _operation; await CloseSessionAsync(); if (_cleanupFailure is not null) throw new AggregateException("文件会话清理失败。", _cleanupFailure); }
}
