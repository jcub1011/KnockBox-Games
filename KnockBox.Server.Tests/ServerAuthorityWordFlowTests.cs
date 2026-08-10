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
/// End-to-end wiring of the shared word service: a game declaring authorityWords, through
/// ServerAuthorityManager.TryStart (which loads the dictionaries), into a live actor whose module
/// queries kb.words and broadcasts the result. Complements the pure runtime/service unit tests.
/// </summary>
public class ServerAuthorityWordFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-word-flow-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));

    public ServerAuthorityWordFlowTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        _cts.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string WordModule = """
        export function createAuthority(kb) {
          return {
            init() {},
            applyIntent(fromId, action) {
              if (action.kind === 'check') return { valid: kb.words.has('en', action.word) };
              if (action.kind === 'pick')  return { word: kb.words.pick('en', action.i), total: kb.words.count('en') };
              return null;
            },
            snapshot() { return {}; },
          };
        }
        """;

    private (ServerAuthority actor, Connection game, FakeWebSocket gameSock, List<Task> loops) StartWordGame()
    {
        var gameDir = Path.Combine(_root, "wg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(gameDir);
        File.WriteAllText(Path.Combine(gameDir, "authority.js"), WordModule);
        File.WriteAllText(Path.Combine(gameDir, "words.txt"), "apple\nbrave\ncrane\n");
        var gameId = new DirectoryInfo(gameDir).Name;
        var manifest = new GameManifest(gameId, gameId, "index.html", null, 8,
            ServerAuthority: "authority.js",
            AuthorityWords: new Dictionary<string, AuthorityWordDeclaration> { ["en"] = new("words.txt") });

        var connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        var manager = new ServerAuthorityManager(id => Path.Combine(_root, id),
            AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs()),
            connections, lobbies, TimeProvider.System,
            new AuthorityWordService(NullLogger<AuthorityWordService>.Instance),
            isDevelopment: false, NullLoggerFactory.Instance);

        Assert.True(lobbies.TryCreate(gameId, "p1", 8, out var lobby, isServerAuthority: true));
        Assert.True(lobby.TryAdd(new Player("p1", "p1")));

        var gameSock = new FakeWebSocket();
        var game = new Connection("p1", "p1", gameSock, NullLogger<Connection>.Instance, OutboundOverflow.DropOldest);
        connections.AddGame(game);
        var loops = new List<Task> { game.SendLoopAsync(_cts.Token) };

        Assert.True(manager.TryStart(lobby, manifest, out var error), error);
        Assert.True(manager.TryGet(lobby.Id, out var actor));
        return (actor, game, gameSock, loops);
    }

    private async Task<List<GameMessage>> DrainServerFrames(ServerAuthority actor, Connection game, FakeWebSocket sock, List<Task> loops)
    {
        actor.Stop();
        await actor.Completion;
        game.CompleteOutbound();
        foreach (var loop in loops) await loop;
        return sock.Sent
            .Select(b => JsonSerializer.Deserialize(b, KnockBoxProtocolContext.Default.IMessage))
            .OfType<GameMessage>()
            .Where(g => g.From == "server")
            .ToList();
    }

    [Fact]
    public async Task Module_validates_and_picks_against_the_loaded_dictionary()
    {
        var (actor, game, sock, loops) = StartWordGame();
        actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"check","word":"APPLE"}}"""); // folds → hit
        actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"check","word":"zzz"}}""");    // miss
        actor.PostIntent("p1", """{"_kb":"intent","action":{"kind":"pick","i":0}}""");            // global index 0

        var frames = await DrainServerFrames(actor, game, sock, loops);
        var deltas = frames.Where(f => f.Payload.GetProperty("_kb").GetString() == "delta").ToList();

        Assert.Equal(3, deltas.Count);
        Assert.True(deltas[0].Payload.GetProperty("patch").GetProperty("valid").GetBoolean());
        Assert.False(deltas[1].Payload.GetProperty("patch").GetProperty("valid").GetBoolean());
        Assert.Equal("apple", deltas[2].Payload.GetProperty("patch").GetProperty("word").GetString());
        Assert.Equal(3, deltas[2].Payload.GetProperty("patch").GetProperty("total").GetInt32());
    }
}
