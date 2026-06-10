using System.Net.WebSockets;
using System.Threading.Channels;

namespace KnockBox.Server.Networking;

/// <summary>
/// How a connection degrades when its bounded outbound queue fills (a stuck/slow socket):
/// </summary>
public enum OutboundOverflow
{
    /// <summary>
    /// Evict the oldest queued frame. Correct for the <b>data</b> role's host-authoritative state
    /// broadcasts, where the newest snapshot supersedes older ones.
    /// </summary>
    DropOldest,

    /// <summary>
    /// Tear the connection down. Correct for the <b>control</b> role, whose lobby events are rare,
    /// small, and must not be silently lost — a full queue means the socket is genuinely stuck.
    /// </summary>
    CloseOnFull,
}

/// <summary>
/// One connected client. A <see cref="WebSocket"/> forbids concurrent <c>SendAsync</c> calls, so
/// every outbound frame is funnelled through a single-reader channel drained by one writer loop —
/// callers (relay/broadcast) just enqueue bytes and ordering is preserved without locking.
/// </summary>
public sealed class Connection
{
    // Bounded so a dead/slow socket can't grow the queue without limit and pressure server memory.
    // The cap is generous — a healthy connection never fills it; only a stuck one does. What happens
    // on overflow depends on the role (see <see cref="OutboundOverflow"/>).
    private const int OutboundCapacity = 1024;

    private readonly Channel<byte[]> _outbound;
    private readonly WebSocket _socket;
    private readonly OutboundOverflow _overflow;

    public Connection(string playerId, string displayName, WebSocket socket,
        OutboundOverflow overflow = OutboundOverflow.DropOldest)
    {
        PlayerId = playerId;
        DisplayName = displayName;
        _socket = socket;
        _overflow = overflow;

        // CloseOnFull uses Wait mode: TryWrite returns false when full instead of dropping, which we
        // turn into a teardown. DropOldest evicts the oldest queued frame and the write always succeeds.
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(OutboundCapacity)
        {
            SingleReader = true,
            FullMode = overflow == OutboundOverflow.DropOldest
                ? BoundedChannelFullMode.DropOldest
                : BoundedChannelFullMode.Wait,
        });
    }

    public string PlayerId { get; }
    public string DisplayName { get; set; }

    /// <summary>The single lobby this client is currently in (one at a time in the skeleton).</summary>
    public string? LobbyId { get; set; }

    /// <summary>Enqueue a pre-serialized frame. Non-blocking; safe to call from any thread.</summary>
    public void Send(byte[] bytes)
    {
        if (_outbound.Writer.TryWrite(bytes)) return;
        // Only reachable in CloseOnFull mode: the queue is full, so the socket is stuck. Complete the
        // writer so the send loop ends and the owning handler tears the connection down.
        if (_overflow == OutboundOverflow.CloseOnFull) _outbound.Writer.TryComplete();
    }

    /// <summary>Drains the outbound channel onto the socket. Runs for the connection's lifetime.</summary>
    public async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(ct))
                await _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public void CompleteOutbound() => _outbound.Writer.TryComplete();
}
