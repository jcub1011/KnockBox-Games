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
/// Drives the REAL <see cref="WebSocketHandler"/> over fake sockets to prove a game's
/// <see cref="GameLogMessage"/> (sent on its DATA socket) is forwarded to the same player's CONTROL
/// socket with the trusted context (gameId, server timestamp, isHost) stamped and the untrusted
/// metadata sanitized. This is the server-side half of "logPlay() populates the home Play Log".
/// </summary>
public class GameLogFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private static (WebSocketHandler handler, ConnectionManager connections, LobbyManager lobbies, TokenService tokens)
        BuildServer()
    {
        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var catalog = new GameCatalog(Path.GetTempPath(), NullLogger<GameCatalog>.Instance);
        var tokens = new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance);
        var limits = ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());
        var handler = new WebSocketHandler(connections, lobbies, catalog, tokens, limits,
            new MutableTimeProvider(Now), NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance);
        return (handler, connections, lobbies, tokens);
    }

    private static async Task<List<IMessage?>> DriveAsync(string senderId, GameLogMessage frame)
    {
        var (handler, connections, lobbies, tokens) = BuildServer();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;

        Assert.True(lobbies.TryCreate("tic-tac-toe", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        // The sender's CONTROL socket is where the forwarded entry must land.
        var ctrlSock = new ScriptedWebSocket();
        var ctrl = new Connection(senderId, senderId, ctrlSock, NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(ctrl);
        var ctrlLoop = ctrl.SendLoopAsync(ct);

        // Drive the sender's DATA socket: Attach then the game's GameLog frame.
        var ticket = tokens.IssueTicket(senderId, lobby.Id, "tic-tac-toe");
        var dataSock = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new AttachMessage(ticket)),
            ConnectionManager.Serialize(frame),
        ]);
        await handler.HandleAsync(dataSock, "http://game.local", ct);

        ctrl.CompleteOutbound();
        await ctrlLoop;

        return ctrlSock.Sent
            .Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage))
            .ToList();
    }

    [Fact]
    public async Task Host_game_log_is_forwarded_to_control_with_context_stamped()
    {
        var received = await DriveAsync("host", new GameLogMessage(
            new Dictionary<string, string> { ["placement"] = "1", ["foo"] = "bar" }));

        var entry = Assert.IsType<GameLogMessage>(
            Assert.Single(received, m => m is GameLogMessage));
        Assert.Equal("tic-tac-toe", entry.GameId);   // resolved from the lobby, not the game
        Assert.Equal(Now, entry.Timestamp);           // server clock, not the game's
        Assert.Equal(true, entry.IsHost);
        Assert.Equal("1", entry.Metadata["placement"]);
        Assert.Equal("bar", entry.Metadata["foo"]);
    }

    [Fact]
    public async Task Guest_game_log_is_stamped_is_host_false()
    {
        var received = await DriveAsync("guest", new GameLogMessage(
            new Dictionary<string, string> { ["result"] = "lost" }));

        var entry = Assert.IsType<GameLogMessage>(Assert.Single(received, m => m is GameLogMessage));
        Assert.Equal(false, entry.IsHost);
        Assert.Equal("lost", entry.Metadata["result"]);
    }

    [Fact]
    public async Task Untrusted_metadata_is_sanitized_and_capped()
    {
        var bag = new Dictionary<string, string>
        {
            ["forge\nline"] = "value\r\ninjected",          // control chars stripped from key and value
            ["   "] = "dropped",                            // whitespace-only key dropped
            ["big"] = new string('x', WebSocketHandler.MaxPlayLogValueLength + 50),
        };
        for (var i = 0; i < WebSocketHandler.MaxPlayLogEntries + 10; i++) bag[$"k{i}"] = "v";

        var received = await DriveAsync("host", new GameLogMessage(bag));
        var entry = Assert.IsType<GameLogMessage>(Assert.Single(received, m => m is GameLogMessage));

        Assert.True(entry.Metadata.Count <= WebSocketHandler.MaxPlayLogEntries);
        Assert.DoesNotContain(entry.Metadata.Keys, k => k.Contains('\n') || string.IsNullOrWhiteSpace(k));
        Assert.All(entry.Metadata.Values, v => Assert.True(v.Length <= WebSocketHandler.MaxPlayLogValueLength));
        Assert.DoesNotContain(entry.Metadata.Values, v => v.Contains('\n') || v.Contains('\r'));
    }

    /// <summary>Minimal in-memory WebSocket: replays scripted inbound frames, captures outbound ones.</summary>
    private sealed class ScriptedWebSocket(IEnumerable<byte[]>? inbound = null) : WebSocket
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
                if (_state == WebSocketState.Open) _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            var msg = _inbound.Dequeue();
            msg.CopyTo(buffer.Array!, buffer.Offset);
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
