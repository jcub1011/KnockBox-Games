using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Where a server-authority game's files live is the catalog's answer, not <c>gamesRoot/&lt;id&gt;</c>:
/// since the <c>.kbg</c> package format a game may be served out of the unpacked-package cache
/// instead. These tests pin that the manager honours the resolved directory, because getting it
/// wrong fails silently in the worst way — every packaged authority game refuses to start.
/// </summary>
public class ServerAuthorityLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-auth-loc-" + Guid.NewGuid().ToString("N"));

    public ServerAuthorityLocationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private const string Module = """
        export function createAuthority(kb) {
          return { init() {}, applyIntent() { return null; }, snapshot() { return { ok: true }; } };
        }
        """;

    private static readonly GameManifest Manifest = new(
        "packaged-authority", "P", "index.html", null, 4,
        ServerAuthority: "authority.js",
        AuthorityWords: new Dictionary<string, AuthorityWordDeclaration> { ["en"] = new("words.txt") });

    /// <summary>Lays the game down under a root that is deliberately NOT the games folder.</summary>
    private string WriteUnpackedGame()
    {
        var dir = Path.Combine(_root, "games-unpacked", Manifest.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "authority.js"), Module);
        File.WriteAllText(Path.Combine(dir, "words.txt"), "apple\nbrave\n");
        return dir;
    }

    private ServerAuthorityManager Manager(Func<string, string?> gameDirectory) =>
        new(gameDirectory,
            AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs()),
            new ConnectionManager(), new LobbyManager(), TimeProvider.System,
            new AuthorityWordService(NullLogger<AuthorityWordService>.Instance),
            isDevelopment: false, NullLoggerFactory.Instance);

    [Fact]
    public void Starts_a_game_whose_files_live_outside_the_games_folder()
    {
        var unpacked = WriteUnpackedGame();
        var manager = Manager(id => id == Manifest.Id ? unpacked : null);
        var lobbies = new LobbyManager();
        Assert.True(lobbies.TryCreate(Manifest.Id, "p1", 4, out var lobby, isServerAuthority: true));
        Assert.True(lobby.TryAdd(new Player("p1", "p1")));

        var started = manager.TryStart(lobby, Manifest, out var error);

        Assert.True(started, error);
        Assert.Null(error);
        Assert.Equal(1, manager.ActorCount);
        manager.StopAll();
    }

    [Fact]
    public void Refuses_to_start_a_game_the_catalog_cannot_place()
    {
        // Nothing resolved the id — the game was removed between lobby creation and this call. Fail
        // loudly (the caller tears the lobby down) rather than start a half-alive lobby.
        WriteUnpackedGame();
        var manager = Manager(_ => null);
        var lobbies = new LobbyManager();
        Assert.True(lobbies.TryCreate(Manifest.Id, "p1", 4, out var lobby, isServerAuthority: true));

        Assert.False(manager.TryStart(lobby, Manifest, out var error));
        Assert.Equal("The game's server-authority module is missing.", error);
        Assert.Equal(0, manager.ActorCount);
    }

    [Fact]
    public void Prunes_the_module_cache_by_the_directory_the_catalog_published()
    {
        // The cache is keyed on the module's full path, so a prune that rebuilt the path from a games
        // root would never match a packaged game's entry and would leak its parsed AST forever.
        var unpacked = WriteUnpackedGame();
        var manager = Manager(id => id == Manifest.Id ? unpacked : null);
        var lobbies = new LobbyManager();
        Assert.True(lobbies.TryCreate(Manifest.Id, "p1", 4, out var lobby, isServerAuthority: true));
        Assert.True(lobby.TryAdd(new Player("p1", "p1")));
        Assert.True(manager.TryStart(lobby, Manifest, out _));

        // Still declared, from that same directory: nothing to reclaim, and the next start still works.
        manager.PruneModuleCache(new Dictionary<string, GameCatalog.GameLocation>
        {
            [Manifest.Id] = new(Manifest, unpacked),
        });
        Assert.True(lobbies.TryCreate(Manifest.Id, "p2", 4, out var second, isServerAuthority: true));
        Assert.True(second.TryAdd(new Player("p2", "p2")));
        Assert.True(manager.TryStart(second, Manifest, out var error), error);

        manager.StopAll();
    }
}
