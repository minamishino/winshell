using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WinShell.Mcp.Transport;

public enum TransportStopReason { None, EndOfInput, Cancelled, InputFailure, CapacityExceeded, LeaseExpired, OutputFailure, HandlerFailure }
public sealed record TransportStatistics(int HeldLeases, int OrdinaryLeases, int ControlLeases, int QueuedMessages,
    int ActiveSends, int MaximumHeldLeases, int MaximumActiveSends, bool ReaderCompleted, bool DeadlineCompleted);

/// <summary>FIFO stdio framing with bounded admission. Limits exclude the SDK's outer handler tail and DOM overhead.</summary>
public sealed class BoundedStdioTransport : ITransport
{
    public const int MaximumPayloadBytes = 512 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopping;
    private readonly Channel<JsonRpcMessage> _messages = Channel.CreateBounded<JsonRpcMessage>(new BoundedChannelOptions(16)
        { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    private readonly object _gate = new();
    private readonly Dictionary<JsonRpcMessage, (bool Control, long Started)> _leases = new(ReferenceEqualityComparer.Instance);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Task _readerTask;
    private readonly Task _deadlineTask;
    private Task? _closeTask;
    private TaskCompletionSource _sendsJoined = CompletedSignal();
    private int _held;
    private int _ordinary;
    private int _control;
    private int _sends;
    private int _maximumHeld;
    private int _maximumSends;
    private int _reason;

    public BoundedStdioTransport(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _stopping = _stop.Token;
        _readerTask = ReadAsync();
        _deadlineTask = WatchDeadlinesAsync();
    }
    public string? SessionId => null;
    public ChannelReader<JsonRpcMessage> MessageReader => _messages.Reader;
    public CancellationToken Stopping => _stopping;
    public TransportStopReason StopReason => (TransportStopReason)Volatile.Read(ref _reason);
    public TransportStatistics Statistics
    {
        get { lock (_gate) return new(_held, _ordinary, _control, _messages.Reader.Count, _sends, _maximumHeld,
            _maximumSends, _readerTask.IsCompleted, _deadlineTask.IsCompleted); }
    }

    public McpMessageHandler TrackIncoming(McpMessageHandler next) => async (context, cancellationToken) =>
    {
        try
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            // The SDK sends the normal response within next. Do not release on exceptional paths:
            // its outer catch may still serialize/send an error after this filter has unwound.
            lock (_gate)
            {
                if (_leases.Remove(context.JsonRpcMessage, out var lease))
                {
                    --_held;
                    if (lease.Control) --_control; else --_ordinary;
                }
            }
        }
        catch
        {
            Stop(TransportStopReason.HandlerFailure);
            throw;
        }
    };

    public void Stop(TransportStopReason reason)
    {
        if (reason == TransportStopReason.None) throw new ArgumentOutOfRangeException(nameof(reason));
        if (Interlocked.CompareExchange(ref _reason, (int)reason, 0) != 0) return;
        _messages.Writer.TryComplete();
        _stop.Cancel();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[MaximumPayloadBytes + 1];
        var used = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _input.ReadAsync(buffer.AsMemory(used), _stopping).ConfigureAwait(false);
                if (read == 0)
                {
                    Stop(used == 0 ? TransportStopReason.EndOfInput : TransportStopReason.InputFailure);
                    return;
                }
                used += read;
                var consumed = 0;
                while (Array.IndexOf(buffer, (byte)'\n', consumed, used - consumed) is var newline && newline >= 0)
                {
                    var length = newline - consumed;
                    if (length == 0 || length > MaximumPayloadBytes) throw new InvalidDataException("Frame limit.");
                    lock (_gate)
                    {
                        if (_held == 16) { Stop(TransportStopReason.CapacityExceeded); return; }
                        _maximumHeld = Math.Max(_maximumHeld, ++_held); // Includes the parser reservation.
                    }
                    var message = Parse(buffer.AsMemory(consumed, length));
                    var control = IsControl(message);
                    lock (_gate)
                    {
                        if ((control ? _control == 4 : _ordinary == 12) || _stop.IsCancellationRequested)
                        { Stop(TransportStopReason.CapacityExceeded); return; }
                        if (control) ++_control; else ++_ordinary;
                        _leases.Add(message, (control, Stopwatch.GetTimestamp()));
                    }
                    if (!_messages.Writer.TryWrite(message)) { Stop(TransportStopReason.CapacityExceeded); return; }
                    consumed = newline + 1;
                }
                if (consumed != 0)
                {
                    buffer.AsSpan(consumed, used - consumed).CopyTo(buffer);
                    used -= consumed;
                }
                if (used > MaximumPayloadBytes) throw new InvalidDataException("Frame limit.");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Stop(TransportStopReason.InputFailure); }
        finally { _messages.Writer.TryComplete(); }
    }

    private static JsonRpcMessage Parse(ReadOnlyMemory<byte> payload)
    {
        _ = StrictUtf8.GetCharCount(payload.Span);
        var reader = new Utf8JsonReader(payload.Span, new JsonReaderOptions { MaxDepth = 64 });
        var tokens = 0;
        while (reader.Read()) if (++tokens > 8192) throw new InvalidDataException("Token limit.");
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        var message = document.RootElement.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions)
            ?? throw new InvalidDataException("Missing message.");
        if (message is JsonRpcMessageWithId { Id.Id: string id } && id.Length > 128)
            throw new InvalidDataException("ID limit.");
        var method = message switch { JsonRpcRequest request => request.Method, JsonRpcNotification notification => notification.Method, _ => null };
        if (method?.Length > 128) throw new InvalidDataException("Method limit.");
        if (message is JsonRpcRequest { Method: "tools/call", Params: { } parameters })
        {
            var call = parameters.Deserialize<CallToolRequestParams>(McpJsonUtilities.DefaultOptions);
            if (call?.Name.Length > 128) throw new InvalidDataException("Tool limit.");
        }
        if (message is JsonRpcNotification { Method: "notifications/cancelled", Params: { } cancellationParameters })
        {
            var cancellation = cancellationParameters.Deserialize<CancelledNotificationParams>(McpJsonUtilities.DefaultOptions);
            if (cancellation?.RequestId.Id is string cancelledId && cancelledId.Length > 128)
                throw new InvalidDataException("Cancellation ID limit.");
        }
        return message;
    }

    private static bool IsControl(JsonRpcMessage message) => message is JsonRpcResponse or JsonRpcError ||
        message is JsonRpcNotification { Method: "notifications/cancelled" or "notifications/initialized" } ||
        message is JsonRpcRequest { Method: "initialize" or "ping" };

    private async Task WatchDeadlinesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping).ConfigureAwait(false))
            {
                bool expired;
                lock (_gate) expired = _leases.Values.Any(lease => Stopwatch.GetElapsedTime(lease.Started) >= TimeSpan.FromSeconds(30));
                if (expired) { Stop(TransportStopReason.LeaseExpired); return; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_stop.IsCancellationRequested) throw new OperationCanceledException(_stopping);
            if (_sends == 16) { Stop(TransportStopReason.CapacityExceeded); throw new IOException("Send capacity."); }
            if (_sends == 0) _sendsJoined = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _maximumSends = Math.Max(_maximumSends, ++_sends);
        }
        var acquired = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await _writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            deadline.Token.ThrowIfCancellationRequested();
            var staging = new CappedBufferWriter(MaximumPayloadBytes);
            using (var writer = new Utf8JsonWriter(staging))
                JsonSerializer.Serialize(writer, message, McpJsonUtilities.DefaultOptions);
            // Nothing reaches stdout until the complete payload has passed the hard capacity check.
            await _output.WriteAsync(staging.WrittenMemory, deadline.Token).ConfigureAwait(false);
            await _output.WriteAsync("\n"u8.ToArray(), deadline.Token).ConfigureAwait(false);
            await _output.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch
        {
            Stop(TransportStopReason.OutputFailure); // Partial pipe frames are fatal; never retry or append a second frame.
            throw;
        }
        finally
        {
            if (acquired) _writer.Release();
            lock (_gate) if (--_sends == 0) _sendsJoined.TrySetResult();
        }
    }

    public Task CloseAsync()
    {
        lock (_gate) return _closeTask ??= CloseCoreAsync();
    }
    private async Task CloseCoreAsync()
    {
        Stop(TransportStopReason.Cancelled);
        await Task.WhenAll(_readerTask, _deadlineTask).ConfigureAwait(false);
        Task sendsJoined;
        lock (_gate) sendsJoined = _sendsJoined.Task;
        await sendsJoined.ConfigureAwait(false);
        // Host joins SDK handlers before invoking transport disposal. Drain retained failed leases only now.
        lock (_gate)
        {
            while (_messages.Reader.TryRead(out _)) { }
            _leases.Clear();
            _held = _ordinary = _control = 0;
        }
        try { await _input.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await _output.DisposeAsync().ConfigureAwait(false); }
            finally { _writer.Dispose(); _stop.Dispose(); }
        }
    }
    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }
    public ValueTask DisposeAsync() => new(CloseAsync());
}
