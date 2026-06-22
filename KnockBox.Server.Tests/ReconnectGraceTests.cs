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
/// Drives the REAL <see cref="WebSocketHandler"/> over fake sockets to prove the reconnect-grace
/// behaviour: a dropped CONTROL socket flags the member disconnected (keeping them in the lobby and
/// the lobby alive), peers are told on both planes, a reconnect within the window restores them with
/// no churn, and the reaper removes only those whose grace truly elapsed. With grace disabled the
/// old immediate-leave behaviour is preserved.
/// </summary>
public class ReconnectGraceTests
{
    private const string GameOrigin = "http://game.local";
    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    private static (WebSocketHandler handler, ConnectionManager connections, LobbyManager lobbies,
        TokenService tokens, MutableTimeProvider time) BuildServer(string? graceSeconds = null)
    {
        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var catalog = new GameCatalog(Path.GetTempPath(), NullLogger<GameCatalog>.Instance);
        var tokens = new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance);
        var config = graceSeconds is null
            ? new ConfigurationBuilder().Build()
            : ConfigFactory.FromPairs(("KnockBox:DisconnectGraceSeconds", graceSeconds));
        var limits = ServerLimits.FromConfiguration(config);
        var time = new MutableTimeProvider(Now);
        var handler = new WebSocketHandler(connections, lobbies, catalog, tokens, limits, time,
            NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance);
        return (handler, connections, lobbies, tokens, time);
    }

    // Registers a live (never-closing) control + game socket pair for a member, so broadcasts to them
    // are captured. Returns the capture lists and a flush action to drain the send loops.
    private static (List<byte[]> ctrl, List<byte[]> game, Func<Task> flush) Observe(
        ConnectionManager connections, string playerId, CancellationToken ct)
    {
        var ctrlSock = new ScriptedWebSocket();
        var ctrl = new Connection(playerId, playerId, ctrlSock, NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(ctrl);
        var ctrlLoop = ctrl.SendLoopAsync(ct);

        var gameSock = new ScriptedWebSocket();
        var game = new Connection(playerId, playerId, gameSock, NullLogger<Connection>.Instance);
        connections.AddGame(game);
        var gameLoop = game.SendLoopAsync(ct);

        return (ctrlSock.Sent, gameSock.Sent, async () =>
        {
            ctrl.CompleteOutbound();
            game.CompleteOutbound();
            await ctrlLoop;
            await gameLoop;
        });
    }

    // Drives a member's CONTROL socket through a realistic Hello + Rejoin, then lets it close (no
    // more scripted frames) so RunControlAsync's finally runs the disconnect handling.
    private static Task DriveControlConnectThenDrop(
        WebSocketHandler handler, TokenService tokens, string playerId, string lobbyId, CancellationToken ct)
    {
        var sock = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage(null, playerId, tokens.IssueIdentity(playerId))),
            ConnectionManager.Serialize(new RejoinLobbyMessage("c1", lobbyId)),
        ]);
        return handler.HandleAsync(sock, GameOrigin, ct);
    }

    private static List<IMessage?> Decode(IEnumerable<byte[]> frames) =>
        frames.Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage)).ToList();

    [Fact]
    public async Task Control_drop_flags_disconnected_keeps_member_and_notifies_peers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, connections, lobbies, tokens, _) = BuildServer();

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        var host = Observe(connections, "host", ct);

        await DriveControlConnectThenDrop(handler, tokens, "guest", lobby.Id, ct);
        await host.flush();

        Assert.Contains(Decode(host.ctrl), m => m is PlayerDisconnectedMessage d && d.PlayerId == "guest" && d.LobbyId == lobby.Id);
        Assert.Contains(Decode(host.game), m => m is GamePlayerDisconnectedMessage d && d.PlayerId == "guest");
        Assert.DoesNotContain(Decode(host.ctrl), m => m is PlayerLeftMessage);   // not a hard leave
        Assert.True(lobby.Contains("guest"));                                    // still a member
        Assert.NotNull(lobbies.Get(lobby.Id));                                   // lobby survives
    }

    [Fact]
    public async Task Reconnect_within_grace_announces_connected_without_leaving()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, connections, lobbies, tokens, _) = BuildServer();

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        var host = Observe(connections, "host", ct);

        // Drop, then reconnect (a second Hello + Rejoin) within the grace window.
        await DriveControlConnectThenDrop(handler, tokens, "guest", lobby.Id, ct);
        await DriveControlConnectThenDrop(handler, tokens, "guest", lobby.Id, ct);
        await host.flush();

        var ctrl = Decode(host.ctrl);
        Assert.Contains(ctrl, m => m is PlayerConnectedMessage c && c.PlayerId == "guest" && c.LobbyId == lobby.Id);
        Assert.Contains(Decode(host.game), m => m is GamePlayerConnectedMessage c && c.PlayerId == "guest");
        Assert.DoesNotContain(ctrl, m => m is PlayerLeftMessage);   // never a hard leave
        Assert.True(lobby.Contains("guest"));
    }

    [Fact]
    public async Task Reaper_removes_member_after_grace_and_notifies_peers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, connections, lobbies, tokens, time) = BuildServer();

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        var host = Observe(connections, "host", ct);

        await DriveControlConnectThenDrop(handler, tokens, "guest", lobby.Id, ct);
        Assert.True(lobby.Contains("guest")); // held during grace

        time.Advance(TimeSpan.FromSeconds(61)); // past the default 60s grace
        handler.ReapDisconnectedPlayers();
        await host.flush();

        Assert.Contains(Decode(host.ctrl), m => m is PlayerLeftMessage l && l.PlayerId == "guest");
        Assert.Contains(Decode(host.game), m => m is GamePlayerLeftMessage l && l.PlayerId == "guest");
        Assert.False(lobby.Contains("guest"));         // evicted
        Assert.NotNull(lobbies.Get(lobby.Id));         // host remains, so lobby stays
    }

    [Fact]
    public async Task Solo_member_disconnect_closes_lobby_immediately()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, _, lobbies, tokens, _) = BuildServer();

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));

        // A lone host with nobody else connected drops: the lobby is "dark", so there's no live game
        // to reconnect to — it closes at once rather than being held for the grace window.
        await DriveControlConnectThenDrop(handler, tokens, "host", lobby.Id, ct);

        Assert.Null(lobbies.Get(lobby.Id)); // gone immediately — no grace hold, no reaper needed
    }

    [Fact]
    public async Task Leave_that_strands_only_disconnected_members_closes_lobby()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, _, lobbies, tokens, _) = BuildServer();

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        // The guest is mid-grace (disconnected). Nothing has closed the lobby yet.
        Assert.True(lobby.MarkDisconnected("guest", Now));
        Assert.NotNull(lobbies.Get(lobby.Id));

        // The host connects and then explicitly leaves (Leave/home button → LeaveLobby). Only a
        // disconnected guest would remain, so the lobby is now dark and must close rather than
        // linger for the guest's grace window.
        var sock = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage(null, "host", tokens.IssueIdentity("host"))),
            ConnectionManager.Serialize(new RejoinLobbyMessage("c1", lobby.Id)),
            ConnectionManager.Serialize(new LeaveLobbyMessage(lobby.Id)),
        ]);
        await handler.HandleAsync(sock, GameOrigin, ct);

        Assert.Null(lobbies.Get(lobby.Id));
    }

    [Fact]
    public async Task Reaper_keeps_a_member_who_reconnected_before_the_sweep()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, connections, lobbies, tokens, time) = BuildServer();

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        // Keep the host connected so the lobby isn't "dark" when the guest drops (it survives).
        Observe(connections, "host", ct);
        await DriveControlConnectThenDrop(handler, tokens, "guest", lobby.Id, ct);

        // Race: the guest already has a fresh, live control connection by the time the sweep runs.
        var liveGuest = new Connection("guest", "Guest", new FakeWebSocket(), NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(liveGuest);

        time.Advance(TimeSpan.FromSeconds(120));
        handler.ReapDisconnectedPlayers();

        Assert.True(lobby.Contains("guest"));               // not evicted — they're back
        Assert.Empty(lobby.ExpiredDisconnects(time.GetUtcNow())); // flag was cleared, not left stale
    }

    [Fact]
    public async Task Grace_disabled_removes_immediately_with_no_presence_events()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cts.Token;
        var (handler, connections, lobbies, tokens, _) = BuildServer(graceSeconds: "0");

        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));

        var host = Observe(connections, "host", ct);

        await DriveControlConnectThenDrop(handler, tokens, "guest", lobby.Id, ct);
        await host.flush();

        var ctrl = Decode(host.ctrl);
        Assert.Contains(ctrl, m => m is PlayerLeftMessage l && l.PlayerId == "guest"); // old behaviour
        Assert.DoesNotContain(ctrl, m => m is PlayerDisconnectedMessage);
        Assert.False(lobby.Contains("guest"));
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
