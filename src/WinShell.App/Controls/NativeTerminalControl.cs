using Microsoft.UI.Xaml.Controls;
using WinShell.Infrastructure.Windows.Terminal;
using WinShell.Core.Terminal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using WinShell.Core.Settings;

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
    private bool _rendererReady, _closed;
    private Exception? _failure;
    public Exception? Failure => _failure ?? _renderer.Failure;
    public event EventHandler<Exception>? Faulted;
    private Task? _presentation;
    private PresentationState? _pendingPresentation;
    private readonly ScrollBar _scrollBar = new() { Width = 14, Orientation = Orientation.Vertical, IsTabStop = false };
    private (int Top, int Height, int Total)? _pendingScrollState;
    private int _scrollDispatchPending;
    private int? _pendingScrollPosition;
    private Task? _scroll;
    private XamlRoot? _subscribedRoot;
    private bool _presentationSuppressed;
    private bool _inputSuppressed;
    private PresentationState? _lastPresentation;
    private TerminalPreferences _preferences = new();
    private SolidColorBrush? _backgroundBrush, _foregroundBrush;
    private long _backgroundColorToken, _backgroundOpacityToken, _foregroundColorToken, _foregroundOpacityToken;
    private readonly long _backgroundToken, _foregroundToken;
    private readonly record struct PresentationState(int Width, int Height, bool Visible, bool Focused, int Dpi, ElementTheme Theme, uint BackgroundColor, uint ForegroundColor, bool BackendRunning, TerminalPreferences Preferences);
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
        Binding SurfaceBinding() => new() { Source = this, Path = new PropertyPath(nameof(Background)) };
        layout.SetBinding(Grid.BackgroundProperty, SurfaceBinding());
        _presenter.SetBinding(Grid.BackgroundProperty, SurfaceBinding());
        _scrollBar.SetBinding(BackgroundProperty, SurfaceBinding());
        _backgroundToken = RegisterPropertyChangedCallback(BackgroundProperty, (_, _) => RefreshThemeBrushes());
        _foregroundToken = RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => RefreshThemeBrushes());
        RefreshThemeBrushes();
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
            if (_closed || !_running || _stop.IsCancellationRequested) return;
            _pendingScrollPosition = checked((int)args.NewValue);
            if (_scroll is null || _scroll.IsCompleted) _scroll = ApplyScrollAsync();
        };
        IsTabStop = true;
        SizeChanged += (_, _) => QueuePresentation();
        GotFocus += (_, _) => QueuePresentation();
        Unloaded += (_, _) => { _presenter.SetVisible(false); QueuePresentation(false); };
        Loaded += (_, _) =>
        {
            if (_closed) return;
            if (_subscribedRoot is null && XamlRoot is { } root) { _subscribedRoot = root; root.Changed += RootChanged; }
            _presenter.SetVisible(!_presentationSuppressed, !_inputSuppressed);
            QueuePresentation();
        };
        LayoutUpdated += (_, _) => { if (!_closed && _rendererReady && !_stop.IsCancellationRequested) _presenter.UpdateBounds(); };
        ActualThemeChanged += (_, _) => QueuePresentation();
    }
    public RendererClient Renderer => _renderer;
    public nint HostWindow => _presenter.Window;
    public void ApplyPreferences(TerminalPreferences preferences)
    {
        if (!DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("终端偏好必须在 XAML 线程更新。");
        ArgumentNullException.ThrowIfNull(preferences);
        preferences.Validate();
        if (_closed) return;
        _preferences = preferences;
        QueuePresentation();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => QueuePresentation();
    public void SetPresentationVisible(bool visible, bool allowInput = true)
    {
        _presentationSuppressed = !visible;
        _inputSuppressed = !allowInput;
        _presenter.SetVisible(visible && IsLoaded && Visibility == Visibility.Visible, allowInput);
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
        if (_closed || !_rendererReady || _stop.IsCancellationRequested) return;
        try
        {
            var scale = (XamlRoot ?? _subscribedRoot)?.RasterizationScale ?? 1;
            if (Background is not SolidColorBrush background || background.Color.A != 255 || background.Opacity != 1 || Foreground is not SolidColorBrush foreground)
                throw new InvalidOperationException("终端需要实心不透明主题背景和实心文字画刷。");
            var backgroundColor = background.Color;
            var foregroundColor = foreground.Color;
            var alpha = foregroundColor.A / 255d * foreground.Opacity;
            byte Blend(byte foregroundChannel, byte backgroundChannel) => checked((byte)Math.Round(foregroundChannel * alpha + backgroundChannel * (1 - alpha)));
            var nativeBackground = (uint)(backgroundColor.R | backgroundColor.G << 8 | backgroundColor.B << 16);
            var nativeForeground = (uint)(Blend(foregroundColor.R, backgroundColor.R) | Blend(foregroundColor.G, backgroundColor.G) << 8 | Blend(foregroundColor.B, backgroundColor.B) << 16);
            var state = new PresentationState((int)Math.Max(16, Math.Round(_presenter.ActualWidth * scale)), (int)Math.Max(32, Math.Round(_presenter.ActualHeight * scale)),
                (visible ?? Visibility == Microsoft.UI.Xaml.Visibility.Visible) && !_presentationSuppressed, !_inputSuppressed && FocusState != FocusState.Unfocused,
                checked((int)Math.Round(96 * scale)), ActualTheme, nativeBackground, nativeForeground, _running, _preferences);
            if (_lastPresentation == state) return;
            _lastPresentation = state;
            _pendingPresentation = state;
            if (_presentation is null || _presentation.IsCompleted) _presentation = ApplyPresentationAsync();
        }
        catch (Exception error) { HandleFailure(error); }
    }
    private void RefreshThemeBrushes()
    {
        if (_closed) return;
        ObserveBrush(Background as SolidColorBrush, ref _backgroundBrush, ref _backgroundColorToken, ref _backgroundOpacityToken);
        ObserveBrush(Foreground as SolidColorBrush, ref _foregroundBrush, ref _foregroundColorToken, ref _foregroundOpacityToken);
        QueuePresentation();
    }
    private void ObserveBrush(SolidColorBrush? brush, ref SolidColorBrush? observedBrush, ref long colorToken, ref long opacityToken)
    {
        if (ReferenceEquals(brush, observedBrush)) return;
        if (observedBrush is not null)
        {
            observedBrush.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, colorToken);
            observedBrush.UnregisterPropertyChangedCallback(Brush.OpacityProperty, opacityToken);
        }
        observedBrush = brush;
        if (brush is not null)
        {
            colorToken = brush.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => QueuePresentation());
            opacityToken = brush.RegisterPropertyChangedCallback(Brush.OpacityProperty, (_, _) => QueuePresentation());
        }
    }
    private async Task ApplyPresentationAsync()
    {
        try
        {
            while (_pendingPresentation is { } state && !_stop.IsCancellationRequested)
            {
                _pendingPresentation = null;
                _presenter.UpdateBounds();
                await _renderer.SetThemeAsync(state.Theme == ElementTheme.Dark, state.Dpi, state.BackgroundColor, state.ForegroundColor, state.Preferences, _stop.Token);
                if (!state.BackendRunning)
                {
                    await _renderer.SetVisibleAsync(state.Visible, _stop.Token);
                    continue;
                }
                var dimensions = await _renderer.ResizeAsync(state.Width, state.Height, _stop.Token);
                await _session!.ResizeAsync(dimensions.Columns, dimensions.Rows, _stop.Token);
                await _renderer.SetVisibleAsync(state.Visible, _stop.Token);
                // Once focus enters the child, only its own WM_KILLFOCUS may clear native focus.
                if (state.Visible && state.Focused && !_inputSuppressed) await _renderer.SetFocusedAsync(true, _stop.Token);
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
        _presenter.SetVisible(!_presentationSuppressed && IsLoaded && Visibility == Visibility.Visible, !_inputSuppressed);
        await _renderer.StartAsync(_presenter.Window, cancellationToken);
        _rendererReady = true;
        QueuePresentation();
        if (_presentation is { } presentation) await presentation.WaitAsync(cancellationToken);
        if (Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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
        _closed = true;
        _rendererReady = false;
        _stop.Cancel();
        UnregisterPropertyChangedCallback(BackgroundProperty, _backgroundToken);
        UnregisterPropertyChangedCallback(ForegroundProperty, _foregroundToken);
        ObserveBrush(null, ref _backgroundBrush, ref _backgroundColorToken, ref _backgroundOpacityToken);
        ObserveBrush(null, ref _foregroundBrush, ref _foregroundColorToken, ref _foregroundOpacityToken);
        var failures = new List<Exception>();
        async Task Join(Task task) { try { await task; } catch (OperationCanceledException) { } catch (Exception error) { failures.Add(error); } }
        // Backend Close cancels a pending Start and keeps its sole ConPTY drainer alive.
        if (_session is not null)
        {
            await Join(_session.CloseAsync(CancellationToken.None).AsTask());
        }
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
