using CommunityToolkit.Mvvm.ComponentModel;
using WinShell.Core.Profiles;
using WinShell.Core.Ssh;
using WinShell.Core.Terminal;

namespace WinShell.App.Features.Terminal;

// UI-affine owner; this object and its renderer are single-use. Reconnect creates a new owner.
public sealed class TerminalTabViewModel(string title, ConnectionProfile? profile,
    Func<CancellationToken, Task<IShellSession>> createBackend,
    Func<IShellSession, CancellationToken, Task> startView, Func<Task> outputCompletion,
    Func<Task> closeView) : ObservableObject, IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private IShellSession? _backend;
    private Task? _operation, _close;
    private bool _closing;
    private string _status = "等待启动。";
    private Exception? _failure;
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; } = title;
    public ConnectionProfile? Profile { get; } = profile;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsRunning { get; private set; }
    public Exception? Failure => _failure;
    public bool IsClosing => _closing;
    public Task StartAsync()
    {
        if (_operation is not null) return _operation;
        if (_closing) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = completion.Task;
        _ = RunAsync();
        return _operation;
        async Task RunAsync()
        {
            try
            {
                Status = "正在连接终端…";
                _backend = await createBackend(_stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                await startView(_backend, _stop.Token);
                _stop.Token.ThrowIfCancellationRequested();
                IsRunning = true; OnPropertyChanged(nameof(IsRunning));
                Status = "终端已连接。";
                await outputCompletion();
                await _backend.OutputCompletion;
                if (!_closing) Status = "远端或本地进程已结束。";
            }
            catch (OperationCanceledException) { if (!_closing) Status = "终端连接已取消。"; }
            catch (Exception error) { RecordFailure(error); }
            finally
            {
                IsRunning = false; OnPropertyChanged(nameof(IsRunning));
                completion.TrySetResult();
            }
            // Schedule owned close only after publishing operation completion (no self-join).
            try { await CloseAsync(); } catch (Exception error) { RecordFailure(error); }
        }
    }
    public void RecordFailure(Exception error)
    {
        _failure ??= error;
        Status = _failure is SshConnectionException ssh ? $"SSH {ssh.Kind}：{ssh.Message}" : $"终端失败：{_failure.Message}";
        OnPropertyChanged(nameof(Failure));
    }
    public Task CloseAsync()
    {
        if (_close is not null) return _close;
        _closing = true;
        OnPropertyChanged(nameof(IsClosing));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _close = completion.Task;
        _stop.Cancel();
        if (_failure is null) Status = "正在关闭并等待终端资源释放…";
        _ = CloseCoreAsync();
        return _close;
        async Task CloseCoreAsync()
        {
            var failures = new List<Exception>();
            async Task Attempt(Func<Task> action) { try { await action(); } catch (Exception error) { failures.Add(error); RecordFailure(error); } }
            // The view cancels renderer/start; an authenticated late backend is disposed after operation joins.
            await Attempt(closeView);
            if (_operation is not null) await Attempt(() => _operation);
            if (_backend is not null) await Attempt(() => _backend.DisposeAsync().AsTask());
            _backend = null;
            _stop.Dispose();
            if (_failure is null) Status = "终端已关闭。";
            if (failures.Count == 0) completion.TrySetResult();
            else completion.TrySetException(new AggregateException("终端清理失败。", failures));
        }
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
