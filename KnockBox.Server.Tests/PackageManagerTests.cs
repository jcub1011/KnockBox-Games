using System.Text;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The install engine: receiving an upload, validating it exactly once through
/// <c>GamePackageReader</c>, placing it atomically, retaining a rollback copy, and honouring the apply
/// mode against lobbies that are running right now.
/// </summary>
public class PackageManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kb-pkgmgr-{Guid.NewGuid():N}");
    private readonly ContentPaths.Resolved _paths;
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly GameCatalog _catalog;
    private readonly GamePackageInstaller _installer;
    private readonly PackageJobRegistry _jobs;
    private readonly GameLifecycleGate _gate;
    private readonly LobbyManager _lobbies;
    private readonly ConnectionManager _connections = new();
    private readonly FakeHttpMessageHandler _http = new();

    private static readonly GamePackageLimits Generous = new(100L * 1024 * 1024, 1000, 10_000);

    public PackageManagerTests()
    {
        _paths = new ContentPaths.Resolved(
            Path.Combine(_root, "web"), Path.Combine(_root, "games"), Path.Combine(_root, "logs"),
            Path.Combine(_root, "games-compressed"), Path.Combine(_root, "games-unpacked"),
            Path.Combine(_root, "games-managed"))
        { BlobsRoot = Path.Combine(_root, "blobs") };
        foreach (var dir in new[] { _paths.GamesRoot, _paths.GamesUnpackedRoot, _paths.GamesManagedRoot })
            Directory.CreateDirectory(dir);

        _catalog = new GameCatalog([_paths.GamesRoot, _paths.GamesUnpackedRoot],
            NullLogger<GameCatalog>.Instance, 1 << 20, 1 << 20);
        _installer = new GamePackageInstaller(
            [new(_paths.GamesRoot, PackageMarker.GamesRoot), new(_paths.GamesManagedRoot, PackageMarker.ManagedRoot)],
            _paths.GamesUnpackedRoot, Generous, null, NullLogger<GamePackageInstaller>.Instance);
        _jobs = new PackageJobRegistry(_clock);
        _lobbies = new LobbyManager(_clock);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_root, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_root, "admin-settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, _clock, NullLogger<AdminAuthService>.Instance);
        _gate = new GameLifecycleGate(new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance));
    }

    public void Dispose()
    {
        _catalog.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PackageManager New(PackageManagerOptions? options = null) => new(
        _paths, _catalog, _installer, _jobs, _gate, _lobbies,
        new LobbyCloser(_lobbies, _connections, NullLogger<LobbyCloser>.Instance),
        Generous, options ?? new PackageManagerOptions(), _clock,
        NullLogger<PackageManager>.Instance);

    private static Stream Bytes(byte[] data) => new MemoryStream(data);

    /// <summary>Uploads a package and waits for its job to reach a terminal state.</summary>
    private async Task<PackageJob> UploadAsync(
        PackageManager manager, byte[] package, PackageApplyMode mode = PackageApplyMode.Drain)
    {
        var staged = await manager.ReceiveAsync(Bytes(package));
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, mode);
        Assert.True(start.Started, start.Error);
        return await SettleAsync(start.Job!.JobId);
    }

    private async Task<PackageJob> SettleAsync(string jobId)
    {
        for (var i = 0; i < 400; i++)
        {
            var job = _jobs.Get(jobId);
            if (job is { IsTerminal: true }) return job;
            // Pump WHILE waiting, not afterwards. Placing a package only renames the .kbg and asks for a
            // rescan; an apply then holds the game's lifecycle gate closed until the installer reports the
            // files actually extracted, so a job cannot reach a terminal state until some reconcile pass
            // runs. In the server that pass comes from GameCatalog.Discovered; here this loop stands in
            // for it. Pumping only after the job settled would wait for something nothing was driving.
            PumpInstaller();
            await Task.Delay(25);
        }
        Assert.Fail($"Job {jobId} never finished (last phase: {_jobs.Get(jobId)?.Phase}).");
        return null!;
    }

    /// <summary>Waits for a job to reach a phase WITHOUT driving the installer, unlike SettleAsync.</summary>
    private async Task WaitForPhaseAsync(string jobId, string phase)
    {
        for (var i = 0; i < 200; i++)
        {
            if (_jobs.Get(jobId)?.Phase == phase) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Job {jobId} never reached '{phase}' (last phase: {_jobs.Get(jobId)?.Phase}).");
    }

    /// <summary>Runs installer passes until the extracted game appears, as the running server would.</summary>
    private void PumpInstaller()
    {
        for (var i = 0; i < 6; i++)
        {
            var result = _installer.Reconcile();
            if (!result.Pending) break;
        }
        _catalog.Discover();
    }

    // ── Receiving ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_upload_lands_in_staging_with_its_byte_count()
    {
        var package = PackageFixture.Valid("demo");

        using var staged = await New().ReceiveAsync(Bytes(package));

        Assert.True(File.Exists(staged.Path));
        Assert.Equal(package.Length, staged.Bytes);
        // Same volume as the destination, so the eventual move into place is a rename, not a copy.
        Assert.StartsWith(ManagedPackageLayout.StagingDir(_paths.GamesManagedRoot), staged.Path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_upload_over_the_byte_cap_is_refused_and_leaves_nothing_behind()
    {
        // Counted while streaming, never trusted from Content-Length — that header is the client's claim.
        var manager = new PackageManager(
            _paths, _catalog, _installer, _jobs, _gate, _lobbies,
            new LobbyCloser(_lobbies, _connections, NullLogger<LobbyCloser>.Instance),
            new GamePackageLimits(64, 1000, 10_000), new PackageManagerOptions(), _clock,
            NullLogger<PackageManager>.Instance);

        await Assert.ThrowsAsync<PackageManager.PackageTooLargeException>(
            () => manager.ReceiveAsync(Bytes(new byte[4096])));

        Assert.Empty(Directory.GetFiles(ManagedPackageLayout.StagingDir(_paths.GamesManagedRoot)));
    }

    [Fact]
    public async Task A_byte_cap_of_zero_means_no_limit_rather_than_refusing_everything()
    {
        // GamePackageLimits' own doc, GamePackageReader and INFRASTRUCTURE.md §9 all say a non-positive
        // value disables that individual check. This path compared against it unconditionally, so an
        // operator following the docs to lift the cap instead had every upload refused at its first byte
        // for exceeding "the 0-byte limit".
        var manager = new PackageManager(
            _paths, _catalog, _installer, _jobs, _gate, _lobbies,
            new LobbyCloser(_lobbies, _connections, NullLogger<LobbyCloser>.Instance),
            new GamePackageLimits(0, 1000, 10_000), new PackageManagerOptions(), _clock,
            NullLogger<PackageManager>.Instance);

        using var staged = await manager.ReceiveAsync(Bytes(PackageFixture.Valid("demo")));

        Assert.True(File.Exists(staged.Path));
    }

    [Fact]
    public async Task A_corrupt_package_is_refused_with_a_reason_and_leaves_no_staged_file()
    {
        // A payload that is not valid Brotli throws InvalidDataException, which is neither
        // GamePackageException nor IOException — so it escaped the catch here, surfaced as an unhandled
        // 500 the operator could do nothing with, and skipped the Dispose that removes the staged upload.
        var manager = New();
        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.CorruptBrotli("demo")));

        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.Invalid, start.Refusal);
        Assert.NotNull(start.Error);
        Assert.False(File.Exists(staged.Path));
        Assert.Empty(Directory.GetFiles(ManagedPackageLayout.StagingDir(_paths.GamesManagedRoot)));
    }

    [Fact]
    public async Task Sweeping_staging_clears_an_interrupted_upload()
    {
        var manager = New();
        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.Valid("demo")));

        manager.SweepStaging();

        Assert.False(File.Exists(staged.Path));
    }

    // ── Installing ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_uploaded_package_becomes_the_managed_package_for_its_id()
    {
        var job = await UploadAsync(New(), PackageFixture.Valid("demo", "Demo"));

        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        Assert.Equal(PackageJobKind.Install, job.Kind);
        Assert.Equal("demo", job.GameId);
        // Canonically named for the id, whatever the operator called the file they uploaded.
        Assert.True(File.Exists(ManagedPackageLayout.PackagePath(_paths.GamesManagedRoot, "demo")));
    }

    [Fact]
    public async Task An_installed_package_is_extracted_and_discovered()
    {
        await UploadAsync(New(), PackageFixture.Valid("demo", "Demo"));

        PumpInstaller();

        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("Demo", manifest.Name);
    }

    [Fact]
    public async Task Bytes_that_are_not_a_package_are_refused_before_a_job_exists()
    {
        var manager = New();
        var staged = await manager.ReceiveAsync(Bytes([1, 2, 3, 4, 5]));

        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.Invalid, start.Refusal);
        // Answered as an error rather than with a job id that fails a second later — the caller is still
        // holding the request.
        Assert.Equal(0, _jobs.Count);
        Assert.False(File.Exists(staged.Path));
    }

    [Fact]
    public async Task A_plain_zip_with_no_kbg_header_is_refused_with_an_actionable_message()
    {
        var manager = New();
        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.ZipWithoutHeader()));

        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);

        Assert.False(start.Started);
        Assert.Contains(GamePackage.HeaderEntryName, start.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_operation_on_the_same_game_is_refused_while_one_is_running()
    {
        // Drain mode with a lobby running holds the id, which is exactly the window a second click
        // arrives in.
        var manager = New();
        await UploadAsync(manager, PackageFixture.Valid("demo"));
        PumpInstaller();
        _lobbies.TryCreate("demo", "host", 4, out _);

        var first = await manager.ReceiveAsync(Bytes(PackageFixture.Valid("demo")));
        var started = manager.StartInstallFromFile(first, PackageJobSource.Upload, PackageApplyMode.Drain);
        Assert.True(started.Started);

        var second = await manager.ReceiveAsync(Bytes(PackageFixture.Valid("demo")));
        var refused = manager.StartInstallFromFile(second, PackageJobSource.Upload, PackageApplyMode.Drain);

        Assert.False(refused.Started);
        Assert.Equal(PackageRefusal.Busy, refused.Refusal);

        _jobs.Cancel(started.Job!.JobId);
        await SettleAsync(started.Job.JobId);
    }

    [Fact]
    public async Task A_game_provided_by_the_read_only_games_folder_cannot_be_replaced()
    {
        // games/ wins a contested id, so a managed copy would never be served. Refused with the fix
        // named, rather than installing something inert.
        File.WriteAllBytes(Path.Combine(_paths.GamesRoot, "demo.kbg"), PackageFixture.Valid("demo"));

        var manager = New();
        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.Valid("demo")));
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.NotManaged, start.Refusal);
        Assert.Contains("games folder", start.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installing_is_refused_when_the_managed_root_is_disabled()
    {
        var manager = New(new PackageManagerOptions(Enabled: false));

        Assert.False(manager.CanInstall);
        Assert.Contains("ManagedPackages", manager.InstallBlockedReason()!, StringComparison.Ordinal);

        var staged = await New().ReceiveAsync(Bytes(PackageFixture.Valid("demo")));
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.Unavailable, start.Refusal);
    }

    // ── Updating and the apply modes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Replacing_an_installed_game_is_reported_as_an_update()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();

        var job = await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        Assert.Equal(PackageJobKind.Update, job.Kind);
        Assert.Equal("1.0.0", job.FromVersion);
        Assert.Equal("2.0.0", job.ToVersion);
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("2.0.0", manifest.Version);
    }

    [Fact]
    public async Task Auto_defers_rather_than_interrupting_a_running_game()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        _lobbies.TryCreate("demo", "host", 4, out _);

        var job = await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"), PackageApplyMode.Auto);
        PumpInstaller();

        Assert.Equal(PackageJobStatus.Cancelled, job.Status);
        Assert.Contains("Deferred", job.Phase, StringComparison.Ordinal);
        // Nothing was touched: the running game is still the version its players started.
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("1.0.0", manifest.Version);
        // And the gate was released, so players can still start new lobbies.
        Assert.True(_gate.CanCreateLobby("demo"));
    }

    [Fact]
    public async Task Force_closes_the_running_lobbies_and_applies()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        _lobbies.TryCreate("demo", "host", 4, out var lobby);

        var job = await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"), PackageApplyMode.Force);
        PumpInstaller();

        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        Assert.Null(_lobbies.Get(lobby!.Id));
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("2.0.0", manifest.Version);
    }

    [Fact]
    public async Task The_game_stays_gated_until_the_new_files_have_actually_been_extracted()
    {
        // Place() only renames the .kbg and asks for a rescan; the extraction happens on a later installer
        // pass, which then swaps the live directory aside. Releasing the gate at the end of Place meant a
        // force update closed every lobby, announced "Updated to 2.0.0", re-opened the game — and a player
        // starting a lobby in that window got the OLD build and then 404s mid-session as it was swapped
        // underneath them. Which is the exact outcome force and drain modes exist to prevent.
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();

        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.Versioned("demo", "Demo", "2.0.0")));
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Force);
        Assert.True(start.Started, start.Error);

        // Nothing is driving reconcile passes here, so the package is placed but not yet extracted.
        await WaitForPhaseAsync(start.Job!.JobId, "Extracting files.");
        Assert.Equal(GameLifecycle.Updating, _gate.StateOf("demo"));
        Assert.False(_gate.CanCreateLobby("demo"));
        Assert.False(_jobs.Get(start.Job.JobId)!.IsTerminal);
        // Still serving 1.0.0 — which is precisely why the gate must not have been released.
        Assert.True(_catalog.TryGet("demo", out var during));
        Assert.Equal("1.0.0", during.Version);

        // SettleAsync drives the passes, standing in for the server's Discovered→Reconcile loop.
        var job = await SettleAsync(start.Job.JobId);

        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        Assert.Null(job.Warning);
        Assert.Equal(GameLifecycle.Idle, _gate.StateOf("demo"));
        Assert.True(_gate.CanCreateLobby("demo"));
        PumpInstaller();
        Assert.True(_catalog.TryGet("demo", out var after));
        Assert.Equal("2.0.0", after.Version);
    }

    [Fact]
    public async Task Drain_waits_for_the_running_lobby_and_blocks_new_ones_meanwhile()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        _lobbies.TryCreate("demo", "host", 4, out var lobby);

        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.Versioned("demo", "Demo", "2.0.0")));
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);
        Assert.True(start.Started);

        // Wait for the job to actually reach the waiting state before asserting on the gate.
        for (var i = 0; i < 200 && _jobs.Get(start.Job!.JobId)!.Status != PackageJobStatus.WaitingForLobbies; i++)
            await Task.Delay(25);

        Assert.Equal(PackageJobStatus.WaitingForLobbies, _jobs.Get(start.Job!.JobId)!.Status);
        // The whole point of draining: no NEW lobby may start, or the wait would never end.
        Assert.False(_gate.CanCreateLobby("demo"));
        Assert.Equal(GameLifecycle.Draining, _gate.StateOf("demo"));
        // And it stays listed while it waits.
        Assert.True(_gate.IsListed("demo"));

        _lobbies.Remove(lobby!.Id);
        var job = await SettleAsync(start.Job.JobId);

        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        Assert.True(_gate.CanCreateLobby("demo"));
    }

    [Fact]
    public async Task A_draining_job_can_be_cancelled_and_releases_the_gate()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        _lobbies.TryCreate("demo", "host", 4, out _);

        var staged = await manager.ReceiveAsync(Bytes(PackageFixture.Versioned("demo", "Demo", "2.0.0")));
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, PackageApplyMode.Drain);
        for (var i = 0; i < 200 && _jobs.Get(start.Job!.JobId)!.Status != PackageJobStatus.WaitingForLobbies; i++)
            await Task.Delay(25);

        Assert.Equal(PackageCancelOutcome.Cancelled, _jobs.Cancel(start.Job!.JobId));
        var job = await SettleAsync(start.Job.JobId);

        Assert.Equal(PackageJobStatus.Cancelled, job.Status);
        // A cancelled drain must not leave the game unlaunchable — that would be worse than the update
        // the operator just called off.
        Assert.True(_gate.CanCreateLobby("demo"));
        PumpInstaller();
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("1.0.0", manifest.Version);
    }

    // ── Backups and rollback ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Updating_retains_the_previous_package()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        var backups = manager.Backups("demo");

        Assert.Single(backups);
        Assert.Equal("1.0.0", backups[0].Version);
    }

    [Fact]
    public async Task Backups_are_pruned_to_the_retention_count()
    {
        var manager = New(new PackageManagerOptions(BackupCount: 2));
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();

        foreach (var version in new[] { "2.0.0", "3.0.0", "4.0.0" })
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", version));
            PumpInstaller();
        }

        var backups = manager.Backups("demo");

        Assert.Equal(2, backups.Count);
        Assert.Equal(["3.0.0", "2.0.0"], backups.Select(b => b.Version)); // newest first
    }

    [Fact]
    public async Task Retention_of_zero_keeps_nothing()
    {
        var manager = New(new PackageManagerOptions(BackupCount: 0));
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        Assert.Empty(manager.Backups("demo"));
    }

    [Fact]
    public async Task Rolling_back_restores_the_previous_version()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        var start = manager.StartRollback("demo", null, PackageApplyMode.Force);
        Assert.True(start.Started, start.Error);
        var job = await SettleAsync(start.Job!.JobId);
        PumpInstaller();

        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        Assert.Equal(PackageJobKind.Rollback, job.Kind);
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("1.0.0", manifest.Version);
    }

    [Fact]
    public async Task Rolling_back_swaps_rather_than_accumulating()
    {
        // With one retained version, v1 and v2 trade places — so repeated rollback toggles predictably
        // instead of growing the backups folder.
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        _clock.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync(manager.StartRollback("demo", null, PackageApplyMode.Force).Job!.JobId);
        PumpInstaller();

        var backups = manager.Backups("demo");
        Assert.Single(backups);
        Assert.Equal("2.0.0", backups[0].Version);
    }

    [Fact]
    public void Rolling_back_with_nothing_retained_is_refused()
    {
        var start = New().StartRollback("demo", null, PackageApplyMode.Force);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.NotFound, start.Refusal);
    }

    [Fact]
    public async Task Rolling_back_to_a_version_that_is_not_retained_is_refused()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        var start = manager.StartRollback("demo", "0.1.0", PackageApplyMode.Force);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.NotFound, start.Refusal);
        Assert.Contains("0.1.0", start.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_corrupted_backup_is_caught_by_the_same_reader_as_a_fresh_package()
    {
        // Age is not trust: a file that has sat on disk for months goes through GamePackageReader.Read
        // exactly like one off the network.
        var manager = New();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));
        PumpInstaller();

        var backup = manager.Backups("demo").Single();
        File.WriteAllBytes(backup.Path, [9, 9, 9, 9]);

        var start = manager.StartRollback("demo", null, PackageApplyMode.Force);

        Assert.False(start.Started);
        Assert.Equal(PackageRefusal.Invalid, start.Refusal);
        // The live package is untouched — a failed rollback must not break a working game.
        PumpInstaller();
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("2.0.0", manifest.Version);
    }

    [Fact]
    public async Task A_package_with_no_version_is_retained_under_a_placeholder()
    {
        var manager = New();
        await UploadAsync(manager, PackageFixture.Valid("demo", "Demo"));
        PumpInstaller();
        await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "1.0.0"));
        PumpInstaller();

        var backups = manager.Backups("demo");

        Assert.Single(backups);
        Assert.Null(backups[0].Version);
    }

    // ── Marketplace installs ──────────────────────────────────────────────────────────────────────
    // The download path, over a faked origin. Everything above drives StartInstallFromFile, which never
    // touches MarketplaceClient — so none of it exercised RunDownload, and a self-deadlock there went
    // unnoticed until an operator watched a real install sit in "Verifying the package." forever.

    /// <summary>Publishes a real package at its derived release URL and returns the catalog entry.</summary>
    private MarketplacePlugin Publish(string id = "demo", string version = "1.0.0")
    {
        var package = MarketplaceFixture.Package(id, version);
        _http.Map(MarketplaceFixture.AssetUrl($"{id}.kbg"), package, contentType: "application/octet-stream");

        var json = MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(
            Id: id, Version: version,
            SourceJson: MarketplaceFixture.Source($"{id}.kbg", MarketplaceFixture.Sha256(package))));
        return MarketplaceClient.Parse(Encoding.UTF8.GetBytes(json)).Plugins![0];
    }

    private MarketplaceClient Downloader() => new(
        _http.Client(), MarketplaceFixture.Options(), Generous, NullLogger<MarketplaceClient>.Instance);

    [Fact]
    public async Task A_marketplace_install_finishes_rather_than_stalling_after_the_download()
    {
        var manager = New();

        var start = manager.StartMarketplaceInstall(Downloader(), Publish(), PackageApplyMode.Force);

        Assert.True(start.Started, start.Error);
        var job = await SettleAsync(start.Job!.JobId);
        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        PumpInstaller();
        Assert.True(_catalog.TryGet("demo", out var manifest));
        Assert.Equal("1.0.0", manifest.Version);
    }

    [Fact]
    public async Task A_marketplace_install_gives_its_install_slot_back()
    {
        // The failure this guards is not a failed job — it is a hung one. RunDownload used to hold the
        // single install permit across ApplyAsync, which waits on the same semaphore, so the job wedged
        // in Verifying (still cancellable, since that status is) and never released the permit. Nothing
        // in the job feed said so; every later install, upload, rollback and uninstall simply queued
        // behind it until the process restarted. So the invariant is asserted directly.
        var manager = New(new PackageManagerOptions { MaxConcurrentInstalls = 1 });

        var start = manager.StartMarketplaceInstall(Downloader(), Publish(), PackageApplyMode.Force);
        var job = await SettleAsync(start.Job!.JobId);

        Assert.Equal(PackageJobStatus.Succeeded, job.Status);
        Assert.Equal(1, manager.AvailableInstallSlots);
    }

    [Fact]
    public async Task A_second_operation_runs_after_a_marketplace_install_rather_than_queueing_forever()
    {
        // The operator-visible half of the same bug: with one permit leaked, the next thing they tried
        // sat in Queued with no explanation.
        var manager = New(new PackageManagerOptions { MaxConcurrentInstalls = 1 });
        await SettleAsync(
            manager.StartMarketplaceInstall(Downloader(), Publish(), PackageApplyMode.Force).Job!.JobId);
        PumpInstaller();

        var second = await UploadAsync(manager, PackageFixture.Versioned("demo", "Demo", "2.0.0"));

        Assert.Equal(PackageJobStatus.Succeeded, second.Status);
        Assert.Equal(1, manager.AvailableInstallSlots);
    }
}
