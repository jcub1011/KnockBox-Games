using KnockBox.Server.Games;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using File = KnockBox.Server.Tests.PackageFixture.File;

namespace KnockBox.Server.Tests;

/// <summary>
/// Lifecycle tests for <c>.kbg</c> installation: settling, freshness, replacement, uninstall, id
/// collisions, and quarantine. Format validation itself lives in <see cref="GamePackageReaderTests"/>.
/// </summary>
public class GamePackageInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-pkginst-" + Guid.NewGuid().ToString("N"));
    private readonly string _gamesRoot;
    private readonly string _unpackedRoot;
    private readonly string _compressedRoot;

    public GamePackageInstallerTests()
    {
        _gamesRoot = Path.Combine(_root, "games");
        _unpackedRoot = Path.Combine(_root, "games-unpacked");
        _compressedRoot = Path.Combine(_root, "games-compressed");
        Directory.CreateDirectory(_gamesRoot);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static readonly GamePackageLimits Generous = new(100L * 1024 * 1024, 1000, 10_000);

    private GamePackageInstaller New(GameAssetPrecompressor? precompressor = null, GamePackageLimits? limits = null) =>
        new(_gamesRoot, _unpackedRoot, limits ?? Generous, precompressor,
            NullLogger<GamePackageInstaller>.Instance);

    /// <summary>Drops a package into the games folder and returns its path.</summary>
    private string Drop(string fileName, byte[] package)
    {
        var path = Path.Combine(_gamesRoot, fileName);
        System.IO.File.WriteAllBytes(path, package);
        return path;
    }

    /// <summary>
    /// Runs passes until nothing is pending. A package must present the same (mtime, length) on two
    /// consecutive passes before it installs, so a single pass is never enough — that is the guard that
    /// stops a half-copied archive being read.
    /// </summary>
    private static bool RunToCompletion(GamePackageInstaller installer, int maxPasses = 6)
    {
        var changed = false;
        for (var i = 0; i < maxPasses; i++)
        {
            var result = installer.Reconcile();
            changed |= result.Changed;
            if (!result.Pending) break;
        }
        return changed;
    }

    private string Installed(string id, string relative) => Path.Combine(_unpackedRoot, id, relative);

    // ── Installing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Installs_a_package_into_a_folder_named_after_its_id()
    {
        Drop("anything.kbg", PackageFixture.Valid("demo", "Demo"));

        Assert.True(RunToCompletion(New()));

        // The folder is named from the id, NOT the archive filename — GameCatalog requires the folder
        // name to equal the manifest id.
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
        Assert.True(System.IO.File.Exists(Installed("demo", "GAME.json")));
        Assert.True(System.IO.File.Exists(Installed("demo", "index.html")));
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "anything")));
    }

    [Fact]
    public void Reports_how_many_packages_it_saw()
    {
        Drop("a.kbg", PackageFixture.Valid("a"));
        Drop("b.kbg", PackageFixture.Valid("b"));

        RunToCompletion(New());

        var installer = New();
        installer.Reconcile();
        Assert.Equal(2, installer.PackagesObserved);
    }

    [Fact]
    public void Round_trips_brotli_and_identity_payloads()
    {
        var payload = PackageFixture.Filler();
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("assets/code.js", payload, Brotli: true),
            new File("assets/note.txt", PackageFixture.Bytes("plain"))));

        RunToCompletion(New());

        Assert.Equal(payload, System.IO.File.ReadAllBytes(Installed("demo", Path.Combine("assets", "code.js"))));
        Assert.Equal("plain", System.IO.File.ReadAllText(Installed("demo", Path.Combine("assets", "note.txt"))));
    }

    [Fact]
    public void Does_not_install_a_package_that_has_not_settled()
    {
        // A large archive still being copied changes between passes. One pass must therefore never be
        // enough, or a partially-written file would be read.
        Drop("demo.kbg", PackageFixture.Valid("demo"));

        var installer = New();
        var first = installer.Reconcile();

        Assert.False(first.Changed);
        Assert.True(first.Pending, "an unsettled package must ask for another pass");
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        var second = installer.Reconcile();
        Assert.True(second.Changed);
        Assert.False(second.Pending);
    }

    [Fact]
    public void A_package_that_keeps_changing_is_never_installed_until_it_stops()
    {
        var path = Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();

        for (var i = 0; i < 3; i++)
        {
            installer.Reconcile();
            // Simulate more bytes arriving between passes.
            System.IO.File.AppendAllText(path, "x");
            Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
        }
    }

    [Fact]
    public void An_already_current_package_is_not_reinstalled()
    {
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();
        Assert.True(RunToCompletion(installer));

        // A marker inside the extracted folder records which package file and version produced it.
        var result = installer.Reconcile();
        Assert.False(result.Changed);
        Assert.False(result.Pending);
    }

    [Fact]
    public void A_fresh_installer_recognises_an_existing_installation()
    {
        // Freshness lives on disk, not in memory, so a restarted server must not re-extract the library.
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        RunToCompletion(New());

        Assert.False(RunToCompletion(New()), "a restart should not reinstall an unchanged package");
    }

    [Fact]
    public void A_replaced_package_is_reinstalled_and_stale_files_disappear()
    {
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null, new File("old.txt", PackageFixture.Bytes("old"))));
        RunToCompletion(New());
        Assert.True(System.IO.File.Exists(Installed("demo", "old.txt")));

        // Replace with a version that no longer contains old.txt.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null, new File("new.txt", PackageFixture.Bytes("new"))));
        Assert.True(RunToCompletion(New()));

        Assert.True(System.IO.File.Exists(Installed("demo", "new.txt")));
        // The swap replaces the folder wholesale, so nothing survives from the previous version.
        Assert.False(System.IO.File.Exists(Installed("demo", "old.txt")));
    }

    [Fact]
    public void Installing_leaves_no_staging_directories_behind()
    {
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        RunToCompletion(New());

        var staging = Path.Combine(_unpackedRoot, ".staging");
        Assert.True(!Directory.Exists(staging) || Directory.EnumerateDirectories(staging).Count() == 0);
        // Only the game folder (and the dot-prefixed staging container) may exist at the top level.
        Assert.Equal(["demo"], Directory.EnumerateDirectories(_unpackedRoot)
            .Select(d => new DirectoryInfo(d).Name).Where(n => !n.StartsWith('.')).Order());
    }

    // ── Uninstalling ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Uninstalls_a_game_whose_package_is_removed_but_not_on_the_first_pass()
    {
        var path = Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();
        RunToCompletion(installer);
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        System.IO.File.Delete(path);

        // An operator replacing a package by delete-then-copy transiently has no file there. Removing on
        // the first sighting would drop a live game out of the catalog during that window.
        var first = installer.Reconcile();
        Assert.False(first.Changed);
        Assert.True(first.Pending, "the countdown must ask for another pass");
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        var second = installer.Reconcile();
        Assert.True(second.Changed);
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
    }

    [Fact]
    public void A_package_restored_during_the_countdown_is_kept()
    {
        var package = PackageFixture.Valid("demo");
        var path = Drop("demo.kbg", package);
        var installer = New();
        RunToCompletion(installer);

        System.IO.File.Delete(path);
        installer.Reconcile();              // countdown starts
        Drop("demo.kbg", package);          // operator finishes the replace
        RunToCompletion(installer);

        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")),
            "a package restored mid-countdown must not be uninstalled");
    }

    [Fact]
    public void An_unreadable_games_folder_uninstalls_nothing()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permission bits only

        Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();
        RunToCompletion(installer);
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        System.IO.File.SetUnixFileMode(_gamesRoot, UnixFileMode.None);
        try
        {
            try { _ = Directory.EnumerateFiles(_gamesRoot).Any(); return; } // running as root: can't test
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            // A listing failure must NOT read as "no packages exist" — that would wipe the whole library
            // over a transient permissions problem.
            installer.Reconcile();
            installer.Reconcile();
            Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
        }
        finally
        {
            System.IO.File.SetUnixFileMode(_gamesRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ── Bad packages ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_malformed_package_is_reported_and_does_not_stop_the_others()
    {
        Drop("good.kbg", PackageFixture.Valid("good"));
        Drop("bad.kbg", PackageFixture.Build("bad", "Bad",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], formatVersion: 99));

        var installer = New();
        RunToCompletion(installer);

        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "good")));
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "bad")));
        Assert.NotNull(installer.InstallFailure);
        Assert.Contains("bad.kbg", installer.InstallFailure);
    }

    [Fact]
    public void A_malformed_package_is_quarantined_rather_than_retried_every_pass()
    {
        Drop("bad.kbg", PackageFixture.Build("bad", "Bad",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], formatVersion: 99));

        var installer = New();
        RunToCompletion(installer);
        Assert.NotNull(installer.InstallFailure);

        // Second look: the file hasn't changed, so it isn't re-read and isn't re-reported. Without this a
        // single broken package would log an error every reconcile, forever.
        var again = installer.Reconcile();
        Assert.False(again.Changed);
        Assert.False(again.Pending);
        Assert.Null(installer.InstallFailure);
    }

    [Fact]
    public void A_repaired_package_is_retried_after_quarantine()
    {
        Drop("demo.kbg", PackageFixture.Build("demo", "Demo",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], formatVersion: 99));
        var installer = New();
        RunToCompletion(installer);
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        // Quarantine is keyed on (path, mtime, length), so replacing the file clears it.
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        Assert.True(RunToCompletion(installer));
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
    }

    [Fact]
    public void A_file_that_is_not_a_zip_is_skipped_without_throwing()
    {
        Drop("garbage.kbg", PackageFixture.Bytes("this is definitely not a zip archive"));

        var installer = New();
        RunToCompletion(installer); // must not throw

        Assert.NotNull(installer.InstallFailure);
    }

    [Fact]
    public void A_truncated_package_installs_once_the_copy_completes()
    {
        var package = PackageFixture.Valid("demo");
        var path = Path.Combine(_gamesRoot, "demo.kbg");
        System.IO.File.WriteAllBytes(path, package[..(package.Length / 2)]);

        var installer = New();
        RunToCompletion(installer);
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        System.IO.File.WriteAllBytes(path, package); // the copy finishes
        Assert.True(RunToCompletion(installer));
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
    }

    [Fact]
    public void A_traversal_package_writes_nothing_outside_the_game_folder()
    {
        var canary = Path.Combine(_root, "canary.txt");
        System.IO.File.WriteAllText(canary, "untouched");

        Drop("evil.kbg", PackageFixture.Valid("demo", null, null,
            new File("../../canary.txt", PackageFixture.Bytes("OVERWRITTEN"))));

        var installer = New();
        RunToCompletion(installer);

        Assert.Equal("untouched", System.IO.File.ReadAllText(canary));
        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
        Assert.NotNull(installer.InstallFailure);
    }

    [Fact]
    public void Ignores_files_that_are_not_packages()
    {
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        System.IO.File.WriteAllText(Path.Combine(_gamesRoot, "notes.txt"), "hello");
        System.IO.File.WriteAllText(Path.Combine(_gamesRoot, "archive.zip"), "not ours");

        var installer = New();
        RunToCompletion(installer);

        Assert.Equal(1, installer.PackagesObserved);
        Assert.Null(installer.InstallFailure);
    }

    // ── Id collisions ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_packages_claiming_one_id_resolve_deterministically()
    {
        // Both declare id "demo". Ordinal filename order decides, so the outcome is stable across passes
        // and hosts rather than depending on directory-enumeration order.
        Drop("aaa.kbg", PackageFixture.Valid("demo", "First"));
        Drop("zzz.kbg", PackageFixture.Valid("demo", "Second"));

        RunToCompletion(New());

        var manifest = System.IO.File.ReadAllText(Installed("demo", "GAME.json"));
        Assert.Contains("First", manifest);
        Assert.Single(Directory.EnumerateDirectories(_unpackedRoot).Where(d => !new DirectoryInfo(d).Name.StartsWith('.')));
    }

    [Fact]
    public void Removing_the_winner_lets_the_other_package_take_over()
    {
        Drop("aaa.kbg", PackageFixture.Valid("demo", "First"));
        Drop("zzz.kbg", PackageFixture.Valid("demo", "Second"));
        var installer = New();
        RunToCompletion(installer);

        System.IO.File.Delete(Path.Combine(_gamesRoot, "aaa.kbg"));
        // Uninstall of the old copy takes its countdown, then the remaining package installs.
        for (var i = 0; i < 6; i++) installer.Reconcile();

        Assert.Contains("Second", System.IO.File.ReadAllText(Installed("demo", "GAME.json")));
    }

    // ── Serving-cache seeding ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Seeds_the_precompressed_cache_from_the_packages_brotli_payloads()
    {
        // The whole reason payloads are per-file Brotli: the server copies them straight into its HTTP
        // serving cache instead of re-running Brotli at maximum effort (~50s for a large wasm).
        var payload = PackageFixture.Filler();
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null, new File("code.js", payload, Brotli: true)));

        var precompressor = new GameAssetPrecompressor(
            _compressedRoot, gzip: true, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));

        var variant = Path.Combine(_compressedRoot, "demo", "code.js.br");
        Assert.True(System.IO.File.Exists(variant), "the package's Brotli blob should have been copied into the cache");

        using var fs = System.IO.File.OpenRead(variant);
        using var br = new System.IO.Compression.BrotliStream(fs, System.IO.Compression.CompressionMode.Decompress);
        using var ms = new MemoryStream();
        br.CopyTo(ms);
        Assert.Equal(payload, ms.ToArray());
    }

    [Fact]
    public void A_seeded_asset_is_not_recompressed_by_the_next_reconcile()
    {
        // Seeding writes an index row keyed to the extracted file's (mtime, length) — the same thing the
        // ordinary pass compares — so the asset must read as fresh rather than being redone.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Filler(), Brotli: true)));

        var precompressor = new GameAssetPrecompressor(
            _compressedRoot, gzip: false, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));

        var variant = Path.Combine(_compressedRoot, "demo", "code.js.br");
        var before = System.IO.File.GetLastWriteTimeUtc(variant);
        var bytesBefore = System.IO.File.ReadAllBytes(variant);

        precompressor.ReconcileAll(new Dictionary<string, string> { ["demo"] = Path.Combine(_unpackedRoot, "demo") });

        Assert.Equal(before, System.IO.File.GetLastWriteTimeUtc(variant));
        Assert.Equal(bytesBefore, System.IO.File.ReadAllBytes(variant));
    }

    [Fact]
    public void A_package_backed_game_keeps_its_cache_across_reconciles()
    {
        // Regression guard: the precompressor used to test gamesRoot/<id> for existence, which is never
        // true for a game installed from a package — so every pass deleted the cache and recompressed the
        // whole game at maximum effort, forever.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Filler(), Brotli: true)));

        var precompressor = new GameAssetPrecompressor(
            _compressedRoot, gzip: false, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));

        var games = new Dictionary<string, string> { ["demo"] = Path.Combine(_unpackedRoot, "demo") };
        precompressor.ReconcileAll(games);
        precompressor.ReconcileAll(games);

        Assert.True(System.IO.File.Exists(Path.Combine(_compressedRoot, "demo", "code.js.br")));
    }

    [Fact]
    public void Works_without_a_precompressor()
    {
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Filler(), Brotli: true)));

        Assert.True(RunToCompletion(New(precompressor: null)));
        Assert.True(System.IO.File.Exists(Installed("demo", "code.js")));
        Assert.False(Directory.Exists(_compressedRoot));
    }

    // ── Limits ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_package_over_the_size_limit_is_refused()
    {
        Drop("big.kbg", PackageFixture.Valid("demo", null, null, new File("big.bin", new byte[64 * 1024])));

        var installer = New(limits: Generous with { MaxBytes = 4096 });
        RunToCompletion(installer);

        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
        Assert.Contains("MaxPackageBytes", installer.InstallFailure);
    }

    // ── Empty and missing directories ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_games_folder_creates_nothing()
    {
        var installer = New();
        var result = installer.Reconcile();

        Assert.False(result.Changed);
        Assert.False(result.Pending);
        Assert.Equal(0, installer.PackagesObserved);
        // Don't create a cache directory when there is nothing to cache.
        Assert.False(Directory.Exists(_unpackedRoot));
    }

    [Fact]
    public void A_missing_games_folder_is_benign()
    {
        Directory.Delete(_gamesRoot, recursive: true);

        var result = New().Reconcile(); // must not throw
        Assert.False(result.Changed);
    }
}
