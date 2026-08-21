using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Design §11: the game origin must never serve a game's server-authority module — it is
/// server-side code (and for hidden-information games, secret). Covers the request gate and the
/// precompressor's skip + prune of the module's variants.
/// </summary>
public class GameOriginAuthorityDenyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-deny-" + Guid.NewGuid().ToString("N"));
    private readonly string _compressed;

    public GameOriginAuthorityDenyTests()
    {
        _compressed = _root + "-compressed";
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_compressed, recursive: true); } catch { /* best effort */ }
    }

    private GameCatalog CatalogWith(string id, string manifestJson, params (string Name, string Content)[] files)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "GAME.json"), manifestJson);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html></html>");
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        var catalog = new GameCatalog(_root, NullLogger<GameCatalog>.Instance);
        catalog.Discover();
        return catalog;
    }

    private const string AuthorityGame =
        """{ "id": "sa", "name": "S", "entry": "index.html", "maxPlayers": 2, "serverAuthority": "authority.js" }""";

    [Theory]
    [InlineData("/games/sa/authority.js")]
    [InlineData("/games/sa/authority.js.br")]     // stale precompressed variant must not leak either
    [InlineData("/games/sa/authority.js.gz")]
    [InlineData("/games/sa/AUTHORITY.JS")]        // case-insensitive filesystems serve it either way
    [InlineData("/GAMES/sa/authority.js")]
    public void Denies_the_authority_module_in_all_its_shapes(string path)
    {
        var catalog = CatalogWith("sa", AuthorityGame, ("authority.js", "export function createAuthority(kb) {}"));
        Assert.True(GameOriginAssetGate.IsDeniedAuthorityAsset(path, catalog));
    }

    [Fact]
    public void Denies_a_nested_authority_module_path()
    {
        var catalog = CatalogWith("sa",
            """{ "id": "sa", "name": "S", "entry": "index.html", "maxPlayers": 2, "serverAuthority": "server/authority.js" }""",
            ("server/authority.js", "export function createAuthority(kb) {}"));
        Assert.True(GameOriginAssetGate.IsDeniedAuthorityAsset("/games/sa/server/authority.js", catalog));
        Assert.False(GameOriginAssetGate.IsDeniedAuthorityAsset("/games/sa/authority.js", catalog));
    }

    // A raw string comparison denies "/games/sa/authority.js" and waves through every spelling below,
    // each of which PhysicalFileProvider (via Path.GetFullPath) resolves to the very same file. The gate
    // has to canonicalize exactly like whatever ends up opening the file.
    [Theory]
    [InlineData("/games/sa//authority.js")]        // doubled separator
    [InlineData("/games/sa///authority.js")]
    [InlineData("/games/sa/./authority.js")]       // "." segment
    [InlineData("/games/sa/.//./authority.js")]
    [InlineData("/games/sa/\\authority.js")]       // Windows treats '\' as a separator
    [InlineData("/games/sa//authority.js.br")]     // …and the same for a stale precompressed variant
    public void Denies_non_canonical_spellings_of_the_authority_module(string path)
    {
        var catalog = CatalogWith("sa", AuthorityGame, ("authority.js", "export function createAuthority(kb) {}"));
        Assert.True(GameOriginAssetGate.IsDeniedAuthorityAsset(path, catalog));
    }

    [Theory]
    [InlineData("/games/wg//words.txt")]
    [InlineData("/games/wg/data//answers.txt")]
    [InlineData("/games/wg/./data/./answers.txt")]
    [InlineData("/games/wg//words.txt.br")]
    public void Denies_non_canonical_spellings_of_a_word_file(string path)
    {
        Assert.True(GameOriginAssetGate.IsDeniedAuthorityAsset(path, WordCatalog()));
    }

    // The manifest side needs canonicalizing too: GameCatalog validates these by RESOLVING the path, so
    // it happily accepts a game that declares "./authority.js" — which a raw comparison never matches.
    [Theory]
    [InlineData("./authority.js")]
    [InlineData("server//authority.js")]
    [InlineData("server\\authority.js")]
    public void Denies_a_module_the_manifest_declared_non_canonically(string declared)
    {
        // The backslash case asserts SEPARATOR semantics, which only Windows has. On Linux a
        // backslash is an ordinary filename character, so the manifest would name a file other than
        // the one written below - a game the catalog skips outright, long before this gate is asked.
        // Skipped rather than deleted: on Windows it really does check that a backslash-spelled
        // manifest cannot serve the module.
        if (declared.Contains('\\') && !OperatingSystem.IsWindows()) return;

        var nested = declared.Replace('\\', '/').Replace("//", "/").TrimStart('.', '/');
        var catalog = CatalogWith("sa",
            $$"""{ "id": "sa", "name": "S", "entry": "index.html", "maxPlayers": 2, "serverAuthority": "{{declared.Replace("\\", "\\\\")}}" }""",
            (nested, "export function createAuthority(kb) {}"));
        Assert.True(GameOriginAssetGate.IsDeniedAuthorityAsset($"/games/sa/{nested}", catalog));
    }

    [Theory]
    [InlineData("/games/sa/index.html")]      // ordinary assets serve
    [InlineData("/games/sa/game.js")]
    [InlineData("/games/sa/authority.js.map")] // only the module and its .br/.gz variants are denied
    [InlineData("/games/other/authority.js")]  // unknown game — the static middleware 404s it anyway
    [InlineData("/knockbox.js")]               // not under /games/ at all
    [InlineData("/games/sa")]                  // no file segment
    [InlineData("/games//authority.js")]        // empty id
    [InlineData("/games/sa/../sa/authority.js")] // traversal: refused as unparseable, and the file
                                                 // providers block it independently
    public void Allows_everything_else(string path)
    {
        var catalog = CatalogWith("sa", AuthorityGame, ("authority.js", "export function createAuthority(kb) {}"));
        Assert.False(GameOriginAssetGate.IsDeniedAuthorityAsset(path, catalog));
    }

    [Fact]
    public void A_game_without_serverAuthority_is_never_denied()
    {
        var catalog = CatalogWith("plain",
            """{ "id": "plain", "name": "P", "entry": "index.html", "maxPlayers": 2 }""",
            ("authority.js", "// just an unfortunately named client asset"));
        Assert.False(GameOriginAssetGate.IsDeniedAuthorityAsset("/games/plain/authority.js", catalog));
    }

    // ── authorityWords files ─────────────────────────────────────────────────

    private const string WordGame =
        """{ "id": "wg", "name": "W", "entry": "index.html", "maxPlayers": 4, "serverAuthority": "authority.js", "authorityWords": { "en": { "file": "words.txt" }, "answers": { "file": "data/answers.txt" } } }""";

    private GameCatalog WordCatalog() => CatalogWith("wg", WordGame,
        ("authority.js", "export function createAuthority(kb) {}"),
        ("words.txt", "apple\nbrave\n"),
        ("data/answers.txt", "crane\n"));

    [Theory]
    [InlineData("/games/wg/words.txt")]
    [InlineData("/games/wg/words.txt.br")]
    [InlineData("/games/wg/words.txt.gz")]
    [InlineData("/games/wg/WORDS.TXT")]
    [InlineData("/games/wg/data/answers.txt")]     // nested word file
    [InlineData("/games/wg/data/answers.txt.br")]
    public void Denies_declared_word_files_in_all_their_shapes(string path)
    {
        Assert.True(GameOriginAssetGate.IsDeniedAuthorityAsset(path, WordCatalog()));
    }

    [Theory]
    [InlineData("/games/wg/index.html")]
    [InlineData("/games/wg/data/other.txt")]       // a non-declared file in the same folder serves
    public void Allows_non_word_files_in_a_word_game(string path)
    {
        Assert.False(GameOriginAssetGate.IsDeniedAuthorityAsset(path, WordCatalog()));
    }

    [Fact]
    public void Precompressor_skips_declared_word_files()
    {
        var catalog = CatalogWith("wg", WordGame,
            ("authority.js", Compressible("export function createAuthority(kb) {}")),
            ("words.txt", Compressible("apple")),
            ("data/answers.txt", Compressible("crane")),
            ("game.js", Compressible("render();")));
        var precompressor = new GameAssetPrecompressor(_compressed, gzip: true, minBytes: 1,
            NullLogger<GameAssetPrecompressor>.Instance);

        precompressor.ReconcileAll(catalog.GameLocations);

        Assert.True(File.Exists(Path.Combine(_compressed, "wg", "game.js.br")));
        Assert.False(File.Exists(Path.Combine(_compressed, "wg", "words.txt.br")));
        Assert.False(File.Exists(Path.Combine(_compressed, "wg", "data", "answers.txt.br")));
    }

    // ── Precompressor exclusion ──────────────────────────────────────────────

    private static string Compressible(string seed) => string.Concat(Enumerable.Repeat(seed + "\n", 200));

    [Fact]
    public void Precompressor_skips_the_authority_module_but_compresses_the_rest()
    {
        var catalog = CatalogWith("sa", AuthorityGame,
            ("authority.js", Compressible("export function createAuthority(kb) { /* rules */ }")),
            ("game.js", Compressible("render();")));
        var precompressor = new GameAssetPrecompressor(_compressed, gzip: true, minBytes: 1,
            NullLogger<GameAssetPrecompressor>.Instance);

        precompressor.ReconcileAll(catalog.GameLocations);

        Assert.True(File.Exists(Path.Combine(_compressed, "sa", "game.js.br")));
        Assert.False(File.Exists(Path.Combine(_compressed, "sa", "authority.js.br")));
        Assert.False(File.Exists(Path.Combine(_compressed, "sa", "authority.js.gz")));
    }

    [Fact]
    public void Precompressor_prunes_a_pre_existing_authority_variant()
    {
        // Simulate a cache warmed BEFORE the game declared serverAuthority (or before this feature
        // existed): the variant is on disk and must be actively deleted, not just skipped.
        var catalog = CatalogWith("sa", AuthorityGame,
            ("authority.js", Compressible("export function createAuthority(kb) {}")));
        var staleDir = Path.Combine(_compressed, "sa");
        Directory.CreateDirectory(staleDir);
        File.WriteAllText(Path.Combine(staleDir, "authority.js.br"), "stale variant bytes");
        var precompressor = new GameAssetPrecompressor(_compressed, gzip: true, minBytes: 1,
            NullLogger<GameAssetPrecompressor>.Instance);

        precompressor.ReconcileAll(catalog.GameLocations);

        Assert.False(File.Exists(Path.Combine(staleDir, "authority.js.br")));
    }
}
