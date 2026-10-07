using Microsoft.UI.Xaml.Controls;
using WinShell.Infrastructure.Windows.Terminal;
using WinShell.Core.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace WinShell.App.Controls;

public sealed class NativeTerminalControl : UserControl
{
    private readonly NativeTerminalPresenter _presenter = new();
    private readonly RendererClient _renderer;
    private Task? _close;
    private Task? _start, _output, _failureObserver;
    private readonly CancellationTokenSource _stop = new();
    private IShellSession? _session;
    private bool _running;
    private Exception? _failure;
    public Exception? Failure => _failure ?? _renderer.Failure;
    public event EventHandler<Exception>? Faulted;
    private Task? _presentation;
    private (int Width, int Height, bool Visible, bool Focused)? _pendingPresentation;
    private readonly ScrollBar _scrollBar = new() { Width = 14, Orientation = Orientation.Vertical, IsTabStop = false };
    private (int Top, int Height, int Total)? _pendingScrollState;
    private int _scrollDispatchPending;
    private int? _pendingScrollPosition;
    private Task? _scroll;
    private XamlRoot? _subscribedRoot;
    private bool _presentationSuppressed;
    private (int Width, int Height, bool Visible, bool Focused, double Scale, ElementTheme Theme)? _lastPresentation;
    public Task OutputCompletion => _output ?? Task.CompletedTask;
    public NativeTerminalControl(RendererJob job, string rendererExecutable)
    {
        _renderer = new(job, rendererExecutable);
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_scrollBar, 1);
        layout.Children.Add(_presenter); layout.Children.Add(_scrollBar);
        Content = layout;
        _renderer.ScrollChanged += (_, state) =>
        {
            lock (_scrollBar) _pendingScrollState = state;
            if (Interlocked.Exchange(ref _scrollDispatchPending, 1) != 0) return;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _scrollDispatchPending, 0);
                (int Top, int Height, int Total)? update;
                lock (_scrollBar) { update = _pendingScrollState; _pendingScrollState = null; }
                if (update is { } value && !_stop.IsCancellationRequested)
                {
                    _scrollBar.Maximum = Math.Max(0, value.Total - value.Height);
                    _scrollBar.ViewportSize = Math.Max(1, value.Height);
                    _scrollBar.Value = Math.Clamp(value.Top, 0, _scrollBar.Maximum);
                }
            })) throw new InvalidOperationException("XAML dispatcher rejected scroll state.");
        };
        _scrollBar.Scroll += (_, args) =>
        {
            if (!_running || _stop.IsCancellationRequested) return;
            _pendingScrollPosition = checked((int)args.NewValue);
            if (_scroll is null || _scroll.IsCompleted) _scroll = ApplyScrollAsync();
        };
        IsTabStop = true;
        SizeChanged += (_, _) => QueuePresentation();
        GotFocus += (_, _) => QueuePresentation();
        Unloaded += (_, _) => { _presenter.SetVisible(false); QueuePresentation(false); };
        Loaded += (_, _) =>
        {
            if (_subscribedRoot is null && XamlRoot is { } root) { _subscribedRoot = root; root.Changed += RootChanged; }
            _presenter.SetVisible(!_presentationSuppressed);
            QueuePresentation();
        };
        LayoutUpdated += (_, _) => { if (_running && !_stop.IsCancellationRequested) _presenter.UpdateBounds(); };
        ActualThemeChanged += (_, _) => QueuePresentation();
    }
    public RendererClient Renderer => _renderer;
    public nint HostWindow => _presenter.Window;
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueuePresentation();
    public void SetPresentationVisible(bool visible)
    {
        _presentationSuppressed = !visible;
        _presenter.SetVisible(visible && IsLoaded && Visibility == Visibility.Visible);
        QueuePresentation(visible);
    }
    public Task<string> CopySelectionAsync(CancellationToken token) => _renderer.CopySelectionAsync(token);
    public async Task PasteAsync(string text, CancellationToken token)
    {
        var encoding = new System.Text.UTF8Encoding(false, true);
        if (text.Length > 65536 || encoding.GetByteCount(text) > 65536) throw new ArgumentException("粘贴内容不能超过64KiB。", nameof(text));
        if (!_running || _session is null) throw new InvalidOperationException("Terminal is not running.");
        await _session.WriteInputAsync(encoding.GetBytes(text), token);
    }
    private async Task ApplyScrollAsync()
    {
        try
        {
            while (_pendingScrollPosition is { } top && !_stop.IsCancellationRequested)
            {
                _pendingScrollPosition = null;
                await _renderer.ScrollAsync(top, _stop.Token);
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { HandleFailure(error); }
    }
    public Task StartAsync(IShellSession session, CancellationToken cancellationToken)
    {
        if (_start is not null || _close is not null) throw new InvalidOperationException("Terminal instance can only start once.");
        _session = session;
        _renderer.InputConsumer = session.WriteInputAsync;
        _start = StartCoreAsync(cancellationToken);
        return ObserveStartAsync(_start);
    }
    private async Task ObserveStartAsync(Task start)
    {
        try { await start; }
        catch (Exception initial)
        {
            var cause = Failure ?? initial;
            try { await CloseAsync(CancellationToken.None); }
            catch (Exception cleanup) { throw new AggregateException(cause, cleanup); }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cause).Throw();
        }
    }
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        // Publish _start before a synchronously completed failure can request Close.
        await Task.Yield();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await StartRendererAsync(linked.Token);
        _failureObserver = ObserveRendererFailureAsync();
        await _session!.StartAsync(linked.Token);
        await _renderer.SetFocusedAsync(false, linked.Token);
        _running = true;
        QueuePresentation();
        _output = CopyOutputAsync();
    }
    private async Task CopyOutputAsync()
    {
        try
        {
            await _session!.Output.CopyToAsync(new RendererDestination(_renderer), _stop.Token);
            await _renderer.CompleteOutputAsync(_stop.Token);
        }
        catch (Exception error)
        {
            if (!_stop.IsCancellationRequested) HandleFailure(error);
            throw;
        }
    }
    private void QueuePresentation(bool? visible = null)
    {
        if (!_running || _stop.IsCancellationRequested) return;
        var scale = (XamlRoot ?? _subscribedRoot)?.RasterizationScale ?? 1;
        var state = ((int)Math.Max(16, Math.Round(_presenter.ActualWidth * scale)), (int)Math.Max(32, Math.Round(_presenter.ActualHeight * scale)), (visible ?? Visibility == Microsoft.UI.Xaml.Visibility.Visible) && !_presentationSuppressed, FocusState != Microsoft.UI.Xaml.FocusState.Unfocused, scale, ActualTheme);
        if (_lastPresentation == state) return;
        _lastPresentation = state;
        _pendingPresentation = (state.Item1, state.Item2, state.Item3, state.Item4);
        if (_presentation is null || _presentation.IsCompleted) _presentation = ApplyPresentationAsync();
    }
    private async Task ApplyPresentationAsync()
    {
        try
        {
            while (_pendingPresentation is { } state && !_stop.IsCancellationRequested)
            {
                _pendingPresentation = null;
                _presenter.UpdateBounds();
                await _renderer.SetThemeAsync(ActualTheme == ElementTheme.Dark, checked((int)Math.Round(96 * (XamlRoot?.RasterizationScale ?? 1))), _stop.Token);
                var dimensions = await _renderer.ResizeAsync(state.Width, state.Height, _stop.Token);
                await _session!.ResizeAsync(dimensions.Columns, dimensions.Rows, _stop.Token);
                await _renderer.SetVisibleAsync(state.Visible, _stop.Token);
                // Once focus enters the child, only its own WM_KILLFOCUS may clear native focus.
                if (state.Focused) await _renderer.SetFocusedAsync(true, _stop.Token);
            }
        }
        catch (Exception error) when (_stop.IsCancellationRequested && error is OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error) { HandleFailure(error); }
    }
    private async Task ObserveRendererFailureAsync()
    {
        try
        {
            // Independent of pending backend Start/output creation, and still active after normal EOF.
            var failure = await _renderer.FailureCompletion.WaitAsync(_stop.Token);
            HandleFailure(failure);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { if (!_stop.IsCancellationRequested) HandleFailure(error); }
    }
    private void HandleFailure(Exception error)
    {
        _failure ??= error;
        try { Faulted?.Invoke(this, _failure); }
        catch (Exception observerError) { _failure = new AggregateException(_failure, observerError); }
        // Return from this observer before cleanup joins it; Close owns its returned task.
        _ = CloseAsync(CancellationToken.None);
    }
    public async Task StartRendererAsync(CancellationToken cancellationToken)
    {
        _presenter.CreateHost();
        await _renderer.StartAsync(_presenter.Window, cancellationToken);
    }
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_close is not null) return _close;
        cancellationToken.ThrowIfCancellationRequested();
        // Publish before synchronous cancellation callbacks can reenter, without deferring cancellation.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _close = completion.Task;
        _ = CompleteCloseAsync(completion);
        return _close;
    }
    private async Task CompleteCloseAsync(TaskCompletionSource completion)
    {
        try { await CloseCoreAsync(); completion.TrySetResult(); }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
    }
    private async Task CloseCoreAsync()
    {
        _stop.Cancel();
        var failures = new List<Exception>();
        async Task Join(Task task) { try { await task; } catch (OperationCanceledException) { } catch (Exception error) { failures.Add(error); } }
        // Backend Close cancels a pending Start and keeps its sole ConPTY drainer alive.
        if (_session is not null) await Join(_session.CloseAsync(CancellationToken.None).AsTask());
        if (_start is not null) await Join(_start);
        await Join(_renderer.CloseAsync(CancellationToken.None));
        if (_output is not null) await Join(_output);
        if (_presentation is not null) await Join(_presentation);
        if (_scroll is not null) await Join(_scroll);
        if (_failureObserver is not null) await Join(_failureObserver);
        if (_session is not null) await Join(_session.DisposeAsync().AsTask());
        _presenter.Dispose();
        if (_subscribedRoot is { } root) root.Changed -= RootChanged;
        _subscribedRoot = null;
        _stop.Dispose();
        if (failures.Count != 0) throw new AggregateException(failures);
    }
    private sealed class RendererDestination(RendererClient renderer) : Stream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => new(renderer.ConsumeOutputAsync(buffer, token));
        public override bool CanRead => false; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer,int offset,int count) => throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
    }
}
