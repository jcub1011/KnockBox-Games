using KnockBox.Server.Games;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

public class GameCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-catalog-" + Guid.NewGuid().ToString("N"));

    public GameCatalogTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private void WriteGame(string id, string manifestJson, string entry = "index.html", bool writeEntry = true)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "GAME.json"), manifestJson);
        if (writeEntry) File.WriteAllText(Path.Combine(dir, entry), "<html></html>");
    }

    private GameCatalog NewCatalog() => new(_root, NullLogger<GameCatalog>.Instance);

    [Fact]
    public void Discovers_a_valid_game()
    {
        WriteGame("ttt", """
        { "id": "ttt", "name": "Tic-Tac-Toe", "entry": "index.html",
          "thumbnail": "thumb.svg", "minPlayers": 2, "maxPlayers": 2, "crossOriginIsolated": true }
        """);
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.True(catalog.TryGet("ttt", out var m));
        Assert.Equal("Tic-Tac-Toe", m.Name);
        Assert.True(m.CrossOriginIsolated);
    }

    [Fact]
    public void Skips_a_game_whose_entry_file_is_missing()
    {
        WriteGame("broken",
            """{ "id": "broken", "name": "B", "entry": "index.html", "minPlayers": 1, "maxPlayers": 1 }""",
            writeEntry: false);
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.False(catalog.TryGet("broken", out _));
    }

    [Fact]
    public void Skips_invalid_json_without_throwing()
    {
        WriteGame("bad", "{ this is not json ");
        var catalog = NewCatalog();

        catalog.Discover(); // must not throw
        Assert.Empty(catalog.Games);
    }

    [Fact]
    public async Task Polling_rescans_when_a_manifest_appears()
    {
        // The polling fallback exists for environments where FileSystemWatcher never fires (Docker
        // bind mounts) — so this test uses ONLY StartPolling, never StartWatching.
        using var catalog = NewCatalog();
        catalog.Discover();
        Assert.Empty(catalog.Games);
        catalog.StartPolling(TimeSpan.FromMilliseconds(50));

        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "minPlayers": 2, "maxPlayers": 2 }""");

        // Poll tick (≤50ms) + debounce (~500ms); generous deadline to absorb CI scheduling noise.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !catalog.TryGet("ttt", out _))
            await Task.Delay(50);

        Assert.True(catalog.TryGet("ttt", out _));
    }

    [Fact]
    public void Rediscovery_drops_a_removed_game_via_atomic_swap()
    {
        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "minPlayers": 2, "maxPlayers": 2 }""");
        var catalog = NewCatalog();
        catalog.Discover();
        Assert.True(catalog.TryGet("ttt", out _));

        Directory.Delete(Path.Combine(_root, "ttt"), recursive: true);
        catalog.Discover();

        Assert.False(catalog.TryGet("ttt", out _));
        Assert.Empty(catalog.Games);
    }
}
