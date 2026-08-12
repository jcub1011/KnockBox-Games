using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The design-§7 error policy: a buggy-but-recoverable module keeps the lobby alive and converged;
/// anything that could corrupt authoritative state kills the lobby loudly rather than limping.
/// </summary>
public class ServerAuthorityErrorPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-authority-err-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));

    public ServerAuthorityErrorPolicyTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Rig
    {
        public required Lobby Lobby;
        public required ServerAuthority Actor;
        public required ServerAuthorityManager Manager;
        public required LobbyManager Lobbies;
        public required Connection Ctrl;
        public required Connection Game;
        public required FakeWebSocket CtrlSock;
        public required FakeWebSocket GameSock;
        public required List<Task> Loops;

        public async Task<(List<IMessage?> ctrl, List<IMessage?> game)> FlushAsync()
        {
            Ctrl.CompleteOutbound();
            Game.CompleteOutbound();
            foreach (var loop in Loops) await loop;
            return (Decode(CtrlSock.Sent), Decode(GameSock.Sent));
        }

        private static List<IMessage?> Decode(IEnumerable<byte[]> frames) =>
            frames.Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage)).ToList();
    }

    private Rig Start(string moduleSource, bool isDevelopment = false, params (string Key, string? Value)[] config)
    {
        var gameDir = Path.Combine(_root, "g-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "authority.js"), moduleSource);
        var gameId = new DirectoryInfo(gameDir).Name;
        var manifest = new GameManifest(gameId, gameId, "index.html", null, 8, ServerAuthority: "authority.js");

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var manager = new ServerAuthorityManager(id => Path.Combine(_root, id),
            AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs(config)),
            connections, new LobbyCloser(lobbies, connections, NullLogger<LobbyCloser>.Instance),
            TimeProvider.System,
            new AuthorityWordService(NullLogger<AuthorityWordService>.Instance),
            isDevelopment, NullLoggerFactory.Instance);

        Assert.True(lobbies.TryCreate(gameId, "p1", 8, out var lobby, isServerAuthority: true));
        Assert.True(lobby.TryAdd(new Player("p1", "p1")));

        var ctrlSock = new FakeWebSocket();
        var ctrl = new Connection("p1", "p1", ctrlSock, NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(ctrl);
        var gameSock = new FakeWebSocket();
        var game = new Connection("p1", "p1", gameSock, NullLogger<Connection>.Instance, OutboundOverflow.DropOldest);
        connections.AddGame(game);
        var loops = new List<Task> { ctrl.SendLoopAsync(_cts.Token), game.SendLoopAsync(_cts.Token) };

        Assert.True(manager.TryStart(lobby, manifest, out var error), error);
        Assert.True(manager.TryGet(lobby.Id, out var actor));
        return new Rig
        {
            Lobby = lobby, Actor = actor, Manager = manager, Lobbies = lobbies,
            Ctrl = ctrl, Game = game, CtrlSock = ctrlSock, GameSock = gameSock, Loops = loops,
        };
    }

    // Throws on 'boom' intents; succeeds on anything else.
    private const string FlakyModule = """
        export function createAuthority(kb) {
          let state = { count: 0 };
          return {
            init() {},
            applyIntent(fromId, action) {
              if (action.kind === 'boom') throw new Error('module bug');
              state.count += 1;
              return { count: state.count };
            },
            snapshot() { return state; },
          };
        }
        """;

    [Fact]
    public async Task Contained_throw_drops_the_intent_resyncs_and_keeps_the_lobby_alive()
    {
        var rig = Start(FlakyModule);
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"boom"}}""");
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"fine"}}""");
        rig.Actor.Stop();
        await rig.Actor.Completion;
        var (_, game) = await rig.FlushAsync();

        var frames = game.OfType<GameMessage>().Where(g => g.From == "server").ToList();
        // Frame 1: the convergence re-sync after the throw (unchanged state). Frame 2: the next
        // intent's delta — the engine survived. No delta was ever sent for the failed intent.
        Assert.Equal(2, frames.Count);
        Assert.Equal("state", frames[0].Payload.GetProperty("_kb").GetString());
        Assert.Equal(0, frames[0].Payload.GetProperty("state").GetProperty("count").GetInt32());
        Assert.Equal("delta", frames[1].Payload.GetProperty("_kb").GetString());
        Assert.Equal(1, frames[1].Payload.GetProperty("patch").GetProperty("count").GetInt32());

        Assert.NotNull(rig.Lobbies.Get(rig.Lobby.Id)); // alive
    }

    [Fact]
    public async Task An_unserializable_result_is_contained_not_fatal()
    {
        // The module's call SUCCEEDS; what fails is turning its result into JSON (a back-reference here).
        // Because serialization used to sit outside the classifying try/catch, that arrived at the actor as
        // an unclassified exception and took the whole lobby down on the first occurrence — where the
        // documented policy is to drop the work, re-broadcast the unchanged snapshot, and tolerate five.
        var rig = Start("""
            export function createAuthority(kb) {
              let state = { count: 0 };
              return {
                init() {},
                applyIntent(fromId, action) {
                  if (action.kind !== 'cycle') { state.count += 1; return { count: state.count }; }
                  const patch = { count: state.count };
                  patch.self = patch;
                  return patch;
                },
                snapshot() { return state; },
              };
            }
            """);
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"cycle"}}""");
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"fine"}}""");
        rig.Actor.Stop();
        await rig.Actor.Completion;
        var (ctrl, game) = await rig.FlushAsync();

        var frames = game.OfType<GameMessage>().Where(g => g.From == "server").ToList();
        Assert.Equal(2, frames.Count);
        Assert.Equal("state", frames[0].Payload.GetProperty("_kb").GetString()); // convergence re-sync
        Assert.Equal("delta", frames[1].Payload.GetProperty("_kb").GetString()); // the engine survived
        Assert.Empty(ctrl.OfType<LobbyClosedMessage>());
        Assert.NotNull(rig.Lobbies.Get(rig.Lobby.Id));
    }

    [Fact]
    public async Task Five_consecutive_contained_failures_escalate_to_fatal()
    {
        var rig = Start(FlakyModule);
        for (var i = 0; i < 5; i++)
            rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"boom"}}""");
        await rig.Actor.Completion; // the fatal path breaks the loop without Stop()
        var (ctrl, _) = await rig.FlushAsync();

        var closed = Assert.Single(ctrl.OfType<LobbyClosedMessage>());
        Assert.Equal((rig.Lobby.Id, "authority-failed"), (closed.LobbyId, closed.Reason));
        Assert.Null(rig.Lobbies.Get(rig.Lobby.Id));
        Assert.False(rig.Manager.TryGet(rig.Lobby.Id, out _));
    }

    [Fact]
    public async Task A_success_resets_the_consecutive_failure_counter()
    {
        var rig = Start(FlakyModule);
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 4; i++) // stay under the 5-in-a-row threshold…
                rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"boom"}}""");
            rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"fine"}}"""); // …then reset
        }
        rig.Actor.Stop();
        await rig.Actor.Completion;

        Assert.NotNull(rig.Lobbies.Get(rig.Lobby.Id)); // 12 failures total, never 5 consecutive
    }

    [Fact]
    public async Task Infinite_loop_closes_the_lobby_and_aborts_game_sockets()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { for (;;) {} },
                snapshot() { return {}; },
              };
            }
            """, config: ("KnockBox:AuthorityCallTimeoutMs", "150"));
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{}}""");
        await rig.Actor.Completion;
        var (ctrl, _) = await rig.FlushAsync();

        Assert.Single(ctrl.OfType<LobbyClosedMessage>());
        Assert.Null(rig.Lobbies.Get(rig.Lobby.Id));
        Assert.False(rig.Manager.TryGet(rig.Lobby.Id, out _));
    }

    [Fact]
    public async Task Memory_bomb_closes_the_lobby()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { const a = []; for (;;) a.push(new Array(4096).fill(0)); },
                snapshot() { return {}; },
              };
            }
            """, config: ("KnockBox:AuthorityMaxMemoryBytes", "4194304"));
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{}}""");
        await rig.Actor.Completion;
        var (ctrl, _) = await rig.FlushAsync();

        var closed = Assert.Single(ctrl.OfType<LobbyClosedMessage>());
        Assert.Equal("authority-failed", closed.Reason);
        Assert.Null(rig.Lobbies.Get(rig.Lobby.Id));
    }

    [Fact]
    public async Task Development_relays_a_contained_error_to_the_lobby_production_does_not()
    {
        foreach (var isDevelopment in new[] { true, false })
        {
            var rig = Start(FlakyModule, isDevelopment: isDevelopment);
            rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"boom"}}""");
            rig.Actor.Stop();
            await rig.Actor.Completion;
            var (_, game) = await rig.FlushAsync();

            var errors = game.OfType<GameMessage>()
                .Where(g => g.From == "server" && g.Payload.TryGetProperty("_kb", out var k) && k.GetString() == "error")
                .ToList();
            if (isDevelopment)
            {
                var error = Assert.Single(errors);
                Assert.Contains("module bug", error.Payload.GetProperty("message").GetString());
            }
            else
            {
                Assert.Empty(errors); // no internals leak to clients in production
            }
        }
    }

    [Fact]
    public async Task Load_failure_fails_TryStart_with_a_clear_error()
    {
        var gameDir = Path.Combine(_root, "broken");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "authority.js"), "this is not javascript ((");
        var manifest = new GameManifest("broken", "B", "index.html", null, 4, ServerAuthority: "authority.js");

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var manager = TestAuthorities.Manager(connections, lobbies, gamesRoot: _root);
        Assert.True(lobbies.TryCreate("broken", "p1", 4, out var lobby, isServerAuthority: true));
        lobby.TryAdd(new Player("p1", "p1"));

        Assert.False(manager.TryStart(lobby, manifest, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.False(manager.TryGet(lobby.Id, out _));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Disabled_authority_fails_TryStart_loudly()
    {
        var rigless = TestAuthorities.Manager(new ConnectionManager(), new LobbyManager(),
            gamesRoot: _root, config: ConfigFactory.FromPairs(("KnockBox:AuthorityEnabled", "false")));
        var lobby = new Lobby("AB12", "g", "p1", 4, DateTimeOffset.UnixEpoch, isServerAuthority: true);
        Assert.False(rigless.TryStart(lobby, new GameManifest("g", "G", "index.html", null, 4, ServerAuthority: "authority.js"), out var error));
        Assert.Contains("disabled", error, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Max_lobbies_cap_refuses_the_next_lobby()
    {
        var gameDir = Path.Combine(_root, "capped");
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "authority.js"),
            "export function createAuthority(kb){return{init(){},applyIntent(){return null;},snapshot(){return{};}};}");
        var manifest = new GameManifest("capped", "C", "index.html", null, 4, ServerAuthority: "authority.js");

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var manager = TestAuthorities.Manager(connections, lobbies, gamesRoot: _root,
            config: ConfigFactory.FromPairs(("KnockBox:AuthorityMaxLobbies", "1")));

        Assert.True(lobbies.TryCreate("capped", "p1", 4, out var first, isServerAuthority: true));
        first.TryAdd(new Player("p1", "p1"));
        Assert.True(manager.TryStart(first, manifest, out _));

        Assert.True(lobbies.TryCreate("capped", "p2", 4, out var second, isServerAuthority: true));
        second.TryAdd(new Player("p2", "p2"));
        Assert.False(manager.TryStart(second, manifest, out var error));
        Assert.Contains("limit", error, StringComparison.OrdinalIgnoreCase);

        // Closing the first frees the slot.
        manager.Stop(first.Id);
        Assert.True(manager.TryStart(second, manifest, out _));
        await Task.CompletedTask;
    }
}
