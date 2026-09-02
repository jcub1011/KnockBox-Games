using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The hand-written outbound frame must be the frame the source-generated serializer would produce for the
/// equivalent <see cref="GameMessage"/>. Writing it directly is what removes the parse + deep-clone +
/// re-serialize round trip from the tick path; this is the check that keeps "directly" from meaning
/// "differently".
/// </summary>
public class ServerAuthorityFrameTests
{
    private static byte[] ViaSerializer(string to, string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return ConnectionManager.Serialize(new GameMessage(to, doc.RootElement.Clone(), From: "server"));
    }

    [Theory]
    [InlineData("all", """{"_kb":"state","state":{"count":1}}""")]
    [InlineData("p1", """{"_kb":"delta","patch":{"a":[1,2,{"b":null}]}}""")]
    [InlineData("p-with-\"quote\"", """{"_kb":"error","message":"it broke"}""")]
    [InlineData("all", """{"_kb":"state","state":null}""")]
    [InlineData("all", """{"_kb":"state","state":{"deep":{"er":{"est":[[],{},""]}}}}""")]
    public void Matches_the_serializer_byte_for_byte_for_ascii_payloads(string to, string payloadJson) =>
        Assert.Equal(ViaSerializer(to, payloadJson), ServerAuthority.SerializeGameFrame(to, payloadJson));

    [Theory]
    [InlineData("all", """{"_kb":"state","state":{"unicode":"héllo ✓","esc":"line\nbreak"}}""")]
    [InlineData("all", """{"_kb":"state","state":{"html":"<b>&'x'</b>","plus":"a+b"}}""")]
    public void Copies_the_payload_verbatim_rather_than_re_encoding_it(string to, string payloadJson)
    {
        // The one deliberate difference: the round trip re-encoded string contents with
        // JavaScriptEncoder.Default (non-ASCII and HTML-sensitive characters as \uXXXX); copying the
        // payload raw keeps whatever the module's serializer wrote. Same JSON value either way, and these
        // frames go into a WebSocket rather than into markup — which is the only thing that escaping buys.
        var frame = ServerAuthority.SerializeGameFrame(to, payloadJson);
        Assert.True(frame.Length < ViaSerializer(to, payloadJson).Length, "the verbatim copy should be shorter");

        // What matters is that both decode to the same message. Compared as VALUES, not raw text: the
        // escaping is exactly what differs, and é and é are the same character to any parser.
        var mine = Assert.IsType<GameMessage>(
            JsonSerializer.Deserialize(frame, KnockBoxProtocolContext.Default.IMessage));
        var theirs = Assert.IsType<GameMessage>(
            JsonSerializer.Deserialize(ViaSerializer(to, payloadJson), KnockBoxProtocolContext.Default.IMessage));
        Assert.Equal(theirs.To, mine.To);
        Assert.Equal(theirs.From, mine.From);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(mine.Payload.GetRawText()),
            System.Text.Json.Nodes.JsonNode.Parse(theirs.Payload.GetRawText())));
    }
}

/// <summary>
/// Actor unit tests (design §12b item 2): inline module sources, real Jint, real
/// ConnectionManager fan-out captured on fake sockets. Determinism: post work, Stop() the actor,
/// await Completion (the channel drains fully before the task ends), then flush the capture
/// connections and decode.
/// </summary>
public class ServerAuthorityActorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-actor-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));

    public ServerAuthorityActorTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Mutable clock that also records CreateTimer calls (to assert tick-timer existence).</summary>
    private sealed class RecordingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public List<TimeSpan> TimerPeriods { get; } = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimerPeriods.Add(period);
            return new NoopTimer(); // ticks are driven explicitly via RequestTick in tests
        }
        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class Member
    {
        private readonly Connection _ctrl;
        private readonly Connection _game;
        private readonly FakeWebSocket _ctrlSock = new();
        private readonly FakeWebSocket _gameSock = new();
        private readonly List<Task> _loops = [];

        public Member(ConnectionManager connections, string id)
        {
            _ctrl = new Connection(id, id, _ctrlSock, NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
            connections.Add(_ctrl);
            _game = new Connection(id, id, _gameSock, NullLogger<Connection>.Instance, OutboundOverflow.DropOldest);
            connections.AddGame(_game);
        }

        public void Start(CancellationToken token) =>
            _loops.AddRange([_ctrl.SendLoopAsync(token), _game.SendLoopAsync(token)]);

        public async Task<(List<IMessage?> ctrl, List<IMessage?> game)> FlushAsync()
        {
            _ctrl.CompleteOutbound();
            _game.CompleteOutbound();
            foreach (var loop in _loops) await loop;
            return (Decode(_ctrlSock.Sent), Decode(_gameSock.Sent));
        }

        private static List<IMessage?> Decode(IEnumerable<byte[]> frames) =>
            frames.Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage)).ToList();
    }

    private sealed class Rig
    {
        public required Lobby Lobby;
        public required ServerAuthority Actor;
        public required ServerAuthorityManager Manager;
        public required ConnectionManager Connections;
        public required Dictionary<string, Member> Members;
        public required RecordingTimeProvider Time;
        public required RecordingLoggerFactory Logs;

        public async Task<Dictionary<string, (List<IMessage?> ctrl, List<IMessage?> game)>> StopAndFlushAsync()
        {
            Actor.Stop();
            await Actor.Completion;
            var result = new Dictionary<string, (List<IMessage?>, List<IMessage?>)>();
            foreach (var (id, member) in Members) result[id] = await member.FlushAsync();
            return result;
        }
    }

    private Rig Start(string moduleSource, string[] memberIds, params (string Key, string? Value)[] config)
    {
        var gameDir = Path.Combine(_root, "g-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "authority.js"), moduleSource);
        var gameId = new DirectoryInfo(gameDir).Name;
        var manifest = new GameManifest(gameId, gameId, "index.html", null, 8, ServerAuthority: "authority.js");

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var time = new RecordingTimeProvider(Now);
        var logs = new RecordingLoggerFactory();
        var manager = TestAuthorities.Manager(connections, lobbies, gamesRoot: _root,
            config: ConfigFactory.FromPairs(config), time: time, loggerFactory: logs);

        Assert.True(lobbies.TryCreate(gameId, memberIds[0], 8, out var lobby, isServerAuthority: true));
        foreach (var id in memberIds) Assert.True(lobby.TryAdd(new Player(id, id)));

        var members = new Dictionary<string, Member>();
        foreach (var id in memberIds)
        {
            var member = new Member(connections, id);
            member.Start(_cts.Token);
            members[id] = member;
        }

        Assert.True(manager.TryStart(lobby, manifest, out var error), error);
        Assert.True(manager.TryGet(lobby.Id, out var actor));
        return new Rig
        {
            Lobby = lobby, Actor = actor, Manager = manager, Connections = connections,
            Members = members, Time = time, Logs = logs,
        };
    }

    private const string CounterModule = """
        export function createAuthority(kb) {
          let state = null;
          return {
            init(players) { state = { count: 0, ids: players.map(p => p.id) }; },
            applyIntent(fromId, action) {
              if (action.kind !== 'inc') return null;
              state.count += 1;
              return { count: state.count, by: fromId };
            },
            snapshot() { return state; },
            onPlayerJoined(p) { state.ids.push(p.id); return null; },
            onPlayerLeft(id) { state.ids = state.ids.filter(x => x !== id); return null; },
          };
        }
        """;

    private static JsonElement Payload(IMessage? m) => Assert.IsType<GameMessage>(m).Payload;

    private static List<GameMessage> ServerFrames(List<IMessage?> game) =>
        game.OfType<GameMessage>().Where(g => g.From == "server").ToList();

    [Fact]
    public async Task Intent_broadcasts_a_delta_from_server_to_every_member()
    {
        var rig = Start(CounterModule, ["p1", "p2"]);
        rig.Actor.PostIntent("p2", """{"_kb":"intent","action":{"kind":"inc"}}""");
        var frames = await rig.StopAndFlushAsync();

        foreach (var id in new[] { "p1", "p2" })
        {
            var delta = Assert.Single(ServerFrames(frames[id].game));
            Assert.Equal("all", delta.To);
            Assert.Equal("delta", delta.Payload.GetProperty("_kb").GetString());
            Assert.Equal(1, delta.Payload.GetProperty("patch").GetProperty("count").GetInt32());
            Assert.Equal("p2", delta.Payload.GetProperty("patch").GetProperty("by").GetString());
        }
    }

    [Fact]
    public async Task Rejected_intent_sends_nothing()
    {
        var rig = Start(CounterModule, ["p1", "p2"]);
        rig.Actor.PostIntent("p2", """{"_kb":"intent","action":{"kind":"bogus"}}""");
        var frames = await rig.StopAndFlushAsync();

        Assert.Empty(ServerFrames(frames["p1"].game));
        Assert.Empty(ServerFrames(frames["p2"].game));
    }

    [Fact]
    public async Task Sync_sends_state_to_the_requester_only()
    {
        var rig = Start(CounterModule, ["p1", "p2"]);
        rig.Actor.PostIntent("p2", """{"_kb":"sync"}""");
        var frames = await rig.StopAndFlushAsync();

        var state = Assert.Single(ServerFrames(frames["p2"].game));
        Assert.Equal("p2", state.To);
        Assert.Equal("state", state.Payload.GetProperty("_kb").GetString());
        Assert.Equal(0, state.Payload.GetProperty("state").GetProperty("count").GetInt32());
        Assert.Empty(ServerFrames(frames["p1"].game));
    }

    [Fact]
    public async Task Per_recipient_mode_projects_a_distinct_state_per_member()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              let secrets = null;
              return {
                init(players) { secrets = Object.fromEntries(players.map((p, i) => [p.id, 'secret-' + i])); },
                applyIntent(fromId, action) { return true; },  // truthy = accepted; host re-projects
                snapshot(forPlayerId) { return { yours: secrets[forPlayerId] ?? null }; },
              };
            }
            export const config = { perRecipient: true };
            """, ["p1", "p2"]);
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{}}""");
        var frames = await rig.StopAndFlushAsync();

        var s1 = Assert.Single(ServerFrames(frames["p1"].game));
        var s2 = Assert.Single(ServerFrames(frames["p2"].game));
        Assert.Equal("state", s1.Payload.GetProperty("_kb").GetString());
        Assert.Equal("secret-0", s1.Payload.GetProperty("state").GetProperty("yours").GetString());
        Assert.Equal("secret-1", s2.Payload.GetProperty("state").GetProperty("yours").GetString());
        Assert.Equal("p1", s1.To);
        Assert.Equal("p2", s2.To);
    }

    [Fact]
    public async Task Roster_join_runs_the_hook_and_rebroadcasts_state_to_all()
    {
        var rig = Start(CounterModule, ["p1", "p2"]);
        rig.Lobby.TryAdd(new Player("p3", "p3"));
        rig.Actor.PostPlayerJoined(new Player("p3", "p3"));
        var frames = await rig.StopAndFlushAsync();

        var state = Assert.Single(ServerFrames(frames["p1"].game));
        Assert.Equal("state", state.Payload.GetProperty("_kb").GetString());
        var ids = state.Payload.GetProperty("state").GetProperty("ids").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["p1", "p2", "p3"], ids); // the hook saw the join
    }

    [Fact]
    public async Task Tick_export_creates_a_clamped_timer_and_ticks_broadcast_patches()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              let t = 0;
              return {
                init() {},
                applyIntent() { return null; },
                snapshot() { return { t }; },
                tick(dtMs) { t += dtMs; return { t }; },
              };
            }
            export const config = { tickHz: 100 };
            """, ["p1"], ("KnockBox:AuthorityTickHzMax", "20"));

        // 100 Hz requested, clamped to the 20 Hz max → a 50 ms period timer.
        Assert.Equal(TimeSpan.FromMilliseconds(50), Assert.Single(rig.Time.TimerPeriods));

        rig.Time.Advance(TimeSpan.FromMilliseconds(50));
        rig.Actor.RequestTick();
        var frames = await rig.StopAndFlushAsync();

        var delta = Assert.Single(ServerFrames(frames["p1"].game));
        Assert.Equal(50, delta.Payload.GetProperty("patch").GetProperty("t").GetDouble());
    }

    [Fact]
    public void No_tick_export_means_no_timer_at_all()
    {
        var rig = Start(CounterModule, ["p1"], ("KnockBox:AuthorityTickHzMax", "20"));
        Assert.Empty(rig.Time.TimerPeriods);
    }

    [Fact]
    public async Task SetOwner_reassigns_the_lobby_owner_and_notifies_both_planes_after_the_delta()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent(fromId, action) {
                  if (action.kind === 'promote') { kb.setOwner(action.target); return { promoted: action.target }; }
                  return null;
                },
                snapshot() { return {}; },
              };
            }
            """, ["p1", "p2"]);
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"promote","target":"p2"}}""");
        var frames = await rig.StopAndFlushAsync();

        Assert.Equal("p2", rig.Lobby.HostId);
        foreach (var id in new[] { "p1", "p2" })
        {
            var owner = Assert.Single(frames[id].ctrl.OfType<OwnerChangedMessage>());
            Assert.Equal(("p2", rig.Lobby.Id), (owner.OwnerId, owner.LobbyId));

            // Ordering: the intent's delta lands before the owner event (design §3).
            var game = frames[id].game;
            var deltaIndex = game.FindIndex(m => m is GameMessage g && g.From == "server");
            var ownerIndex = game.FindIndex(m => m is GameOwnerChangedMessage);
            Assert.True(deltaIndex >= 0 && ownerIndex > deltaIndex,
                $"expected delta ({deltaIndex}) before GameOwnerChanged ({ownerIndex})");
        }
    }

    [Fact]
    public async Task SetOwner_to_a_non_member_is_ignored()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { kb.setOwner('stranger'); return { ok: true }; },
                snapshot() { return {}; },
              };
            }
            """, ["p1"]);
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{}}""");
        var frames = await rig.StopAndFlushAsync();

        Assert.Equal("p1", rig.Lobby.HostId);
        Assert.Empty(frames["p1"].ctrl.OfType<OwnerChangedMessage>());
        Assert.Single(ServerFrames(frames["p1"].game)); // the delta itself still went out
    }

    [Fact]
    public async Task SetLobbyOpen_effect_updates_the_lobby()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { kb.setLobbyOpen(false); return null; },
                snapshot() { return {}; },
              };
            }
            """, ["p1"]);
        Assert.True(rig.Lobby.Open);
        rig.Actor.PostIntent("p1", """{"_kb":"intent","action":{}}""");
        await rig.StopAndFlushAsync();

        Assert.False(rig.Lobby.Open);
    }

    [Fact]
    public async Task Oversized_outbound_frame_is_dropped()
    {
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { return null; },
                snapshot() { return { blob: 'x'.repeat(600000) }; },  // > the 512 KB wire cap
              };
            }
            """, ["p1"]);
        rig.Actor.PostIntent("p1", """{"_kb":"sync"}""");
        var frames = await rig.StopAndFlushAsync();

        Assert.Empty(ServerFrames(frames["p1"].game));
        Assert.True(rig.Manager.TryGet(rig.Lobby.Id, out _)); // dropped, not fatal
    }

    // ── Per-call budget overruns ─────────────────────────────────────────────────────
    //
    // A tick that blows the wall-clock budget used to close the lobby on the FIRST occurrence, which
    // made a game that was merely too slow on its worst turn indistinguishable from a game that was
    // broken — and disconnected everybody either way. Ticks are the one work item the actor already
    // treats as droppable (they coalesce, and RequestTick discards one outright when the channel is
    // full), so a single slow one is a hitch. A module that cannot get through a tick at all still dies.

    /// <summary>Overruns on its first N ticks, then behaves. The counter is bumped BEFORE the stall, so
    /// the test also shows the module's own state surviving a call that was killed part-way.</summary>
    private const string SlowTickModule = """
        export function createAuthority(kb) {
          let ticks = 0;
          const slowFor = SLOW_FOR;
          return {
            init() {},
            applyIntent() { return null; },
            snapshot() { return { ticks }; },
            tick(dtMs) {
              ticks += 1;
              if (ticks <= slowFor) { for (;;) {} }
              return { ticks };
            },
          };
        }
        export const config = { tickHz: 20 };
        """;

    private static string SlowTicks(int n) => SlowTickModule.Replace("SLOW_FOR", n.ToString());

    [Fact]
    public async Task One_tick_over_budget_is_dropped_and_the_lobby_survives()
    {
        var rig = Start(SlowTicks(1), ["p1"],
            ("KnockBox:AuthorityCallTimeoutMs", "120"),
            ("KnockBox:AuthorityTickHzMax", "20"));

        rig.Actor.RequestTick();                       // overruns and is dropped
        await Task.Delay(400, _cts.Token);             // comfortably past the 120 ms budget
        Assert.False(rig.Actor.Completion.IsCompleted, "the lobby should have survived one overrun");

        rig.Actor.RequestTick();                       // the engine is still usable
        await Task.Delay(200, _cts.Token);
        var frames = await rig.StopAndFlushAsync();

        var server = ServerFrames(frames["p1"].game);
        // The dropped tick re-broadcasts state to converge every client on whatever the module now
        // believes — its counter DID advance before the stall, which is exactly the partial state the
        // re-broadcast exists to publish. Then the next tick's delta shows the engine still alive.
        Assert.Equal("state", server[0].Payload.GetProperty("_kb").GetString());
        Assert.Equal(1, server[0].Payload.GetProperty("state").GetProperty("ticks").GetInt32());
        Assert.Equal("delta", server[^1].Payload.GetProperty("_kb").GetString());
        Assert.Equal(2, server[^1].Payload.GetProperty("patch").GetProperty("ticks").GetInt32());
    }

    [Fact]
    public async Task Consecutive_overruns_still_close_the_lobby()
    {
        // Tolerating one is not tolerating forever: a module that cannot finish a tick is broken, and
        // the actor must stop feeding it work rather than burning a core on it every 50 ms.
        var rig = Start(SlowTicks(99), ["p1"],
            ("KnockBox:AuthorityCallTimeoutMs", "80"),
            ("KnockBox:AuthorityMaxConsecutiveOverruns", "3"),
            ("KnockBox:AuthorityTickHzMax", "20"));

        // RequestTick coalesces, so ask repeatedly rather than three times in a row and assume.
        for (var i = 0; i < 40 && !rig.Actor.Completion.IsCompleted; i++)
        {
            rig.Actor.RequestTick();
            await Task.Delay(50, _cts.Token);
        }

        await rig.Actor.Completion;                    // the fatal path breaks the loop without Stop()
        Assert.False(rig.Manager.TryGet(rig.Lobby.Id, out _));
        Assert.Contains(rig.Logs.At(LogLevel.Error),
            m => m.Contains("times in a row", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_call_that_nears_its_budget_is_warned_about_before_anything_breaks()
    {
        // The signal whose absence let a fatal overrun ship. A game developer cannot see Jint cost from
        // a browser, so without this the first news that a module was near its budget was the lobby
        // closing. Warn below the budget, name the game, and say what the consequence is.
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { return null; },
                snapshot() { return {}; },
                tick(dtMs) {
                  // Real interpreted work, not a clock spin: kb.now() reads the injected TimeProvider,
                  // which this rig freezes so ticks are deterministic — a module waiting on it would
                  // wait forever. The warning measures wall time, so the tick has to actually spend it.
                  let n = 0;
                  for (let i = 0; i < 300000; i++) n += i;
                  return { ok: true, n };
                },
              };
            }
            export const config = { tickHz: 20 };
            """, ["p1"],
            // A budget far above what the tick needs, and a threshold far below it: the assertion is
            // about the warning firing at a FRACTION of budget, not about the tick being near failure.
            ("KnockBox:AuthorityCallTimeoutMs", "5000"),
            ("KnockBox:AuthoritySlowCallWarnFraction", "0.001"),
            ("KnockBox:AuthorityTickHzMax", "20"));

        rig.Actor.RequestTick();
        await Task.Delay(400, _cts.Token);
        await rig.StopAndFlushAsync();

        var warning = Assert.Single(rig.Logs.At(LogLevel.Warning),
            m => m.Contains("per-call budget", StringComparison.Ordinal));
        Assert.Contains("Exceeding it closes the lobby", warning, StringComparison.Ordinal);
        Assert.Contains(rig.Lobby.GameId, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_near_budget_warning_fires_on_each_new_worst_rather_than_every_call()
    {
        // A module that sits above the threshold every tick would otherwise emit 20 warnings a second at
        // the default tick rate, which buries the one line that mattered.
        var rig = Start("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { return null; },
                snapshot() { return {}; },
                tick(dtMs) { let n = 0; for (let i = 0; i < 300000; i++) n += i; return { ok: true, n }; },
              };
            }
            export const config = { tickHz: 20 };
            """, ["p1"],
            ("KnockBox:AuthorityCallTimeoutMs", "5000"),
            ("KnockBox:AuthoritySlowCallWarnFraction", "0.001"),
            ("KnockBox:AuthorityTickHzMax", "20"));

        for (var i = 0; i < 5; i++)
        {
            rig.Actor.RequestTick();
            await Task.Delay(120, _cts.Token);
        }
        await rig.StopAndFlushAsync();

        // Five slow ticks of roughly equal cost: the first is a new worst, the rest are not. Jitter can
        // make a later one marginally worse, so this pins "not one per call" rather than exactly one.
        var warnings = rig.Logs.At(LogLevel.Warning)
            .Count(m => m.Contains("per-call budget", StringComparison.Ordinal));
        Assert.InRange(warnings, 1, 3);
    }
}
