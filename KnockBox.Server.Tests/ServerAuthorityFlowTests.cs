using System.Net.WebSockets;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Design-§12b item 4: server-authority behavior through the REAL <see cref="WebSocketHandler"/> —
/// Ready shape, relay divert and envelope enforcement, owner powers and migration, and lifecycle
/// (creation failure, owner departure, dark close).
/// </summary>
public class ServerAuthorityFlowTests : IDisposable
{
    private const string GameOrigin = "https://games.example";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-authority-flow-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));

    public ServerAuthorityFlowTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Counter authority that also demonstrates owner succession: when the owner leaves, promote
    // the first remaining member (the design's kb.setOwner pattern).
    private const string Module = """
        export function createAuthority(kb) {
          let roster = [];
          let ownerId = null;
          let state = { count: 0 };
          return {
            init(players) { roster = players.map(p => p.id); ownerId = roster[0] ?? null; },
            applyIntent(fromId, action) {
              if (action.kind !== 'inc') return null;
              state.count += 1;
              return { count: state.count };
            },
            snapshot() { return state; },
            onPlayerJoined(p) { roster.push(p.id); return null; },
            onPlayerLeft(id) {
              roster = roster.filter(x => x !== id);
              if (id === ownerId && roster.length > 0) { ownerId = roster[0]; kb.setOwner(ownerId); }
              return null;
            },
          };
        }
        """;

    private (WebSocketHandler handler, ConnectionManager connections, LobbyManager lobbies,
        ServerAuthorityManager authorities, TokenService tokens, GameCatalog catalog, string gameId)
        BuildServer(string moduleSource = Module, params (string Key, string? Value)[] config)
    {
        var gameId = "authgame";
        var gameDir = Path.Combine(_root, gameId);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "GAME.json"),
            $$"""{ "id": "{{gameId}}", "name": "A", "entry": "index.html", "maxPlayers": 8, "serverAuthority": "authority.js" }""");
        File.WriteAllText(Path.Combine(gameDir, "index.html"), "<html></html>");
        File.WriteAllText(Path.Combine(gameDir, "authority.js"), moduleSource);

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var catalog = new GameCatalog(_root, NullLogger<GameCatalog>.Instance);
        catalog.Discover();
        var tokens = new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance);
        var cfg = ConfigFactory.FromPairs(config);
        var limits = ServerLimits.FromConfiguration(cfg);
        var authorities = TestAuthorities.Manager(connections, lobbies, gamesRoot: _root, config: cfg);
        var handler = new WebSocketHandler(connections, lobbies, catalog, authorities, tokens, limits,
            TimeProvider.System, NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance);
        return (handler, connections, lobbies, authorities, tokens, catalog, gameId);
    }

    /// <summary>Creates a server-authority lobby directly (membership + actor) — for tests whose
    /// subject is the data plane, not HandleCreateLobby itself.</summary>
    private static Lobby CreateAuthorityLobby(LobbyManager lobbies, ServerAuthorityManager authorities,
        GameCatalog catalog, string gameId, params string[] memberIds)
    {
        Assert.True(catalog.TryGet(gameId, out var manifest));
        Assert.True(lobbies.TryCreate(gameId, memberIds[0], 8, out var lobby, isServerAuthority: true));
        foreach (var id in memberIds) Assert.True(lobby.TryAdd(new Player(id, id)));
        Assert.True(authorities.TryStart(lobby, manifest, out var error), error);
        return lobby;
    }

    // Registers live capture sockets for a member (kept from ReconnectGraceTests' Observe pattern).
    private (List<byte[]> ctrl, List<byte[]> game, Func<Task> flush) Observe(ConnectionManager connections, string playerId)
    {
        var ctrlSock = new ScriptedWebSocket();
        var ctrl = new Connection(playerId, playerId, ctrlSock, NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(ctrl);
        var ctrlLoop = ctrl.SendLoopAsync(_cts.Token);

        var gameSock = new ScriptedWebSocket();
        var game = new Connection(playerId, playerId, gameSock, NullLogger<Connection>.Instance);
        connections.AddGame(game);
        var gameLoop = game.SendLoopAsync(_cts.Token);

        return (ctrlSock.Sent, gameSock.Sent, async () =>
        {
            ctrl.CompleteOutbound();
            game.CompleteOutbound();
            await ctrlLoop;
            await gameLoop;
        });
    }

    private Task DriveData(WebSocketHandler handler, TokenService tokens, string playerId, Lobby lobby,
        out List<byte[]> sent, params IMessage[] frames)
    {
        var script = new List<byte[]>
        {
            ConnectionManager.Serialize(new AttachMessage(tokens.IssueTicket(playerId, lobby.Id, lobby.GameId))),
        };
        script.AddRange(frames.Select(ConnectionManager.Serialize));
        var sock = new ScriptedWebSocket(script);
        sent = sock.Sent;
        return handler.HandleAsync(sock, GameOrigin, _cts.Token);
    }

    private static GameMessage Game(string to, string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return new GameMessage(to, doc.RootElement.Clone());
    }

    private static List<IMessage?> Decode(IEnumerable<byte[]> frames) =>
        frames.Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage)).ToList();

    private static async Task DrainActor(ServerAuthorityManager authorities, Lobby lobby)
    {
        Assert.True(authorities.TryGet(lobby.Id, out var actor));
        actor.Stop();
        await actor.Completion;
    }

    // ── Ready (§5c) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Ready_tells_every_client_guest_with_server_authority_and_the_owner_id()
    {
        var (handler, _, lobbies, authorities, tokens, catalog, gameId) = BuildServer();
        var lobby = CreateAuthorityLobby(lobbies, authorities, catalog, gameId, "creator", "guest");

        // Even the CREATOR is a guest in server mode — no client is ever told it is host.
        await DriveData(handler, tokens, "creator", lobby, out var creatorSent);
        var ready = Assert.IsType<ReadyMessage>(Decode(creatorSent).First(m => m is ReadyMessage));
        Assert.False(ready.IsHost);
        Assert.Equal("server", ready.Authority);
        Assert.Equal("creator", ready.OwnerId);
    }

    [Fact]
    public async Task Ready_in_a_host_lobby_is_unchanged_plus_the_new_fields()
    {
        var (handler, _, lobbies, _, tokens, _, _) = BuildServer();
        Assert.True(lobbies.TryCreate("plain", "creator", 4, out var lobby));
        lobby.TryAdd(new Player("creator", "creator"));

        await DriveData(handler, tokens, "creator", lobby, out var sent);
        var ready = Assert.IsType<ReadyMessage>(Decode(sent).First(m => m is ReadyMessage));
        Assert.True(ready.IsHost);
        Assert.Equal("host", ready.Authority);
        Assert.Equal("creator", ready.OwnerId);
    }

    // ── Relay divert + envelope enforcement (§5a/§5d) ────────────────────────

    [Fact]
    public async Task Intent_to_host_reaches_the_module_and_never_the_creators_socket()
    {
        var (handler, connections, lobbies, authorities, tokens, catalog, gameId) = BuildServer();
        var lobby = CreateAuthorityLobby(lobbies, authorities, catalog, gameId, "creator", "guest");
        var creator = Observe(connections, "creator");

        await DriveData(handler, tokens, "guest", lobby, out _,
            Game("host", """{"_kb":"intent","action":{"kind":"inc"}}"""));
        await DrainActor(authorities, lobby);
        await creator.flush();

        var games = Decode(creator.game).OfType<GameMessage>().ToList();
        var delta = Assert.Single(games);
        Assert.Equal("server", delta.From); // the module answered…
        Assert.Equal("delta", delta.Payload.GetProperty("_kb").GetString());
        Assert.DoesNotContain(games, g => g.From == "guest"); // …and the raw intent never relayed
    }

    [Fact]
    public async Task Non_kb_payload_to_host_is_dropped_in_server_mode()
    {
        var (handler, connections, lobbies, authorities, tokens, catalog, gameId) = BuildServer();
        var lobby = CreateAuthorityLobby(lobbies, authorities, catalog, gameId, "creator", "guest");
        var creator = Observe(connections, "creator");

        await DriveData(handler, tokens, "guest", lobby, out _, Game("host", """{"kind":"legacy-move"}"""));
        await DrainActor(authorities, lobby);
        await creator.flush();

        Assert.Empty(Decode(creator.game).OfType<GameMessage>()); // no relay, no module response
    }

    [Fact]
    public async Task Client_sent_delta_and_state_are_dropped_but_other_chatter_relays()
    {
        var (handler, connections, lobbies, authorities, tokens, catalog, gameId) = BuildServer();
        var lobby = CreateAuthorityLobby(lobbies, authorities, catalog, gameId, "creator", "guest");
        var creator = Observe(connections, "creator");

        await DriveData(handler, tokens, "guest", lobby, out _,
            Game("all", """{"_kb":"state","state":{"count":999}}"""),   // forgery — dropped
            Game("all", """{"_kb":"delta","patch":{"count":999}}"""),   // forgery — dropped
            Game("all", """{"emote":"wave"}"""),                         // ordinary chatter — relays
            Game("creator", """{"_kb":"state","state":{"count":999}}""")); // direct forgery — dropped
        await DrainActor(authorities, lobby);
        await creator.flush();

        var games = Decode(creator.game).OfType<GameMessage>().Where(g => g.From == "guest").ToList();
        var emote = Assert.Single(games);
        Assert.Equal("wave", emote.Payload.GetProperty("emote").GetString());
    }

    // ── Lifecycle (§6) ───────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_lobby_for_a_disabled_authority_fails_loudly_with_no_lobby_left()
    {
        var (handler, _, lobbies, authorities, tokens, _, gameId) =
            BuildServer(config: ("KnockBox:AuthorityEnabled", "false"));

        var sock = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage(null, "creator", tokens.IssueIdentity("creator"))),
            ConnectionManager.Serialize(new CreateLobbyMessage("c1", gameId)),
        ]);
        await handler.HandleAsync(sock, GameOrigin, _cts.Token);

        var sent = Decode(sock.Sent);
        Assert.Contains(sent, m => m is ErrorMessage e && e.Cid == "c1");
        Assert.DoesNotContain(sent, m => m is LobbyCreatedMessage);
        Assert.DoesNotContain(sent, m => m is EnterGameMessage);
        Assert.Empty(lobbies.Snapshot()); // never a half-alive lobby
        _ = authorities;
    }

    [Fact]
    public async Task Creating_a_lobby_for_a_broken_module_fails_loudly()
    {
        var (handler, _, lobbies, _, tokens, _, gameId) = BuildServer(moduleSource: "not javascript ((");

        var sock = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage(null, "creator", tokens.IssueIdentity("creator"))),
            ConnectionManager.Serialize(new CreateLobbyMessage("c1", gameId)),
        ]);
        await handler.HandleAsync(sock, GameOrigin, _cts.Token);

        Assert.Contains(Decode(sock.Sent), m => m is ErrorMessage e && e.Cid == "c1");
        Assert.Empty(lobbies.Snapshot());
    }

    [Fact]
    public async Task Create_via_control_works_and_dark_close_stops_the_actor()
    {
        var (handler, _, lobbies, authorities, tokens, _, gameId) = BuildServer();

        var sock = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage(null, "creator", tokens.IssueIdentity("creator"))),
            ConnectionManager.Serialize(new CreateLobbyMessage("c1", gameId)),
        ]);
        await handler.HandleAsync(sock, GameOrigin, _cts.Token);

        // Creation succeeded on the wire…
        var sent = Decode(sock.Sent);
        var created = Assert.IsType<LobbyCreatedMessage>(sent.First(m => m is LobbyCreatedMessage));
        Assert.Contains(sent, m => m is EnterGameMessage);
        // …and the socket closing left no connected member, so the dark-close chokepoint also
        // stopped the actor (the single normal-teardown path).
        Assert.Null(lobbies.Get(created.LobbyId));
        Assert.False(authorities.TryGet(created.LobbyId, out _));
    }

    [Fact]
    public async Task Owner_leaving_keeps_the_game_running_and_the_module_promotes_a_successor()
    {
        var (handler, connections, lobbies, authorities, tokens, catalog, gameId) = BuildServer();
        var lobby = CreateAuthorityLobby(lobbies, authorities, catalog, gameId, "creator", "guest");
        var guest = Observe(connections, "guest"); // keeps the lobby lit after the creator leaves

        // The creator leaves for good: their shell rejoins (binding this connection to the lobby)
        // and then explicitly leaves — an explicit leave is always immediate, no grace.
        var creatorCtrl = new ScriptedWebSocket(
        [
            ConnectionManager.Serialize(new HelloMessage("creator", "creator", tokens.IssueIdentity("creator"))),
            ConnectionManager.Serialize(new RejoinLobbyMessage("c1", lobby.Id)),
            ConnectionManager.Serialize(new LeaveLobbyMessage(lobby.Id)),
        ]);
        await handler.HandleAsync(creatorCtrl, GameOrigin, _cts.Token);

        Assert.NotNull(lobbies.Get(lobby.Id));   // THE GAME CONTINUES
        Assert.False(lobby.Contains("creator"));

        // An intent from the survivor is still answered by the authority. (Posted directly so the
        // reply lands on the observed socket — the relay divert itself is covered above.)
        Assert.True(authorities.TryGet(lobby.Id, out var actor));
        actor.PostIntent("guest", """{"_kb":"intent","action":{"kind":"inc"}}""");
        await DrainActor(authorities, lobby);
        await guest.flush();

        // The module promoted the guest via kb.setOwner: HostId moved, both planes were told.
        Assert.Equal("guest", lobby.HostId);
        Assert.Contains(Decode(guest.ctrl), m => m is OwnerChangedMessage o && o.OwnerId == "guest");
        Assert.Contains(Decode(guest.game), m => m is GameOwnerChangedMessage o && o.OwnerId == "guest");
        Assert.Contains(Decode(guest.game), m =>
            m is GameMessage g && g.From == "server" && g.Payload.GetProperty("_kb").GetString() == "delta");
    }

    [Fact]
    public async Task Owner_powers_follow_kb_setOwner()
    {
        var (handler, _, lobbies, authorities, tokens, catalog, gameId) = BuildServer("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent(fromId, action) {
                  if (action.kind === 'promote') { kb.setOwner(action.target); return { ok: true }; }
                  return null;
                },
                snapshot() { return {}; },
              };
            }
            """);
        var lobby = CreateAuthorityLobby(lobbies, authorities, catalog, gameId, "creator", "guest");

        // Before migration: the creator holds SetLobbyOpen; the guest is refused.
        await DriveData(handler, tokens, "guest", lobby, out _, new SetLobbyOpenMessage(false));
        Assert.True(lobby.Open);
        await DriveData(handler, tokens, "creator", lobby, out _, new SetLobbyOpenMessage(false));
        Assert.False(lobby.Open);
        lobby.Open = true;

        // The module migrates ownership to the guest.
        await DriveData(handler, tokens, "guest", lobby, out _,
            Game("host", """{"_kb":"intent","action":{"kind":"promote","target":"guest"}}"""));
        Assert.True(authorities.TryGet(lobby.Id, out var actor));
        // Wait for the actor to apply the effect without tearing it down (more powers to test).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (lobby.HostId != "guest" && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal("guest", lobby.HostId);

        // After migration: honored from the new owner, refused from the old one.
        await DriveData(handler, tokens, "creator", lobby, out _, new SetLobbyOpenMessage(false));
        Assert.True(lobby.Open);
        await DriveData(handler, tokens, "guest", lobby, out _, new SetLobbyOpenMessage(false));
        Assert.False(lobby.Open);

        actor.Stop();
        await actor.Completion;
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
