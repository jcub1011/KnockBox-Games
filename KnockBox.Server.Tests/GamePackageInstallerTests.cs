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

    /// <summary>
    /// The catalog snapshot the precompressor takes, for a game installed from a package: its files
    /// live under the unpacked root, never under the games folder.
    /// </summary>
    private IReadOnlyDictionary<string, GameCatalog.GameLocation> Located(params string[] ids) =>
        ids.ToDictionary(
            id => id,
            id => new GameCatalog.GameLocation(
                new KnockBox.Contracts.GameManifest(id, id, "index.html", null, 4),
                Path.Combine(_unpackedRoot, id)),
            StringComparer.OrdinalIgnoreCase);

    // Every drop gets its own last-write time, one second apart. Real drops are minutes apart; these
    // are microseconds apart, and a filesystem timestamp is far coarser than that (~15.6 ms on
    // Windows, whole seconds on some filesystems). Since the installer keys freshness and quarantine
    // on (mtime, length) — deliberately, to avoid re-hashing hundreds of megabytes every pass — two
    // same-length drops inside one tick are indistinguishable to it, and any test replacing a package
    // would pass or fail on how fast the machine ran. Stamping makes "a different drop" always mean a
    // different stamp, which is what these tests are actually about.
    private DateTime _dropClock = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Drops a package into the games folder and returns its path.</summary>
    private string Drop(string fileName, byte[] package)
    {
        var path = Path.Combine(_gamesRoot, fileName);
        System.IO.File.WriteAllBytes(path, package);
        _dropClock = _dropClock.AddSeconds(1);
        System.IO.File.SetLastWriteTimeUtc(path, _dropClock);
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
        // Same-length payloads on purpose: the mtime is then the only half of the freshness key that
        // distinguishes the two, which is exactly the case Drop's stamping makes deterministic.
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
    public void A_replacement_with_the_same_mtime_is_reinstalled_on_its_length()
    {
        // The other half of the key. A filesystem whose timestamps are coarse (whole seconds on some)
        // can hand two consecutive writes the same mtime; length still tells them apart, so a rebuilt
        // package of a different size installs even then.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null, new File("old.txt", PackageFixture.Bytes("old"))));
        RunToCompletion(New());
        var frozen = System.IO.File.GetLastWriteTimeUtc(Path.Combine(_gamesRoot, "demo.kbg"));

        var path = Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("new.txt", PackageFixture.Bytes("a payload of a very different length"))));
        System.IO.File.SetLastWriteTimeUtc(path, frozen); // pretend the clock never moved

        Assert.True(RunToCompletion(New()));
        Assert.True(System.IO.File.Exists(Installed("demo", "new.txt")));
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
    public void A_package_still_being_copied_over_does_not_uninstall_the_live_game()
    {
        // The routine upgrade: an operator copies a new build over the old file. Every pass sees a
        // different (mtime, length) while the copy is in flight, so the package never settles and never
        // reaches the install registration — which the prune step used to read as "the package is gone"
        // and act on within two passes, deleting the game that was serving players perfectly well.
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();
        RunToCompletion(installer);
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));

        var path = Path.Combine(_gamesRoot, "demo.kbg");
        for (var i = 0; i < 4; i++)
        {
            System.IO.File.AppendAllText(path, "more bytes arriving");
            installer.Reconcile();
            Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")),
                $"pass {i + 1}: a package mid-copy is still present, so its extracted game must survive");
        }
    }

    [Fact]
    public void A_quarantined_replacement_does_not_uninstall_the_live_game()
    {
        // Worse than the mid-copy case, because it is permanent: the replacement is settled but malformed,
        // so it is quarantined and never registers. Treating that as "package gone" deleted a working game
        // for good, in exchange for an operator's typo.
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();
        RunToCompletion(installer);

        Drop("demo.kbg", PackageFixture.Build("demo", "Broken",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], formatVersion: 99));
        string? reported = null;
        for (var i = 0; i < 5; i++)
        {
            installer.Reconcile();
            reported ??= installer.InstallFailure;
        }

        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")),
            "a malformed replacement must be refused, not paid for with the working installation");
        // Reported once, on the pass that read it — later passes stay quiet because it's quarantined.
        Assert.Contains("demo.kbg", reported);
    }

    [Fact]
    public async Task Concurrent_passes_do_not_collapse_the_settle_guard()
    {
        // Both guards compare a pass against what the PREVIOUS pass recorded, so they only mean anything
        // if passes are separated by real time. The coalescing gate used to run a second pass immediately
        // on behalf of a caller that arrived mid-pass, which made a package dropped microseconds ago look
        // settled — defeating the one check that stops a half-copied archive from being read.
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();

        using var barrier = new Barrier(2);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            installer.Reconcile();
        })));

        Assert.False(Directory.Exists(Path.Combine(_unpackedRoot, "demo")),
            "a package must never install within a single burst of overlapping passes");
    }

    [Fact]
    public void An_unchanged_pass_does_not_open_the_archive()
    {
        // Reconcile's contract is "a pass where nothing changed costs one directory listing plus a stat per
        // package". Recovering the id was the only reason the no-change path opened the file — and a ZIP's
        // central directory is at the END, so that was a seek through a potentially huge archive, per
        // package, per pass. Holding the file exclusively proves the pass no longer touches it.
        Drop("demo.kbg", PackageFixture.Valid("demo"));
        var installer = New();
        RunToCompletion(installer);

        var path = Path.Combine(_gamesRoot, "demo.kbg");
        using (var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Prove the lock denies readers on this platform, or the assertion below proves nothing.
            try
            {
                using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return;
            }
            catch (IOException) { /* denied, as intended */ }

            var result = installer.Reconcile();

            Assert.False(result.Changed);
            Assert.Null(installer.InstallFailure);
        }
        Assert.True(Directory.Exists(Path.Combine(_unpackedRoot, "demo")));
    }

    [Fact]
    public void Bookkeeping_for_a_package_that_is_gone_is_forgotten()
    {
        // Settle stamps and quarantine rows used to accumulate for the process lifetime: every package name
        // ever dropped in kept a row, and an operator iterating on a broken build added one per attempt.
        Drop("bad.kbg", PackageFixture.Build("bad", "Bad",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], formatVersion: 99));
        var installer = New();
        RunToCompletion(installer);
        Assert.True(installer.TrackedPackages > 0);

        System.IO.File.Delete(Path.Combine(_gamesRoot, "bad.kbg"));
        installer.Reconcile();

        Assert.Equal(0, installer.TrackedPackages);
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
        Assert.Single(Directory.EnumerateDirectories(_unpackedRoot), d => !new DirectoryInfo(d).Name.StartsWith('.'));
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

    [Theory]
    [InlineData(true)]   // the DEFAULT (KnockBox:PrecompressGzip), and the case that regressed
    [InlineData(false)]
    public void A_seeded_asset_is_not_recompressed_by_the_next_reconcile(bool gzip)
    {
        // Seeding writes an index row keyed to the extracted file's (mtime, length) — the same thing the
        // ordinary pass compares — so the asset must read as fresh rather than being redone.
        //
        // Both gzip settings are covered deliberately. This test originally ran only with gzip:false and
        // so missed a real bug: CompressGameDir treats "produced" as "every expected variant is present"
        // (VariantsPresent), so seeding only the .br left every asset looking stale under the default
        // configuration and the next reconcile recompressed it at SmallestSize — exactly the cost seeding
        // exists to avoid. Caught by installing a real game, not by this suite.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Filler(), Brotli: true)));

        var precompressor = new GameAssetPrecompressor(
            _compressedRoot, gzip, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));

        var variant = Path.Combine(_compressedRoot, "demo", "code.js.br");
        var before = System.IO.File.GetLastWriteTimeUtc(variant);
        var bytesBefore = System.IO.File.ReadAllBytes(variant);
        // With gzip on, seeding must also lay down the .gz, or the freshness check can never pass.
        Assert.Equal(gzip, System.IO.File.Exists(Path.Combine(_compressedRoot, "demo", "code.js.gz")));

        precompressor.ReconcileAll(Located("demo"));

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
            _compressedRoot, gzip: true, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));

        var games = Located("demo");
        precompressor.ReconcileAll(games);
        precompressor.ReconcileAll(games);

        Assert.True(System.IO.File.Exists(Path.Combine(_compressedRoot, "demo", "code.js.br")));
    }

    [Fact]
    public void An_upgrade_that_stores_a_file_raw_drops_the_previous_variant()
    {
        // v1's payload compresses; v2's is dense, so the packer stores it identity and the seed records
        // "tried, not beneficial". Recording that outcome without ALSO dropping the old variant (which is
        // what Compress() does when it returns false) left v1's .br on disk permanently: a not-produced
        // index row is skipped by every later pass and is not an orphan to the pruner, so every
        // br-accepting client kept receiving v1's bytes at v2's URL.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Filler(), Brotli: true)));

        var precompressor = new GameAssetPrecompressor(
            _compressedRoot, gzip: true, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));

        var variant = Path.Combine(_compressedRoot, "demo", "code.js.br");
        Assert.True(System.IO.File.Exists(variant));

        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Bytes("already-dense bytes the packer stored raw"))));
        Assert.True(RunToCompletion(New(precompressor)));

        Assert.False(System.IO.File.Exists(variant),
            "the previous version's variant must not survive an upgrade that stores the file raw");
    }

    [Fact]
    public void A_reconcile_that_predates_discovery_keeps_the_freshly_seeded_cache()
    {
        // The installer seeds the cache and only THEN asks for a rediscovery, so for the debounce plus scan
        // that follows, the id is in no catalog map — and the pruner's rule is "absent from the catalog ⇒
        // delete the directory". A reconcile landing in that window (the periodic timer, or the sibling
        // Discovered handler still carrying the pre-install map) deleted the seed it had just written, and
        // the next pass re-paid the max-effort Brotli the seed exists to avoid.
        Drop("demo.kbg", PackageFixture.Valid("demo", null, null,
            new File("code.js", PackageFixture.Filler(), Brotli: true)));

        var precompressor = new GameAssetPrecompressor(
            _compressedRoot, gzip: true, minBytes: 16, NullLogger<GameAssetPrecompressor>.Instance);
        RunToCompletion(New(precompressor));
        var variant = Path.Combine(_compressedRoot, "demo", "code.js.br");
        Assert.True(System.IO.File.Exists(variant));

        var beforeDiscovery = new Dictionary<string, GameCatalog.GameLocation>(StringComparer.OrdinalIgnoreCase);
        precompressor.ReconcileAll(beforeDiscovery);

        Assert.True(System.IO.File.Exists(variant), "a not-yet-discovered game's seed must survive a reconcile");

        // The grace is not a permanent exemption: once the extracted game is actually gone, so is its cache.
        Directory.Delete(Path.Combine(_unpackedRoot, "demo"), recursive: true);
        precompressor.ReconcileAll(beforeDiscovery);

        Assert.False(Directory.Exists(Path.Combine(_compressedRoot, "demo")));
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
