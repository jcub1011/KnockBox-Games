using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Drives the REAL <see cref="WebSocketHandler"/> kick path end-to-end over fake sockets to prove the
/// server pushes a <see cref="KickedMessage"/> to the kicked player's CONTROL socket (and aborts their
/// game socket). This is the server-side half of "the kicked player should leave the game".
/// </summary>
public class KickFlowTests
{
    [Fact]
    public async Task Host_kick_pushes_Kicked_to_target_control_and_aborts_their_game_socket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var catalog = new GameCatalog(Path.GetTempPath(), NullLogger<GameCatalog>.Instance); // no Discover needed
        var tokens = new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance);
        var limits = ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());
        var handler = new WebSocketHandler(connections, lobbies, catalog, tokens, limits, TimeProvider.System,
            NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance);

        // A lobby with a host and a guest.
        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        // Register the guest's CONTROL connection (the shell socket) and run its send loop so anything
        // pushed to it lands in the fake socket's capture list.
        var guestCtrlSock = new FakeWebSocket();
        var guestCtrl = new Connection("guest", "Guest", guestCtrlSock, NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(guestCtrl);
        var guestCtrlLoop = guestCtrl.SendLoopAsync(ct);

        // Register the guest's GAME connection (the iframe's data socket) so the kick can abort it.
        var guestGameSock = new FakeWebSocket();
        var guestGame = new Connection("guest", "Guest", guestGameSock, NullLogger<Connection>.Instance);
        connections.AddGame(guestGame);
        var guestGameLoop = guestGame.SendLoopAsync(ct);

        // Drive the HOST's data socket: Attach with a valid ticket, then send KickPlayer{guest}.
        var ticket = tokens.IssueTicket("host", lobby.Id, "g");
        var hostDataSock = new FakeWebSocket(
        [
            ConnectionManager.Serialize(new AttachMessage(ticket)),
            ConnectionManager.Serialize(new KickPlayerMessage("guest")),
        ]);
        await handler.HandleAsync(hostDataSock, "http://game.local", ct);

        // Flush the guest control send loop, then inspect what it received.
        guestCtrl.CompleteOutbound();
        await guestCtrlLoop;
        await guestGameLoop;

        var received = guestCtrlSock.Sent
            .Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage))
            .ToList();

        Assert.Contains(received, m => m is KickedMessage k && k.LobbyId == lobby.Id);
        Assert.Equal(WebSocketState.Aborted, guestGameSock.State);   // their game socket was evicted
        Assert.False(lobby.Contains("guest"));                       // removed from the lobby
        Assert.True(lobby.IsKicked("guest"));                        // and barred from rejoining
    }

    /// <summary>Minimal in-memory WebSocket: replays scripted inbound frames, captures outbound ones.</summary>
    private sealed class FakeWebSocket(IEnumerable<byte[]>? inbound = null) : WebSocket
    {
        private readonly Queue<byte[]> _inbound = new(inbound ?? []);
        public List<byte[]> Sent { get; } = [];
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_inbound.Count == 0)
            {
                // No more scripted input → emulate a close frame so the handler's receive loop ends.
                if (_state == WebSocketState.Open) _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var msg = _inbound.Dequeue();
            msg.CopyTo(buffer.Array!, buffer.Offset);   // frames are small; one ReceiveAsync per message
            return Task.FromResult(new WebSocketReceiveResult(msg.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            Sent.Add([.. buffer]);
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override void Dispose() { }
    }
}
