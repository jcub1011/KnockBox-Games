using System.Net.WebSockets;
using System.Threading.Channels;

namespace KnockBox.Server.Networking;

/// <summary>
/// One connected client. A <see cref="WebSocket"/> forbids concurrent <c>SendAsync</c> calls, so
/// every outbound frame is funnelled through a single-reader channel drained by one writer loop —
/// callers (relay/broadcast) just enqueue bytes and ordering is preserved without locking.
/// </summary>
public sealed class Connection(string playerId, string displayName, WebSocket socket)
{
    private readonly Channel<byte[]> _outbound =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public string PlayerId { get; } = playerId;
    public string DisplayName { get; set; } = displayName;

    /// <summary>The single lobby this client is currently in (one at a time in the skeleton).</summary>
    public string? LobbyId { get; set; }

    /// <summary>Enqueue a pre-serialized frame. Non-blocking; safe to call from any thread.</summary>
    public void Send(byte[] bytes) => _outbound.Writer.TryWrite(bytes);

    /// <summary>Drains the outbound channel onto the socket. Runs for the connection's lifetime.</summary>
    public async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(ct))
                await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    public void CompleteOutbound() => _outbound.Writer.TryComplete();
}
