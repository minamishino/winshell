using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;

namespace WinShell.App.Features.Ssh;

public enum SshSessionState { Disconnected, Connecting, AwaitingTrust, Connected, Closing, Closed, Failed }

// UI-affine owner. Protocol events are posted to the captured window context.
public sealed partial class SshSessionViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ISshSessionFactory _factory;
    private readonly IKnownHostRepository _knownHosts;
    private readonly Func<SshConnectResult.UnknownHostKey, CancellationToken, Task<bool>> _confirm;
    private readonly SynchronizationContext _context;
    private readonly List<Exception> _cleanupFailures = [];
    private CancellationTokenSource? _attemptCancellation;
    private ISshSession? _session;
    private Task? _operation, _disposal;
    private bool _disposed;

    public SshSessionViewModel(ConnectionProfile profile, ISshSessionFactory factory, IKnownHostRepository knownHosts,
        Func<SshConnectResult.UnknownHostKey, CancellationToken, Task<bool>> confirm, SynchronizationContext context)
    {
        Profile = profile;
        _factory = factory;
        _knownHosts = knownHosts;
        _confirm = confirm;
        _context = context;
    }
    public ConnectionProfile Profile { get; private set; }
    public Guid ProfileId => Profile.Id;
    public string Target => $"{Profile.UserName}@{Profile.Host}:{Profile.Port}";
    public string Title => Profile.DisplayName;
    public SshSessionState State { get; private set; }
    public string StatusMessage { get; private set; } = "尚未连接。";
    public string HostKeyDescription { get; private set; } = "";
    public SshFailureKind? FailureKind { get; private set; }
    public bool CanConnect => !_disposed && _operation is null && _session is null;
    public bool CanCancel => !_disposed && _attemptCancellation is { IsCancellationRequested: false };
    public bool CanDisconnect => !_disposed && _operation is null && _session is not null;

    public void ReportActionFailure()
    {
        if (_disposed) return;
        SetState(State, "SSH 页面操作未能完成，请重试。");
    }

    public void ApplySavedProfile(ConnectionProfile profile)
    {
        if (profile.Id != ProfileId) throw new ArgumentException("连接配置标识不匹配。", nameof(profile));
        if (!CanConnect) return; // Active connections retain their exact snapshot.
        Profile = profile;
        HostKeyDescription = "";
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(HostKeyDescription));
    }

    public Task ConnectAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_operation is not null) return _operation;
        if (_session is not null) return Task.CompletedTask;
        _attemptCancellation = new CancellationTokenSource();
        return RunOperationAsync(ConnectCoreAsync);
    }

    private async Task ConnectCoreAsync()
    {
        var cancellation = _attemptCancellation!;
        var cancellationToken = cancellation.Token;
        FailureKind = null;
        HostKeyDescription = "";
        SetState(SshSessionState.Connecting, "正在连接并验证主机身份…");
        try
        {
            var result = await _factory.ConnectAsync(Profile, cancellationToken);
            if (result is SshConnectResult.UnknownHostKey challenge)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SetState(SshSessionState.AwaitingTrust, "等待主机公钥确认；尚未认证。" );
                var accepted = await _confirm(challenge, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!accepted) { SetState(SshSessionState.Disconnected, "已取消主机公钥确认。"); return; }
                bool trusted;
                try { trusted = await _knownHosts.TrustIfAbsentOrSameAsync(challenge.Endpoint, challenge.Key, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                { throw new SshConnectionException(SshFailureKind.Storage, "无法保存可信主机记录；连接已拒绝。", exception); }
                if (!trusted)
                    throw new SshConnectionException(SshFailureKind.HostKeyChanged, "主机公钥已变化，连接已拒绝。");
                cancellationToken.ThrowIfCancellationRequested();
                SetState(SshSessionState.Connecting, "主机公钥已确认，正在重新连接并认证…");
                result = await _factory.ConnectAsync(Profile, cancellationToken);
            }
            if (result is not SshConnectResult.Connected connected)
                throw new SshConnectionException(SshFailureKind.Protocol, "信任确认后仍收到未知公钥，连接已停止。");
            // Take ownership before checking cancellation: even a late result must be closed.
            _session = connected.Session;
            if (cancellationToken.IsCancellationRequested || _disposed || !_session.IsConnected)
            {
                await ReleaseSessionAsync();
                cancellationToken.ThrowIfCancellationRequested();
                SetState(SshSessionState.Disconnected, "连接已关闭。");
                return;
            }
            _session.Closed += Session_Closed;
            _subscribedSession = _session;
            if (!_session.IsConnected)
            {
                await ReleaseSessionAsync();
                SetState(SshSessionState.Disconnected, "连接已关闭。");
                return;
            }
            HostKeyDescription = $"{connected.Key.Algorithm}  {connected.Key.Fingerprint}";
            SetState(SshSessionState.Connected, "SSH 已连接（已认证），交互式终端尚未接入。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { SetState(SshSessionState.Disconnected, "连接已取消，资源清理已结束。"); }
        catch (SshConnectionException exception)
        {
            FailureKind = exception.Kind;
            SetState(SshSessionState.Failed, FailureMessage(exception.Kind));
        }
        catch (Exception)
        { SetState(SshSessionState.Failed, "连接或资源清理失败，请检查配置后重试。"); }
        finally
        {
            _attemptCancellation = null;
            cancellation.Dispose();
        }
    }

    private ISshSession? _subscribedSession;
    private void Session_Closed(object? sender, SshSessionClosedEventArgs args) => _context.Post(state =>
    {
        if (_disposed || !ReferenceEquals(sender, _session)) return;
        // Posting also prevents protocol callbacks from waiting for UI/disposal.
        _ = HandleRemoteCloseAsync(args.Failure);
    }, null);

    private async Task HandleRemoteCloseAsync(SshConnectionException? failure)
    {
        try
        {
            if (_operation is not null) await _operation;
            if (_disposed || _session is null) return;
            await DisconnectAsync();
            if (failure is not null && !_disposed)
            {
                FailureKind = failure.Kind;
                SetState(SshSessionState.Failed, FailureMessage(failure.Kind));
            }
        }
        catch (Exception) { if (!_disposed) SetState(SshSessionState.Failed, "远端连接已关闭，但部分资源清理失败。"); }
    }

    public void Cancel()
    {
        if (_attemptCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        SetState(SshSessionState.Closing, "正在取消并等待连接清理；协议阶段等待可能延迟关闭…");
    }

    public Task DisconnectAsync()
    {
        if (_disposed) return _disposal ?? Task.CompletedTask;
        if (_operation is null) return RunOperationAsync(async () =>
        {
            SetState(SshSessionState.Closing, "正在断开并清理连接…");
            try { await ReleaseSessionAsync(); SetState(SshSessionState.Disconnected, "SSH 已断开。"); }
            catch { SetState(SshSessionState.Failed, "断开时部分资源清理失败，已尝试全部清理。"); throw; }
        });
        Cancel();
        return _operation;
    }

    private Task RunOperationAsync(Func<Task> operation)
    {
        var completion = new TaskCompletionSource();
        _operation = completion.Task;
        _ = RunAsync();
        return completion.Task;
        async Task RunAsync()
        {
            Exception? failure = null;
            try { await operation(); }
            catch (Exception exception) { failure = exception; }
            finally { _operation = null; NotifyCommands(); }
            if (failure is null) completion.SetResult(); else completion.SetException(failure);
        }
    }

    private async Task ReleaseSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session is null) return;
        if (ReferenceEquals(session, _subscribedSession))
        { session.Closed -= Session_Closed; _subscribedSession = null; }
        List<Exception> failures = [];
        try { await session.DisconnectAsync(CancellationToken.None); }
        catch (Exception exception) { failures.Add(exception); }
        try { await session.DisposeAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        if (failures.Count != 0)
        {
            if (_cleanupFailures.Count == 0) _cleanupFailures.AddRange(failures);
            throw new AggregateException("SSH 资源清理失败。", failures);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposal is not null) return new ValueTask(_disposal);
        _disposed = true;
        Cancel();
        _disposal = DisposeCoreAsync();
        return new ValueTask(_disposal);
    }
    private async Task DisposeCoreAsync()
    {
        SetState(SshSessionState.Closing, "正在关闭 SSH 会话并等待资源清理…");
        try { if (_operation is not null) await _operation; }
        catch (Exception exception) { if (_cleanupFailures.Count == 0) _cleanupFailures.Add(exception); }
        try { await ReleaseSessionAsync(); }
        catch (AggregateException) { /* ReleaseSessionAsync retained every failure. */ }
        SetState(SshSessionState.Closed, _cleanupFailures.Count == 0 ? "SSH 会话已关闭。" : "SSH 会话关闭时部分资源清理失败。");
        if (_cleanupFailures.Count != 0) throw new AggregateException("SSH 会话清理失败。", _cleanupFailures);
    }
    private void SetState(SshSessionState state, string message)
    {
        State = state;
        StatusMessage = message;
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(FailureKind));
        OnPropertyChanged(nameof(HostKeyDescription));
        NotifyCommands();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanDisconnect));
    }
    private static string FailureMessage(SshFailureKind kind) => kind switch
    {
        SshFailureKind.Authentication => "SSH 认证失败，请检查用户名及已保存的密码或私钥口令。",
        SshFailureKind.HostKeyChanged => "主机公钥已变化，连接已拒绝。请通过可信渠道核实，应用不会自动覆盖。",
        SshFailureKind.MissingCredential => "缺少已保存的凭据，请先在连接配置中保存。",
        SshFailureKind.CredentialAccess => "无法读取 Windows 凭据，请检查凭据存储状态。",
        SshFailureKind.Storage => "可信主机存储不可用或损坏，已拒绝连接。",
        SshFailureKind.Network => "SSH 网络连接失败，请检查主机、端口和网络。",
        SshFailureKind.Permission => "访问被拒绝，请检查文件和连接权限。",
        SshFailureKind.Timeout => "SSH 连接超时，可检查网络后重试。",
        SshFailureKind.Capacity => "SSH 连接容量已满，请等待其他连接完成或关闭会话。",
        SshFailureKind.PrivateKey => "私钥不可用、口令错误或格式不支持。首期仅支持受限 RSA PKCS#1 PEM；请查看项目说明。",
        SshFailureKind.InvalidProfile => "SSH 连接配置无效，请检查已保存的配置。",
        _ => "SSH 协议错误，连接已停止。"
    };
}
