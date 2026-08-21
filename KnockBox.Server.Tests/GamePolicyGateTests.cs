using KnockBox.Contracts;
using KnockBox.Server.Admin;
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
/// Drives the REAL <see cref="WebSocketHandler"/> over a scripted socket to pin where operator policy is
/// enforced: which games a player is shown, which they may start, and — just as important — what policy
/// deliberately does NOT touch, namely a lobby that is already running.
/// </summary>
public class GamePolicyGateTests : IDisposable
{
    private readonly string _gamesRoot;

    public GamePolicyGateTests()
    {
        _gamesRoot = Path.Combine(Path.GetTempPath(), $"kb-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_gamesRoot);
        WriteGame("tictactoe", "Tic-Tac-Toe");
        WriteGame("word-rush", "Word Rush");
    }

    public void Dispose()
    {
        try { Directory.Delete(_gamesRoot, recursive: true); } catch { /* best effort */ }
    }

    private void WriteGame(string id, string name)
    {
        var dir = Path.Combine(_gamesRoot, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "GAME.json"),
            $$"""{ "id": "{{id}}", "name": "{{name}}", "entry": "index.html", "maxPlayers": 2 }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
    }

    /// <summary>A policy whose answers a test sets directly — the seam IPlatformPolicy exists for.</summary>
    private sealed class StubPolicy : IPlatformPolicy
    {
        public bool MaintenanceMode { get; set; }
        public string? MaintenanceMessage { get; set; }
        public HashSet<string> Unlisted { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unstartable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Per-game refusal text, e.g. what a game mid-update says. Empty ⇒ the generic message.</summary>
        public Dictionary<string, string> Reasons { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The live banner, if a test is exercising the announcement push.</summary>
        public PlatformAnnouncement? Announcement { get; set; }

        public bool CanCreateLobby(string gameId) => !MaintenanceMode && !Unstartable.Contains(gameId);
        public bool IsListed(string gameId) => !Unlisted.Contains(gameId);
        public string? UnavailableReason(string gameId) => Reasons.GetValueOrDefault(gameId);
    }

    // Returns the frames the server sent. Deliberately NOT the lobby list: when the scripted socket runs
    // out, the handler's disconnect path runs and CloseLobbyIfDark removes a lobby nobody is connected to,
    // so a count taken afterwards is always 0 and would say nothing about whether creation was allowed.
    private List<IMessage?> Run(StubPolicy policy, params IMessage[] frames)
    {
        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var catalog = new GameCatalog(_gamesRoot, NullLogger<GameCatalog>.Instance);
        catalog.Discover();

        var handler = new WebSocketHandler(
            connections, lobbies, catalog,
            TestAuthorities.Manager(connections, lobbies),
            new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance),
            new LimitsProvider(ServerLimits.FromConfiguration(new ConfigurationBuilder().Build())),
            policy, new RelayMetrics(), TimeProvider.System,
            NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance);

        var socket = new ScriptedSocket([.. frames.Select(ConnectionManager.Serialize)]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        handler.HandleAsync(socket, "http://game.local", cts.Token).GetAwaiter().GetResult();

        return [.. socket.Sent.Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage))];
    }

    private static IReadOnlyList<string> CatalogIds(List<IMessage?> replies) =>
        [.. replies.OfType<GameCatalogMessage>().Single().Games.Select(g => g.Id)];

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public void An_available_game_is_listed()
    {
        var replies = Run(new StubPolicy(), new HelloMessage(null, "Ann"), new ListGamesMessage("c1"));
        Assert.Equal(["tictactoe", "word-rush"], CatalogIds(replies).Order());
    }

    [Fact]
    public void A_disabled_or_staged_game_is_withheld_from_the_catalog()
    {
        var policy = new StubPolicy();
        policy.Unlisted.Add("tictactoe");

        var replies = Run(policy, new HelloMessage(null, "Ann"), new ListGamesMessage("c1"));
        // This is what removes the tile from the shell's grid — filtered on the way out, not hidden by the
        // client, so a curious player can't simply ignore a flag.
        Assert.Equal(["word-rush"], CatalogIds(replies));
    }

    [Fact]
    public void A_named_staged_game_is_re_admitted_to_the_catalog()
    {
        var policy = new StubPolicy();
        policy.Unlisted.Add("tictactoe"); // staged: hidden, but still startable

        var replies = Run(policy, new HelloMessage(null, "Ann"), new ListGamesMessage("c1", Include: "tictactoe"));
        // The shell allowlists every launch against the catalog it was given, so a staged game reached
        // through its direct link HAS to come back in that list or the shell rejects its own EnterGame.
        Assert.Equal(["tictactoe", "word-rush"], CatalogIds(replies).Order());
    }

    [Fact]
    public void A_named_DISABLED_game_is_still_withheld()
    {
        var policy = new StubPolicy();
        policy.Unlisted.Add("tictactoe");
        policy.Unstartable.Add("tictactoe"); // disabled, not staged

        var replies = Run(policy, new HelloMessage(null, "Ann"), new ListGamesMessage("c1", Include: "tictactoe"));
        // Disabled means "players may not start this". Handing the manifest back would only move the
        // refusal to the create round trip, after the shell had already raised its launch overlay.
        Assert.Equal(["word-rush"], CatalogIds(replies));
    }

    [Fact]
    public void Include_only_re_admits_the_game_it_names()
    {
        var policy = new StubPolicy();
        policy.Unlisted.Add("tictactoe");
        policy.Unlisted.Add("word-rush");

        var replies = Run(policy, new HelloMessage(null, "Ann"), new ListGamesMessage("c1", Include: "tictactoe"));
        Assert.Equal(["tictactoe"], CatalogIds(replies));
    }

    // ── Creation ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_lobby_can_be_created_for_an_available_game()
    {
        var replies = Run(new StubPolicy(),
            new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));

        Assert.Single(replies.OfType<LobbyCreatedMessage>());
        // EnterGame is the last step of a successful create, so its presence proves the whole path ran
        // rather than just the acknowledgement.
        Assert.Single(replies.OfType<EnterGameMessage>());
    }

    [Fact]
    public void Creating_a_lobby_for_a_disabled_game_is_refused_by_name()
    {
        var policy = new StubPolicy();
        policy.Unstartable.Add("tictactoe");

        var replies = Run(policy,
            new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));

        var error = Assert.Single(replies.OfType<ErrorMessage>());
        // The game's TITLE, not its id: this text goes straight into the shell's error toast.
        Assert.Contains("Tic-Tac-Toe", error.Reason);
        Assert.Empty(replies.OfType<LobbyCreatedMessage>());
    }

    [Fact]
    public void Maintenance_mode_refuses_creation_for_every_game()
    {
        var policy = new StubPolicy { MaintenanceMode = true };

        foreach (var gameId in new[] { "tictactoe", "word-rush" })
        {
            var replies = Run(policy, new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", gameId));
            Assert.Single(replies.OfType<ErrorMessage>());
            Assert.Empty(replies.OfType<LobbyCreatedMessage>());
        }
    }

    [Fact]
    public void Maintenance_mode_shows_the_operators_own_message_when_there_is_one()
    {
        var policy = new StubPolicy { MaintenanceMode = true, MaintenanceMessage = "Back at 09:00." };

        var replies = Run(policy, new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));
        Assert.Equal("Back at 09:00.", Assert.Single(replies.OfType<ErrorMessage>()).Reason);
    }

    [Fact]
    public void Maintenance_mode_without_a_message_still_explains_itself()
    {
        var policy = new StubPolicy { MaintenanceMode = true };

        var replies = Run(policy, new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));
        Assert.Contains("maintenance", Assert.Single(replies.OfType<ErrorMessage>()).Reason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_staged_game_stays_startable()
    {
        var policy = new StubPolicy();
        policy.Unlisted.Add("tictactoe"); // hidden but NOT unstartable — that is what staged means

        var replies = Run(policy,
            new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));

        Assert.Single(replies.OfType<LobbyCreatedMessage>());
        Assert.Single(replies.OfType<EnterGameMessage>());
    }

    [Fact]
    public void A_refused_creation_does_not_eject_the_player_from_the_lobby_they_were_in()
    {
        var policy = new StubPolicy();
        policy.Unstartable.Add("word-rush");

        // Create one lobby, then try to start a blocked game. HandleCreateLobby normally leaves every other
        // lobby first, so the refusal has to come BEFORE that or a player is punished for asking.
        var replies = Run(policy,
            new HelloMessage(null, "Ann"),
            new CreateLobbyMessage("c1", "tictactoe"),
            new CreateLobbyMessage("c2", "word-rush"));

        Assert.Single(replies.OfType<LobbyCreatedMessage>());
        Assert.Single(replies.OfType<ErrorMessage>());
        // Leaving a lobby broadcasts PlayerLeft to its members — including the leaver, who is the only
        // member here. Its absence is the proof that the refusal came before LeaveLobbiesExcept ran.
        Assert.Empty(replies.OfType<PlayerLeftMessage>());
    }

    // ── Announcements (§4.1) ──────────────────────────────────────────────────

    [Fact]
    public void A_connecting_player_is_told_the_live_announcement_right_after_Welcome()
    {
        var policy = new StubPolicy
        {
            Announcement = new PlatformAnnouncement(
                "a1", "Maintenance in 20 minutes.", DateTimeOffset.UnixEpoch, "warning"),
        };

        var replies = Run(policy, new HelloMessage(null, "Ann"));

        // The whole reason this is pushed on connect rather than only broadcast when posted: a player who
        // arrives after the operator posted it must see the same banner, without the server keeping any
        // per-viewer state.
        var announced = Assert.Single(replies.OfType<AnnouncementPostedMessage>());
        Assert.Equal(("a1", "Maintenance in 20 minutes.", "warning"),
            (announced.Id, announced.Text, announced.Severity));
        // After Welcome, so the shell has its identity before anything else arrives.
        Assert.True(replies.FindIndex(m => m is WelcomeMessage)
                    < replies.FindIndex(m => m is AnnouncementPostedMessage));
    }

    [Fact]
    public void With_no_announcement_posted_nothing_extra_is_sent()
    {
        var replies = Run(new StubPolicy(), new HelloMessage(null, "Ann"));
        Assert.Empty(replies.OfType<AnnouncementPostedMessage>());
    }

    /// <summary>Replays scripted inbound frames and captures the outbound ones, then ends the socket.</summary>
    private sealed class ScriptedSocket(IEnumerable<byte[]> inbound) : WebSocket
    {
        private readonly Queue<byte[]> _inbound = new(inbound);
        private WebSocketState _state = WebSocketState.Open;

        public List<byte[]> Sent { get; } = [];

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
            var message = _inbound.Dequeue();
            message.CopyTo(buffer.Array!, buffer.Offset);
            return Task.FromResult(new WebSocketReceiveResult(message.Length, WebSocketMessageType.Text, true));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken ct)
        {
            Sent.Add([.. buffer]);
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
        public override void Dispose() { }
    }
}
