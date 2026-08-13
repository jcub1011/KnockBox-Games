using KnockBox.Server.Games;
using Microsoft.Extensions.Logging;
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

    private GameCatalog NewCatalog(
        long authorityMaxScriptBytes = AuthorityOptions.DefaultMaxScriptBytes,
        long authorityMaxWordFileBytes = AuthorityOptions.DefaultMaxWordFileBytes) =>
        new(_root, NullLogger<GameCatalog>.Instance, authorityMaxScriptBytes, authorityMaxWordFileBytes);

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
    public void An_unreadable_games_folder_keeps_the_previous_catalog_and_notifies_nobody()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permission bits only

        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        var catalog = NewCatalog();
        catalog.Discover();
        Assert.Single(catalog.Games);

        var notifications = 0;
        catalog.Discovered += _ => Interlocked.Increment(ref notifications);

        File.SetUnixFileMode(_root, UnixFileMode.None);
        try
        {
            try { _ = Directory.EnumerateDirectories(_root).Any(); return; } // running as root: can't test
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* denied, as intended */ }

            catalog.Discover();

            // A failed scan is not an empty library. Every Discovered subscriber maintains derived state
            // keyed on "which games exist" — the compressed cache, the word pools, the parsed authority
            // modules — and each treats an absent id as "delete what you built for it". So a mount that
            // blips for one pass must neither empty the catalog nor tell anyone it did, or the whole
            // games-compressed tree is deleted and re-compressed at max effort when access returns.
            Assert.Single(catalog.Games);
            Assert.NotNull(catalog.ScanError);
            Assert.Equal(0, notifications);
        }
        finally
        {
            File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ── serverAuthority validation ────────────────────────────────────────────
    // A manifest that declares serverAuthority but fails any check skips the WHOLE game: silently
    // downgrading a game that asked for server-side enforcement to host mode would betray the opt-in.

    private const string AuthorityManifest =
        """{ "id": "sa", "name": "S", "entry": "index.html", "maxPlayers": 2, "serverAuthority": "authority.js" }""";

    private void WriteAuthorityModule(string id, string name = "authority.js", int sizeBytes = 0)
    {
        var content = sizeBytes > 0 ? new string('/', sizeBytes) : "export function createAuthority(kb) {}";
        File.WriteAllText(Path.Combine(_root, id, name), content);
    }

    [Fact]
    public void Discovers_a_game_with_a_valid_serverAuthority_module()
    {
        WriteGame("sa", AuthorityManifest);
        WriteAuthorityModule("sa");
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.True(catalog.TryGet("sa", out var m));
        Assert.Equal("authority.js", m.ServerAuthority);
    }

    [Fact]
    public void Skips_a_game_whose_authority_module_escapes_the_game_folder()
    {
        WriteGame("sa",
            """{ "id": "sa", "name": "S", "entry": "index.html", "maxPlayers": 2, "serverAuthority": "../evil.js" }""");
        File.WriteAllText(Path.Combine(_root, "evil.js"), "export function createAuthority(kb) {}");
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.False(catalog.TryGet("sa", out _));
    }

    [Fact]
    public void Skips_a_game_whose_authority_module_is_missing()
    {
        WriteGame("sa", AuthorityManifest); // no authority.js written
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.False(catalog.TryGet("sa", out _));
    }

    [Fact]
    public void Skips_a_game_whose_authority_module_exceeds_the_size_cap()
    {
        WriteGame("sa", AuthorityManifest);
        WriteAuthorityModule("sa", sizeBytes: 2048);
        var catalog = NewCatalog(authorityMaxScriptBytes: 1024);
        catalog.Discover();

        Assert.False(catalog.TryGet("sa", out _));
    }

    [Fact]
    public void Skips_a_game_declaring_a_wasm_authority_module()
    {
        // The WASM backend is a later phase; skipping (not ignoring the field) keeps the
        // never-silently-downgrade promise.
        WriteGame("sa",
            """{ "id": "sa", "name": "S", "entry": "index.html", "maxPlayers": 2, "serverAuthority": "authority.wasm" }""");
        WriteAuthorityModule("sa", name: "authority.wasm");
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.False(catalog.TryGet("sa", out _));
    }

    // ── authorityWords validation ─────────────────────────────────────────────
    // Same fail-loud policy as serverAuthority: a word game with a broken dictionary skips entirely.

    private const string WordGameManifest =
        """{ "id": "wg", "name": "W", "entry": "index.html", "maxPlayers": 4, "serverAuthority": "authority.js", "authorityWords": { "en": { "file": "words.txt", "caseInsensitive": true } } }""";

    private void WriteWordGame(string id, string manifestJson, bool writeWords = true, int wordBytes = 0)
    {
        WriteGame(id, manifestJson);
        File.WriteAllText(Path.Combine(_root, id, "authority.js"), "export function createAuthority(kb) {}");
        if (writeWords)
        {
            var content = wordBytes > 0 ? new string('a', wordBytes) : "apple\nbrave\ncrane\n";
            File.WriteAllText(Path.Combine(_root, id, "words.txt"), content);
        }
    }

    [Fact]
    public void Discovers_a_game_with_valid_authorityWords()
    {
        WriteWordGame("wg", WordGameManifest);
        var catalog = NewCatalog();
        catalog.Discover();

        Assert.True(catalog.TryGet("wg", out var m));
        Assert.NotNull(m.AuthorityWords);
        Assert.Equal("words.txt", m.AuthorityWords!["en"].File);
        Assert.True(m.AuthorityWords["en"].CaseInsensitive);
    }

    [Fact]
    public void Skips_a_game_whose_word_file_is_missing()
    {
        WriteWordGame("wg", WordGameManifest, writeWords: false);
        var catalog = NewCatalog();
        catalog.Discover();
        Assert.False(catalog.TryGet("wg", out _));
    }

    [Fact]
    public void Skips_a_game_whose_word_file_escapes_the_game_folder()
    {
        WriteGame("wg",
            """{ "id": "wg", "name": "W", "entry": "index.html", "maxPlayers": 4, "serverAuthority": "authority.js", "authorityWords": { "en": { "file": "../evil.txt" } } }""");
        File.WriteAllText(Path.Combine(_root, "wg", "authority.js"), "export function createAuthority(kb) {}");
        File.WriteAllText(Path.Combine(_root, "evil.txt"), "apple\n");
        var catalog = NewCatalog();
        catalog.Discover();
        Assert.False(catalog.TryGet("wg", out _));
    }

    [Fact]
    public void Skips_a_game_whose_word_file_exceeds_the_size_cap()
    {
        WriteWordGame("wg", WordGameManifest, wordBytes: 4096);
        var catalog = NewCatalog(authorityMaxWordFileBytes: 1024);
        catalog.Discover();
        Assert.False(catalog.TryGet("wg", out _));
    }

    [Fact]
    public void Skips_a_game_declaring_authorityWords_without_serverAuthority()
    {
        WriteGame("wg",
            """{ "id": "wg", "name": "W", "entry": "index.html", "maxPlayers": 4, "authorityWords": { "en": { "file": "words.txt" } } }""");
        File.WriteAllText(Path.Combine(_root, "wg", "words.txt"), "apple\n");
        var catalog = NewCatalog();
        catalog.Discover();
        Assert.False(catalog.TryGet("wg", out _));
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
        Assert.Equal(dir, catalog.GameLocations["ttt"].Directory);
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
            Assert.Equal(Path.Combine(second, "from-package"), catalog.GameLocations["from-package"].Directory);
        }
        finally { Directory.Delete(second, recursive: true); }
    }

    [Fact]
    public void Validates_a_server_authority_game_against_the_root_it_was_found_in()
    {
        // A packaged server-authority game lives in the SECOND root. Its module and word file must be
        // resolved relative to that directory — validating against gamesRoot/<id> would find nothing
        // and skip the whole game, silently losing every packaged authority game.
        var second = SecondRoot();
        try
        {
            var dir = Path.Combine(second, "packaged-authority");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "GAME.json"),
                """
                { "id": "packaged-authority", "name": "P", "entry": "index.html", "maxPlayers": 2,
                  "serverAuthority": "authority.js",
                  "authorityWords": { "en": { "file": "words.txt" } } }
                """);
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
            File.WriteAllText(Path.Combine(dir, "authority.js"), "export function createAuthority(kb) {}");
            File.WriteAllText(Path.Combine(dir, "words.txt"), "apple\nbrave\n");

            using var catalog = new GameCatalog([_root, second], NullLogger<GameCatalog>.Instance);
            catalog.Discover();

            Assert.True(catalog.TryGet("packaged-authority", out var m));
            Assert.Equal("authority.js", m.ServerAuthority);
            Assert.True(catalog.TryGetDirectory("packaged-authority", out var resolved));
            Assert.Equal(dir, resolved);
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
            Assert.Equal(Path.Combine(_root, "dup"), catalog.GameLocations["dup"].Directory);
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

    // ── Rescan logging ────────────────────────────────────────────────────────
    // Discovery re-runs on every file event under the games roots, on the bind-mount poll, and whenever
    // the package installer asks for another pass. Reporting the whole catalog each time buried the one
    // pass that mattered under dozens of identical ones — in the log file and in the portal's bounded
    // log ring, which is what an operator reads when something has gone wrong.

    [Fact]
    public void A_rescan_that_found_nothing_new_says_nothing_new()
    {
        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        var log = new RecordingLogger<GameCatalog>();
        using var catalog = new GameCatalog(_root, log);

        catalog.Discover();
        var afterFirst = log.Lines.Count;
        catalog.Discover();
        catalog.Discover();

        // The first pass reports normally...
        Assert.Contains(log.At(LogLevel.Information), m => m.Contains("Discovered game 'ttt'"));
        Assert.Contains(log.At(LogLevel.Information), m => m.Contains("Game catalog ready: 1 game(s)"));
        // ...and the two that follow it say the same things at Debug, so nothing is lost — it just stops
        // competing with real events for the reader's attention.
        Assert.Equal(afterFirst, log.At(LogLevel.Information).Count());
        Assert.Equal(afterFirst * 2, log.At(LogLevel.Debug).Count());
    }

    [Fact]
    public void A_rescan_that_found_a_change_reports_it_at_information()
    {
        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        var log = new RecordingLogger<GameCatalog>();
        using var catalog = new GameCatalog(_root, log);
        catalog.Discover();
        catalog.Discover(); // quiet

        WriteGame("second", """{ "id": "second", "name": "S", "entry": "index.html", "maxPlayers": 2 }""");
        log.Lines.Clear();
        catalog.Discover();

        Assert.Contains(log.At(LogLevel.Information), m => m.Contains("Discovered game 'second'"));
        Assert.Contains(log.At(LogLevel.Information), m => m.Contains("Game catalog ready: 2 game(s)"));
    }

    [Fact]
    public void A_broken_game_is_warned_about_once_rather_than_on_every_pass()
    {
        // The complaint is worth making — and worth making again the moment anything about it changes —
        // but a misconfiguration nobody has fixed yet does not become more true every twenty seconds.
        WriteGame("broken", """{ "id": "broken", "name": "B", "entry": "index.html", "maxPlayers": 2 }""",
            writeEntry: false);
        var log = new RecordingLogger<GameCatalog>();
        using var catalog = new GameCatalog(_root, log);

        catalog.Discover();
        catalog.Discover();
        catalog.Discover();

        Assert.Single(log.At(LogLevel.Warning), m => m.Contains("Skipping game 'broken'"));

        // Fixing it is a change, so the pass that sees the fix is reported in full.
        File.WriteAllText(Path.Combine(_root, "broken", "index.html"), "<html></html>");
        log.Lines.Clear();
        catalog.Discover();
        Assert.Contains(log.At(LogLevel.Information), m => m.Contains("Discovered game 'broken'"));
        Assert.Empty(log.At(LogLevel.Warning));
    }

    [Fact]
    public void A_quiet_pass_still_notifies_its_subscribers()
    {
        // Only the LOGGING is conditional. The installer, the pre-compressor and the word-pool caches all
        // reconcile off this event, and one of them going a pass without hearing from the catalog would
        // be a real behaviour change hiding behind a logging one.
        WriteGame("ttt", """{ "id": "ttt", "name": "T", "entry": "index.html", "maxPlayers": 2 }""");
        using var catalog = new GameCatalog(_root, new RecordingLogger<GameCatalog>());
        var raised = 0;
        catalog.Discovered += _ => raised++;

        catalog.Discover();
        catalog.Discover();
        catalog.Discover();

        Assert.Equal(3, raised);
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
