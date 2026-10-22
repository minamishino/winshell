using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Terminal;

namespace WinShell.App.Features.Terminal;

// UI-affine owner; this object and its renderer are single-use. Reconnect creates a new owner.
public sealed partial class TerminalTabViewModel(string title, ConnectionProfile? profile,
    Func<CancellationToken, Task<IShellSession>> createBackend,
    Func<IShellSession, CancellationToken, Task> startView, Func<Task> outputCompletion,
    Func<Task> closeView, string targetDescription = "") : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private IShellSession? _backend;
    private Task? _operation, _close;
    private readonly TaskCompletionSource<bool> _startupSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TerminalDiagnosticSession? _diagnostic;
    private string? _endedStatus;
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; } = title;
    public string TargetDescription { get; } = targetDescription;
    public ConnectionProfile? Profile { get; } = profile;
    [ObservableProperty]
    public partial string Status { get; private set; } = "等待启动。";
    [ObservableProperty]
    public partial string DiagnosticText { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public Exception? Failure { get; private set; }
    public bool IsClosing { get; private set; }
    public Task<bool> StartupSettled => _startupSettled.Task;
    public Task StartAsync() => StartAsync(CancellationToken.None);
    public Task StartAsync(CancellationToken startupCancellation)
    {
        if (_operation is not null) return _operation;
        if (IsClosing) { _startupSettled.TrySetResult(false); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _ = RunAsync();
        return _operation;
        async Task RunAsync()
        {
            try
            {
                // External selection/navigation cancellation owns startup only. Detach before success.
                await using (startupCancellation.Register(_stop.Cancel))
                {
                    Status = "正在连接终端…";
                    _backend = await createBackend(_stop.Token);
                    _backend = _diagnostic = new TerminalDiagnosticSession(_backend);
                    _stop.Token.ThrowIfCancellationRequested();
                    await startView(_backend, _stop.Token);
                }
                _stop.Token.ThrowIfCancellationRequested();
                IsRunning = true; OnPropertyChanged(nameof(IsRunning));
                Status = "终端已连接。";
                _startupSettled.TrySetResult(true);
                await outputCompletion();
                await _backend.OutputCompletion;
                if (_diagnostic.Result is { } result) await result.ProcessCompletion;
                if (!IsClosing)
                {
                    var exitCode = _diagnostic.Result?.ExitCode;
                    _endedStatus = exitCode is { } code ? $"终端进程{(code == 0 ? "正常" : "异常")}结束，退出码：{code}。" : "终端已结束；远端未提供退出码。";
                    Status = _endedStatus;
                }
            }
            catch (OperationCanceledException) { if (!IsClosing) Status = "终端连接已取消。"; }
            catch (Exception error) { RecordFailure(error); }
            finally
            {
                _startupSettled.TrySetResult(false);
                IsRunning = false; OnPropertyChanged(nameof(IsRunning));
                completion.TrySetResult();
            }
            // Schedule owned close only after publishing operation completion (no self-join).
            try { await CloseAsync(); } catch (Exception error) { RecordFailure(error); }
        }
    }
    public void RecordFailure(Exception error)
    {
        Failure ??= error;
        Status = Failure is SshConnectionException ssh ? $"SSH {ssh.Kind}：{ssh.Message}" : $"终端失败：{Failure.Message}";
        OnPropertyChanged(nameof(Failure));
    }
    public Task CloseAsync()
    {
        if (_close is not null) return _close;
        IsClosing = true;
        OnPropertyChanged(nameof(IsClosing));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _close = completion.Task;
        var failures = new List<Exception>();
        try { _stop.Cancel(); }
        catch (Exception error)
        {
            failures.Add(error);
            RecordFailure(error);
        }
        if (_operation is null) _startupSettled.TrySetResult(false);
        if (Failure is null && _endedStatus is null) Status = "正在关闭并等待终端资源释放…";
        _ = CloseCoreAsync();
        return _close;
        async Task CloseCoreAsync()
        {
            // The view cancels renderer/start; an authenticated late backend is disposed after operation joins.
            await Attempt(closeView);
            if (_operation is not null) await Attempt(() => _operation);
            if (_backend is not null) await Attempt(() => _backend.DisposeAsync().AsTask());
            if (_diagnostic is not null) DiagnosticText = _diagnostic.Snapshot();
            _diagnostic = null;
            _backend = null;
            _stop.Dispose();
            if (Failure is null) Status = _endedStatus ?? "终端已关闭。";
            if (failures.Count == 0) completion.TrySetResult();
            else completion.TrySetException(new AggregateException("终端清理失败。", failures));
            return;

            async Task Attempt(Func<Task> action) { try { await action(); } catch (Exception error) { failures.Add(error); RecordFailure(error); } }
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
