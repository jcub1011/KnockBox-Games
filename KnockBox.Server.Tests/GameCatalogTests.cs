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
}
