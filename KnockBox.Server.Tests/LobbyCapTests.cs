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
/// Drives the REAL <see cref="WebSocketHandler"/> to pin the capacity caps (spec §2.2): the platform-wide
/// one, the per-game one, and the two properties that make them safe to turn on — a refusal explains
/// itself, and it doesn't cost the player the lobby they were already in.
/// </summary>
public class LobbyCapTests : IDisposable
{
    private readonly string _gamesRoot;

    public LobbyCapTests()
    {
        _gamesRoot = Path.Combine(Path.GetTempPath(), $"kb-caps-{Guid.NewGuid():N}");
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

    private static ServerLimits Defaults => ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());

    [Fact]
    public void The_global_cap_refuses_a_new_lobby_and_names_the_limit()
    {
        var replies = Run(Defaults with { MaxLobbies = 2 },
            seed: lobbies =>
            {
                lobbies.TryCreate("tictactoe", "seed-1", 2, out _);
                lobbies.TryCreate("word-rush", "seed-2", 2, out _);
            },
            new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));

        Assert.Empty(replies.OfType<LobbyCreatedMessage>());
        // The number is in the message on purpose: "try again later" with no reason reads as a fault.
        Assert.Contains("limit of 2", Assert.Single(replies.OfType<ErrorMessage>()).Reason);
    }

    [Fact]
    public void The_per_game_cap_only_refuses_the_game_that_is_full()
    {
        var replies = Run(Defaults with { MaxLobbiesPerGame = 1 },
            seed: lobbies => lobbies.TryCreate("tictactoe", "seed-1", 2, out _),
            new HelloMessage(null, "Ann"),
            new CreateLobbyMessage("c1", "tictactoe"),
            new CreateLobbyMessage("c2", "word-rush"));

        var error = Assert.Single(replies.OfType<ErrorMessage>());
        Assert.Equal("c1", error.Cid);
        Assert.Contains("Tic-Tac-Toe", error.Reason); // named by title, not id — a player has never seen the id
        Assert.Single(replies.OfType<LobbyCreatedMessage>()); // word-rush was unaffected
    }

    [Fact]
    public void Zero_means_unlimited()
    {
        var replies = Run(Defaults with { MaxLobbies = 0, MaxLobbiesPerGame = 0 },
            seed: lobbies =>
            {
                for (var i = 0; i < 50; i++) lobbies.TryCreate("tictactoe", $"seed-{i}", 2, out _);
            },
            new HelloMessage(null, "Ann"), new CreateLobbyMessage("c1", "tictactoe"));

        Assert.Single(replies.OfType<LobbyCreatedMessage>());
        Assert.Empty(replies.OfType<ErrorMessage>());
    }

    [Fact]
    public void A_capped_refusal_does_not_eject_the_player_from_the_lobby_they_were_in()
    {
        // Cap 2 with one lobby already running: the player's own create fills the platform, so their
        // second attempt is refused. HandleCreateLobby normally leaves every other lobby first, so the
        // cap check has to come BEFORE that — otherwise asking costs you the game you were playing.
        var replies = Run(Defaults with { MaxLobbies = 2 },
            seed: lobbies => lobbies.TryCreate("word-rush", "seed-1", 2, out _),
            new HelloMessage(null, "Ann"),
            new CreateLobbyMessage("c1", "tictactoe"),
            new CreateLobbyMessage("c2", "word-rush"));

        Assert.Single(replies.OfType<LobbyCreatedMessage>());
        Assert.Single(replies.OfType<ErrorMessage>());
        // Leaving broadcasts PlayerLeft to the lobby's members — here, only the leaver. Its absence is
        // the proof the refusal came before LeaveLobbiesExcept ran.
        Assert.Empty(replies.OfType<PlayerLeftMessage>());
    }

    [Fact]
    public void Tightening_the_cap_applies_without_a_restart()
    {
        // The whole point of the live seam: the same provider instance the running handler holds.
        var provider = new LimitsProvider(Defaults);
        var replies = Run(provider,
            seed: lobbies => lobbies.TryCreate("tictactoe", "seed-1", 2, out _),
            beforeSecondFrame: () => provider.Apply(new OperatorLimits(MaxLobbies: 1)),
            new HelloMessage(null, "Ann"),
            new CreateLobbyMessage("c1", "tictactoe"));

        Assert.Empty(replies.OfType<LobbyCreatedMessage>());
        Assert.Contains("limit of 1", Assert.Single(replies.OfType<ErrorMessage>()).Reason);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private List<IMessage?> Run(ServerLimits limits, Action<LobbyManager> seed, params IMessage[] frames) =>
        Run(new LimitsProvider(limits), seed, beforeSecondFrame: null, frames);

    private List<IMessage?> Run(LimitsProvider limits, Action<LobbyManager> seed,
        Action? beforeSecondFrame, params IMessage[] frames)
    {
        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var catalog = new GameCatalog(_gamesRoot, NullLogger<GameCatalog>.Instance);
        catalog.Discover();
        seed(lobbies);

        var handler = new WebSocketHandler(
            connections, lobbies, catalog,
            TestAuthorities.Manager(connections, lobbies),
            new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance),
            limits,
            PlatformPolicy.OpenPlatform, new RelayMetrics(), TimeProvider.System,
            NullLoggerFactory.Instance, NullLogger<WebSocketHandler>.Instance);

        var socket = new ScriptedSocket([.. frames.Select(ConnectionManager.Serialize)])
        {
            // Fired once the handshake frame has been consumed, so a test can change the limits at the
            // moment a real operator would: with the socket open and mid-conversation.
            AfterFirstReceive = beforeSecondFrame,
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        handler.HandleAsync(socket, "http://game.local", cts.Token).GetAwaiter().GetResult();

        return [.. socket.Sent.Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage))];
    }

    /// <summary>Replays scripted inbound frames and captures the outbound ones, then ends the socket.</summary>
    private sealed class ScriptedSocket(IEnumerable<byte[]> inbound) : WebSocket
    {
        private readonly Queue<byte[]> _inbound = new(inbound);
        private WebSocketState _state = WebSocketState.Open;
        private bool _firstReceived;

        public List<byte[]> Sent { get; } = [];

        /// <summary>Runs once, after the first inbound frame is handed over. See Run().</summary>
        public Action? AfterFirstReceive { get; init; }

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
            if (!_firstReceived)
            {
                _firstReceived = true;
                AfterFirstReceive?.Invoke();
            }
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
