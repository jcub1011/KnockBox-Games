using KnockBox.Server.Admin;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The transient half of game policy laid over the persisted half: an update in flight blocks new
/// lobbies without touching, or being touched by, anything an operator saved.
/// </summary>
public class GameLifecycleGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kb-lifecycle-{Guid.NewGuid():N}");
    private readonly AdminSettingsStore _settings;
    private readonly GameLifecycleGate _gate;

    public GameLifecycleGateTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_dir, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_dir, "admin-settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        _settings = new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
        _gate = new GameLifecycleGate(_settings);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void An_idle_game_is_startable_and_offers_no_special_reason()
    {
        Assert.Equal(GameLifecycle.Idle, _gate.StateOf("demo"));
        Assert.True(_gate.CanCreateLobby("demo"));
        Assert.Null(_gate.UnavailableReason("demo"));
        Assert.Empty(_gate.States);
    }

    [Theory]
    [InlineData(GameLifecycle.Draining)]
    [InlineData(GameLifecycle.Updating)]
    public void A_gated_game_refuses_new_lobbies_with_a_reason(GameLifecycle state)
    {
        _gate.Enter("demo", state);

        Assert.False(_gate.CanCreateLobby("demo"));
        // A reason players can act on, rather than the generic shrug a disabled game earns.
        Assert.Contains("Try again", _gate.UnavailableReason("demo")!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gated_game_stays_listed()
    {
        // Deliberate: a tile vanishing from the grid and reappearing a minute later reads as a broken
        // platform, and the refusal message says far more than an absent tile does.
        _gate.Enter("demo", GameLifecycle.Updating);

        Assert.True(_gate.IsListed("demo"));
    }

    [Fact]
    public void The_gate_only_affects_the_game_it_names()
    {
        _gate.Enter("demo", GameLifecycle.Updating);

        Assert.True(_gate.CanCreateLobby("other"));
        Assert.Null(_gate.UnavailableReason("other"));
    }

    [Fact]
    public void Game_ids_are_matched_case_insensitively_like_the_catalog()
    {
        _gate.Enter("Demo", GameLifecycle.Draining);

        Assert.False(_gate.CanCreateLobby("demo"));
        Assert.Equal(GameLifecycle.Draining, _gate.StateOf("DEMO"));
    }

    [Fact]
    public void Leaving_returns_a_game_to_startable()
    {
        _gate.Enter("demo", GameLifecycle.Updating);
        _gate.Leave("demo");

        Assert.True(_gate.CanCreateLobby("demo"));
        Assert.Empty(_gate.States);
    }

    [Fact]
    public void Leaving_a_game_that_was_never_gated_is_harmless()
    {
        _gate.Leave("never-seen"); // must not throw

        Assert.Empty(_gate.States);
    }

    [Fact]
    public void Entering_idle_is_the_same_as_leaving()
    {
        _gate.Enter("demo", GameLifecycle.Updating);
        _gate.Enter("demo", GameLifecycle.Idle);

        Assert.True(_gate.CanCreateLobby("demo"));
        Assert.Empty(_gate.States);
    }

    [Fact]
    public void Persisted_policy_still_applies_underneath()
    {
        _settings.SetAvailability("demo", GameAvailability.Disabled);

        // Both layers must allow it. The gate composes the store rather than replacing it, so an
        // operator's decision is never lost behind an engine that happens to be idle.
        Assert.False(_gate.CanCreateLobby("demo"));
        Assert.False(_gate.IsListed("demo"));
        // Nothing is in flight, so there is no engine-specific reason to give.
        Assert.Null(_gate.UnavailableReason("demo"));
    }

    [Fact]
    public void Maintenance_mode_passes_straight_through()
    {
        _settings.SetMaintenance(true, "Back at 5pm.");

        Assert.True(_gate.MaintenanceMode);
        Assert.Equal("Back at 5pm.", _gate.MaintenanceMessage);
        Assert.False(_gate.CanCreateLobby("demo"));
    }

    [Fact]
    public void Concurrent_gate_changes_do_not_lose_entries()
    {
        // Readers are lock-free over an immutable snapshot; writers serialize. A copy-and-swap that
        // dropped a concurrent write would leave a game gated forever with nothing to clear it.
        Parallel.For(0, 100, i => _gate.Enter($"game-{i}", GameLifecycle.Draining));

        Assert.Equal(100, _gate.States.Count);

        Parallel.For(0, 100, i => _gate.Leave($"game-{i}"));

        Assert.Empty(_gate.States);
    }
}
