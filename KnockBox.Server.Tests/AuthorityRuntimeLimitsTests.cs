using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The two server-authority knobs an operator can change from the portal: the concurrent-lobby cap and the
/// parsed-module idle window. Both are LIVE reads through <see cref="AuthorityOptionsProvider"/>, which is
/// the whole point — a cap that only took effect after a restart would be no use to whoever is watching the
/// server run out of memory right now. Everything else on <see cref="AuthorityOptions"/> stays captured at
/// engine construction on purpose, and one test here pins that too.
/// </summary>
public class AuthorityRuntimeLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-auth-limits-" + Guid.NewGuid().ToString("N"));

    public AuthorityRuntimeLimitsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private const string Module = """
        export function createAuthority(kb) {
          return { init() {}, applyIntent() { return null; }, snapshot() { return { ok: true }; } };
        }
        """;

    private static readonly GameManifest Manifest = new(
        "authority-game", "A", "index.html", null, 4, ServerAuthority: "authority.js");

    private string WriteGame(string id = "authority-game")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "authority.js"), Module);
        return dir;
    }

    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private ServerAuthorityManager Manager(
        AuthorityOptionsProvider? provider, Func<string, string?> gameDirectory)
    {
        var configured = provider?.Configured ?? AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs());
        return new ServerAuthorityManager(
            gameDirectory,
            configured,
            new ConnectionManager(),
            new LobbyCloser(new LobbyManager(), new ConnectionManager(), NullLogger<LobbyCloser>.Instance),
            _clock,
            new AuthorityWordService(NullLogger<AuthorityWordService>.Instance),
            isDevelopment: false, NullLoggerFactory.Instance,
            metrics: null,
            authorityLimits: provider);
    }

    // Creates a lobby for the authority game and starts its actor, returning whether it started.
    private static bool Start(ServerAuthorityManager manager, LobbyManager lobbies, out string? error)
    {
        Assert.True(lobbies.TryCreate(Manifest.Id, Guid.NewGuid().ToString("N"), 4, out var lobby,
            isServerAuthority: true));
        return manager.TryStart(lobby, Manifest, out error);
    }

    [Fact]
    public void The_authority_lobby_cap_is_read_live_so_a_portal_edit_does_not_wait_for_a_restart()
    {
        var dir = WriteGame();
        var provider = new AuthorityOptionsProvider(
            AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs(("KnockBox:AuthorityMaxLobbies", "1"))));
        var manager = Manager(provider, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        Assert.True(Start(manager, lobbies, out _));
        Assert.False(Start(manager, lobbies, out var refused));
        Assert.Contains("limit of server-authority games", refused);

        // The edit an operator just made in the portal. No restart, no rebuilt manager — the same instance
        // the running server holds.
        provider.Apply(new OperatorAuthorityOptions(MaxLobbies: 3));

        Assert.True(Start(manager, lobbies, out var error), error);
        Assert.Equal(2, manager.ActorCount);
        manager.StopAll();
    }

    [Fact]
    public void Lowering_the_cap_refuses_the_next_lobby_and_leaves_the_running_ones_alone()
    {
        var dir = WriteGame();
        var provider = new AuthorityOptionsProvider(
            AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs()));  // unlimited by default
        var manager = Manager(provider, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        Assert.True(Start(manager, lobbies, out _));
        Assert.True(Start(manager, lobbies, out _));
        Assert.True(Start(manager, lobbies, out _));

        // Now cap it BELOW what is already running. A cap refuses the next one; it never tears down what
        // players are in the middle of, which is what the portal's own wording promises.
        provider.Apply(new OperatorAuthorityOptions(MaxLobbies: 1));

        Assert.Equal(3, manager.ActorCount);
        Assert.False(Start(manager, lobbies, out var refused));
        Assert.Contains("limit of server-authority games", refused);
        Assert.Equal(3, manager.ActorCount);
        manager.StopAll();
    }

    [Fact]
    public void The_default_cap_is_unlimited_so_nothing_refuses_a_lobby_nobody_configured_against()
    {
        var dir = WriteGame();
        var manager = Manager(null, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        // Well past the 100 this used to default to, without the operator setting anything.
        for (var i = 0; i < 12; i++) Assert.True(Start(manager, lobbies, out var error), error);
        Assert.Equal(12, manager.ActorCount);
        manager.StopAll();
    }

    [Fact]
    public void A_manager_built_without_a_provider_uses_the_configured_options()
    {
        // The provider is an optional trailing parameter (the AuthorityMetrics precedent), so the many
        // tests that build a manager directly keep getting the startup record. Pin that fallback: if Live
        // ever stopped defaulting, those tests would silently start reading an empty provider instead.
        var dir = WriteGame();
        var manager = Manager(null, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        Assert.True(Start(manager, lobbies, out var error), error);
        Assert.Equal(1, manager.ActorCount);
        manager.StopAll();
    }

    [Fact]
    public void Sweeping_never_drops_the_module_a_running_lobby_is_using()
    {
        var dir = WriteGame();
        var provider = new AuthorityOptionsProvider(AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityModuleCacheIdleMinutes", "30"))));
        var manager = Manager(provider, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        Assert.True(Start(manager, lobbies, out var error), error);
        Assert.Equal(1, manager.CachedModules);

        // The in-use set comes off the actor map, which is why the module path rides on the actor entry.
        // Far past the window, twice, with no lobby churn in between: a sweep that merely SKIPPED the entry
        // instead of refreshing its stamp would drop it on the second pass.
        _clock.Advance(TimeSpan.FromHours(10));
        manager.SweepModuleCache();
        _clock.Advance(TimeSpan.FromHours(10));
        manager.SweepModuleCache();

        Assert.Equal(1, manager.CachedModules);
        Assert.Equal(0, manager.EvictedModules);

        manager.StopAll();
    }

    [Fact]
    public void Sweeping_drops_a_module_once_its_last_lobby_has_gone_and_the_window_has_passed()
    {
        var dir = WriteGame();
        var provider = new AuthorityOptionsProvider(AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityModuleCacheIdleMinutes", "30"))));
        var manager = Manager(provider, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        Assert.True(lobbies.TryCreate(Manifest.Id, "p1", 4, out var lobby, isServerAuthority: true));
        Assert.True(manager.TryStart(lobby, Manifest, out var error), error);
        manager.Stop(lobby.Id);
        Assert.Equal(0, manager.ActorCount);

        // Still inside the window: a game between two rounds must not pay to re-parse.
        _clock.Advance(TimeSpan.FromMinutes(29));
        manager.SweepModuleCache();
        Assert.Equal(1, manager.CachedModules);

        _clock.Advance(TimeSpan.FromMinutes(2));
        manager.SweepModuleCache();
        Assert.Equal(0, manager.CachedModules);
        Assert.Equal(1, manager.EvictedModules);
    }

    [Fact]
    public void The_idle_window_is_a_live_read_too_so_switching_eviction_on_needs_no_restart()
    {
        var dir = WriteGame();
        // Shipped off. The sweep timer is armed regardless precisely so this can be turned on later.
        var provider = new AuthorityOptionsProvider(AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityModuleCacheIdleMinutes", "0"))));
        var manager = Manager(provider, id => id == Manifest.Id ? dir : null);
        var lobbies = new LobbyManager();

        Assert.True(lobbies.TryCreate(Manifest.Id, "p1", 4, out var lobby, isServerAuthority: true));
        Assert.True(manager.TryStart(lobby, Manifest, out var error), error);
        manager.Stop(lobby.Id);

        _clock.Advance(TimeSpan.FromDays(1));
        manager.SweepModuleCache();
        Assert.Equal(1, manager.CachedModules);

        provider.Apply(new OperatorAuthorityOptions(ModuleCacheIdleMinutes: 30));
        manager.SweepModuleCache();
        Assert.Equal(0, manager.CachedModules);
    }
}
