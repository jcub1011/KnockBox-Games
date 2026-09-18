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
/// The persisted half of the update engine: which games this server may update on its own.
/// </summary>
/// <remarks>
/// The apply behaviour itself lives with <see cref="Games.PackageManager"/> and is covered by
/// <see cref="PackageManagerTests"/>; what matters here is that enrolment survives a restart, that
/// nothing is enrolled by default, and that an empty enrolment costs no outbound request.
/// </remarks>
public class GameUpdateCoordinatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kb-updates-{Guid.NewGuid():N}");
    private readonly string _settingsPath;

    public GameUpdateCoordinatorTests()
    {
        Directory.CreateDirectory(_dir);
        _settingsPath = Path.Combine(_dir, "admin-settings.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private AdminSettingsStore NewStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_dir, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = _settingsPath,
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        return new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
    }

    [Fact]
    public void Nothing_is_enrolled_by_default()
    {
        var store = NewStore();

        // An operator who never asked for automatic updates gets none — and with an empty enrolment the
        // scheduled check makes no outbound request at all.
        Assert.Equal(UpdatePolicy.Manual, store.GetUpdatePolicy("demo"));
        Assert.Empty(store.UpdatePolicies);
    }

    [Theory]
    [InlineData(UpdatePolicy.Auto)]
    [InlineData(UpdatePolicy.Drain)]
    [InlineData(UpdatePolicy.Force)]
    public void An_enrolment_survives_a_restart(UpdatePolicy policy)
    {
        Assert.Null(NewStore().SetUpdatePolicy("demo", policy));

        Assert.Equal(policy, NewStore().GetUpdatePolicy("demo"));
    }

    [Fact]
    public void Returning_a_game_to_manual_removes_its_row_rather_than_storing_it()
    {
        var store = NewStore();
        store.SetUpdatePolicy("demo", UpdatePolicy.Auto);

        store.SetUpdatePolicy("demo", UpdatePolicy.Manual);

        // Recorded by ABSENCE, the same trick availability uses for Available — otherwise the file
        // accumulates a row per game ever looked at, and "no policy" and "explicitly manual" become two
        // ways to say one thing.
        Assert.Empty(store.UpdatePolicies);
        Assert.DoesNotContain("\"demo\"", File.ReadAllText(_settingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Game_ids_are_matched_case_insensitively_like_the_catalog()
    {
        var store = NewStore();
        store.SetUpdatePolicy("Demo", UpdatePolicy.Drain);

        Assert.Equal(UpdatePolicy.Drain, store.GetUpdatePolicy("demo"));
    }

    [Fact]
    public void The_policy_is_written_camelCase_so_the_file_matches_the_api()
    {
        NewStore().SetUpdatePolicy("demo", UpdatePolicy.Drain);

        Assert.Contains("\"drain\"", File.ReadAllText(_settingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void An_enrolment_and_an_availability_override_coexist()
    {
        var store = NewStore();
        store.SetUpdatePolicy("demo", UpdatePolicy.Auto);
        store.SetAvailability("demo", GameAvailability.Staged);

        var reloaded = NewStore();
        Assert.Equal(UpdatePolicy.Auto, reloaded.GetUpdatePolicy("demo"));
        Assert.Equal(GameAvailability.Staged, reloaded.GetAvailability("demo"));
    }

    [Fact]
    public void A_hand_edited_junk_policy_row_is_dropped_rather_than_failing_the_file()
    {
        File.WriteAllText(_settingsPath,
            """
            {
              "maintenanceMode": true,
              "updates": { "": "auto", "good": "drain" }
            }
            """);

        var store = NewStore();

        Assert.Null(store.LoadError);
        Assert.True(store.MaintenanceMode);
        Assert.Equal(UpdatePolicy.Drain, store.GetUpdatePolicy("good"));
        Assert.Single(store.UpdatePolicies);
    }

    [Fact]
    public void An_enrolment_for_a_game_that_is_not_installed_is_preserved()
    {
        // A game whose files are briefly absent — a mount that hasn't come up, a package mid-replace —
        // must not come back un-enrolled just because a save happened while it was missing.
        var store = NewStore();
        store.SetUpdatePolicy("not-installed-yet", UpdatePolicy.Auto);
        store.SetMaintenance(true, null);

        Assert.Equal(UpdatePolicy.Auto, NewStore().GetUpdatePolicy("not-installed-yet"));
    }

    [Fact]
    public async Task An_enrolled_game_without_an_installed_version_still_updates()
    {
        // Every hand-made game (including all bundled samples) declares no GAME.json version, so the
        // evaluator reports InstalledVersionUnknown rather than UpdateAvailable. The scheduler must
        // still apply the catalog's offered version once the operator enrolled the game — otherwise
        // Refresh discovers an update on every visit while no cadence ever applies one.
        var root = Path.Combine(_dir, "scheduled");
        var gamesRoot = Path.Combine(root, "games");
        var unpackedRoot = Path.Combine(root, "games-unpacked");
        var managedRoot = Path.Combine(root, "games-managed");
        Directory.CreateDirectory(Path.Combine(gamesRoot, "demo"));
        File.WriteAllText(
            Path.Combine(gamesRoot, "demo", "GAME.json"),
            """{"id":"demo","name":"Demo","entry":"index.html","maxPlayers":2}""");
        File.WriteAllText(Path.Combine(gamesRoot, "demo", "index.html"), "<html></html>");
        Directory.CreateDirectory(unpackedRoot);
        Directory.CreateDirectory(managedRoot);

        var catalog = new GameCatalog([gamesRoot], NullLogger<GameCatalog>.Instance);
        catalog.Discover();
        Assert.True(catalog.TryGet("demo", out _));

        var store = NewStore();
        Assert.Null(store.SetUpdatePolicy("demo", UpdatePolicy.Auto));

        const string catalogUrl = "http://127.0.0.1:9/CATALOG.json";
        var catalogJson = """
            {
              "schemaVersion": "1.0.0",
              "name": "Test",
              "revision": 1,
              "plugins": [
                {
                  "id": "demo",
                  "name": "Demo",
                  "version": "1.0.0",
                  "source": {
                    "type": "github-release",
                    "repo": "o/r",
                    "tag": "v1",
                    "asset": "demo.kbg",
                    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                  }
                }
              ]
            }
            """;
        var fakeHttp = new FakeHttpMessageHandler();
        fakeHttp.Map(catalogUrl, catalogJson);
        var options = new MarketplaceOptions(
            true, catalogUrl, "https://github.com",
            4L * 1024 * 1024, 100L * 1024 * 1024,
            TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10));
        var registry = new MarketplaceSourceRegistry(
            fakeHttp.Client(), options, GamePackageLimits.Default, store,
            maxSources: 8, NullLoggerFactory.Instance);

        var paths = new ContentPaths.Resolved(
            Path.Combine(root, "web"), gamesRoot, Path.Combine(root, "logs"),
            Path.Combine(root, "games-compressed"), unpackedRoot, managedRoot)
        { BlobsRoot = Path.Combine(root, "blobs") };
        var installer = new GamePackageInstaller(
            [new(gamesRoot, PackageMarker.GamesRoot), new(managedRoot, PackageMarker.ManagedRoot)],
            unpackedRoot, GamePackageLimits.Default, null, NullLogger<GamePackageInstaller>.Instance);
        var jobs = new PackageJobRegistry(TimeProvider.System);
        var lobbies = new LobbyManager(TimeProvider.System);
        var connections = new ConnectionManager();
        var gate = new GameLifecycleGate(store);
        var packages = new PackageManager(
            paths, catalog, installer, jobs, gate, lobbies,
            new LobbyCloser(lobbies, connections, NullLogger<LobbyCloser>.Instance),
            GamePackageLimits.Default, new PackageManagerOptions(),
            TimeProvider.System, NullLogger<PackageManager>.Instance);

        var coordinator = new GameUpdateCoordinator(
            registry, packages, catalog, store, NullLogger<GameUpdateCoordinator>.Instance);

        try
        {
            var pass = await coordinator.RunOnceAsync();
            Assert.Null(pass.Error);
            Assert.Equal(1, pass.Considered);
            Assert.Equal(1, pass.Started);
        }
        finally
        {
            catalog.Dispose();
            foreach (var job in jobs.Snapshot())
                jobs.Cancel(job.JobId);
        }
    }
}
