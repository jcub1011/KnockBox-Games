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
    public void Missing_games_folder_is_benign_and_sets_no_scan_error()
    {
        Directory.Delete(_root, recursive: true);
        var catalog = NewCatalog();

        catalog.Discover(); // must not throw
        Assert.Empty(catalog.Games);
        Assert.Null(catalog.ScanError); // a missing folder is normal, not a misconfiguration to flag
    }

    [Fact]
    public void Unreadable_games_folder_does_not_crash_and_is_reported()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permission bits only

        File.SetUnixFileMode(_root, UnixFileMode.None);
        try
        {
            // If enumeration still succeeds (e.g. the test runs as root, which bypasses perms), the
            // scenario can't be exercised — skip rather than assert something untrue.
            try { _ = Directory.EnumerateDirectories(_root).Any(); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* denied, as intended */ }

            var catalog = NewCatalog();
            catalog.Discover(); // must not throw despite the access denial
            Assert.Empty(catalog.Games);
            Assert.NotNull(catalog.ScanError);
        }
        finally
        {
            // Restore access so Dispose can clean the directory up.
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    [Fact]
    public void Skips_a_game_whose_folder_name_does_not_match_its_id()
    {
        // Assets are served at /games/{id}/…, so a mismatch would 404 every load. The catalog must
        // refuse it rather than publish a game that can never load.
        var dir = Path.Combine(_root, "wrong-folder");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "GAME.json"),
            """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");

        var catalog = NewCatalog();
        catalog.Discover();

        Assert.False(catalog.TryGet("ttt", out _));
        Assert.Empty(catalog.Games);
    }

    [Theory]
    [InlineData("../escape.html")]
    [InlineData("../../etc/passwd")]
    public void Skips_a_game_whose_entry_escapes_the_game_folder(string entry)
    {
        // The escape target is made to EXIST, so only the traversal check can reject this.
        File.WriteAllText(Path.Combine(_root, "escape.html"), "<html></html>");
        WriteGame("evil", $$"""{ "id": "evil", "name": "E", "entry": "{{entry}}", "maxPlayers": 2 }""");

        var catalog = NewCatalog();
        catalog.Discover();

        Assert.False(catalog.TryGet("evil", out _));
    }

    [Fact]
    public void TryGetDirectory_reports_where_a_games_files_live()
    {
        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.True(catalog.TryGetDirectory("ttt", out var dir));
        Assert.Equal(Path.Combine(_root, "ttt"), dir);
        Assert.False(catalog.TryGetDirectory("nope", out _));
        Assert.Equal(dir, catalog.GameDirectories["ttt"]);
    }

    // ── Multiple roots ────────────────────────────────────────────────────────────────────────────
    // The second root is where .kbg packages get extracted. It is searched after the administrator's
    // games directory, so a hand-placed folder always wins.

    private string SecondRoot()
    {
        var dir = Path.Combine(_root, "..", "kb-unpacked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.GetFullPath(dir);
    }

    private static void WriteGameIn(string root, string id, string name)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "GAME.json"),
            $$"""{ "id": "{{id}}", "name": "{{name}}", "entry": "index.html", "maxPlayers": 2 }""");
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
    }

    [Fact]
    public void Discovers_games_from_every_root()
    {
        var second = SecondRoot();
        try
        {
            WriteGameIn(_root, "from-folder", "Folder Game");
            WriteGameIn(second, "from-package", "Package Game");

            using var catalog = new GameCatalog([_root, second], NullLogger<GameCatalog>.Instance);
            catalog.Discover();

            Assert.True(catalog.TryGet("from-folder", out _));
            Assert.True(catalog.TryGet("from-package", out _));
            Assert.Equal(Path.Combine(second, "from-package"), catalog.GameDirectories["from-package"]);
        }
        finally { Directory.Delete(second, recursive: true); }
    }

    [Fact]
    public void The_first_root_wins_a_duplicate_id()
    {
        var second = SecondRoot();
        try
        {
            WriteGameIn(_root, "dup", "Administrator's Folder");
            WriteGameIn(second, "dup", "Extracted Package");

            using var catalog = new GameCatalog([_root, second], NullLogger<GameCatalog>.Instance);
            catalog.Discover();

            Assert.True(catalog.TryGet("dup", out var m));
            Assert.Equal("Administrator's Folder", m.Name);
            // Crucially the DIRECTORY matches the winning manifest too: serving a mixture of one
            // folder's manifest and another's assets is the failure this ordering prevents.
            Assert.Equal(Path.Combine(_root, "dup"), catalog.GameDirectories["dup"]);
            Assert.Single(catalog.Games);
        }
        finally { Directory.Delete(second, recursive: true); }
    }

    [Fact]
    public void An_unreadable_secondary_root_does_not_set_a_blocking_scan_error()
    {
        // A derived cache root that can't be read degrades .kbg installs, but the plain folders in the
        // games directory still work — so it must never blank a working site via ScanError.
        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        var missing = Path.Combine(_root, "..", "kb-absent-" + Guid.NewGuid().ToString("N"));

        using var catalog = new GameCatalog([_root, Path.GetFullPath(missing)], NullLogger<GameCatalog>.Instance);
        catalog.Discover();

        Assert.True(catalog.TryGet("ttt", out _));
        Assert.Null(catalog.ScanError);
    }

    [Fact]
    public void A_missing_primary_root_still_discovers_from_a_later_root()
    {
        // Ordering must not depend on the primary root existing: a container whose games mount hasn't
        // appeared yet should still serve packages already extracted into the cache.
        var second = SecondRoot();
        try
        {
            WriteGameIn(second, "from-package", "Package Game");
            Directory.Delete(_root, recursive: true);

            using var catalog = new GameCatalog([_root, second], NullLogger<GameCatalog>.Instance);
            catalog.Discover();

            Assert.True(catalog.TryGet("from-package", out _));
            Assert.Null(catalog.ScanError);
        }
        finally { Directory.Delete(second, recursive: true); }
    }

    [Fact]
    public void Requires_at_least_one_root()
    {
        Assert.Throws<ArgumentException>(() => new GameCatalog([], NullLogger<GameCatalog>.Instance));
    }

    [Fact]
    public async Task Polling_notices_a_dropped_package_file()
    {
        // A .kbg creates no directory and touches no GAME.json, so the manifest-only fingerprint used
        // to miss it entirely. On a Docker bind mount this poll is the ONLY signal that fires, so
        // without packages in the fingerprint they would never install there. Assert the signal
        // reaches a Discovered handler; extraction itself is GamePackageInstaller's job.
        using var catalog = NewCatalog();
        catalog.Discover();
        var rescans = 0;
        catalog.Discovered += _ => Interlocked.Increment(ref rescans);
        catalog.StartPolling(TimeSpan.FromMilliseconds(50));

        File.WriteAllBytes(Path.Combine(_root, "something.kbg"), [1, 2, 3, 4]);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref rescans) == 0)
            await Task.Delay(50);

        Assert.True(Volatile.Read(ref rescans) > 0, "dropping a .kbg should trigger a rescan");
    }
}
