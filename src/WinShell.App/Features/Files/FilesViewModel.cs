using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Files;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
namespace WinShell.App.Features.Files;

// UI-affine. Network progress writes only a latest snapshot; the page samples it every 100 ms.
public sealed class FilesViewModel(ISftpSessionFactory factory, IKnownHostRepository knownHosts,
    Func<SftpConnectResult.UnknownHostKey, CancellationToken, Task<bool>> confirm) : ObservableObject, IAsyncDisposable
{
    private ISftpSession? _session;
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private Task? _disposal;
    private Exception? _cleanupFailure;
    private bool _busy, _disposed;
    private string _path = "/", _status = "选择已保存的 SSH 配置以浏览 SFTP 文件。", _progressText = "";
    private IReadOnlyList<RemoteFileEntry> _entries = [];
    private FileTransferProgress? _latestProgress;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) NotifyState(); } }
    public bool IsConnected => _session?.IsConnected == true;
    public bool CanConnect => !_disposed && !IsBusy && !IsConnected;
    public bool CanBrowse => !_disposed && !IsBusy && IsConnected;
    public bool CanCancel => !_disposed && IsBusy;
    public string Path { get => _path; set => SetProperty(ref _path, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public IReadOnlyList<RemoteFileEntry> Entries { get => _entries; private set => SetProperty(ref _entries, value); }
    public Guid? ConnectedProfileId { get; private set; }
    private string _listedPath = "/";
    public Task ConnectAsync(ConnectionProfile profile) => RunAsync(async token =>
    {
        await CloseSessionAsync(); ConnectedProfileId = profile.Id; Entries = []; Status = "正在连接 SFTP…";
        var result = await factory.ConnectAsync(profile, token);
        if (result is SftpConnectResult.UnknownHostKey challenge)
        {
            token.ThrowIfCancellationRequested(); Status = "等待确认主机公钥。";
            if (!await confirm(challenge, token)) { ConnectedProfileId = null; Status = "未信任主机，连接已取消。"; return; }
            token.ThrowIfCancellationRequested();
            if (!await knownHosts.TrustIfAbsentOrSameAsync(challenge.Endpoint, challenge.Key, token))
                throw new SshConnectionException(SshFailureKind.HostKeyChanged, "可信主机记录已改变；连接已拒绝。");
            token.ThrowIfCancellationRequested(); result = await factory.ConnectAsync(profile, token);
        }
        if (result is not SftpConnectResult.Connected connected) throw new SshConnectionException(SshFailureKind.HostKeyChanged, "重新连接时未确认同一主机公钥。");
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
    public Task DisconnectAsync() => RunAsync(async _ => { await CloseSessionAsync(); Entries = []; Status = "SFTP 已断开。"; });
    public void Cancel() { _cancellation?.Cancel(); }
    public void PublishProgress()
    {
        if (_disposed) return;
        var latest = Interlocked.Exchange(ref _latestProgress, null);
        if (latest is not null) ProgressText = $"{latest.BytesTransferred:N0} / {latest.TotalBytes:N0} 字节";
        NotifyState();
    }
    private sealed class LatestProgress(FilesViewModel owner) : IProgress<FileTransferProgress>
    { public void Report(FileTransferProgress value) => Interlocked.Exchange(ref owner._latestProgress, value); }
    private ISftpSession RequireSession() => _session ?? throw new InvalidOperationException("请先连接 SFTP。");
    private Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || IsBusy) return _operation;
        _cancellation = new(); IsBusy = true; ProgressText = ""; Interlocked.Exchange(ref _latestProgress, null);
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
        var message = error is OperationCanceledException ? "操作已取消；连接已弃用。" : error is SshConnectionException or ArgumentException or IOException or InvalidOperationException ? error.Message : "SFTP 操作失败。";
        if (error.Data["RemoteTemporaryFile"] is string remote) message += $" 远端临时文件可能残留：{remote}";
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
        OnPropertyChanged(nameof(IsConnected)); OnPropertyChanged(nameof(CanConnect)); OnPropertyChanged(nameof(CanBrowse)); OnPropertyChanged(nameof(CanCancel));
    }
    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new(_disposal);
        _disposed = true; Cancel(); NotifyState();
        _disposal = DisposeCoreAsync(); return new(_disposal);
    }
    private async Task DisposeCoreAsync() { await _operation; await CloseSessionAsync(); if (_cleanupFailure is not null) throw new AggregateException("SFTP 会话清理失败。", _cleanupFailure); }
}
