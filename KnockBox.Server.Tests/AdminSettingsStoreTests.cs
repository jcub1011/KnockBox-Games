using KnockBox.Server.Admin;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Pins the operator-policy store: that a change takes effect immediately, that it survives a restart,
/// and that a settings file the server can't read degrades to defaults loudly rather than crashing or
/// silently re-enabling a game the operator disabled.
/// </summary>
public class AdminSettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _settingsPath;
    private readonly string _secretPath;

    public AdminSettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"kb-admin-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _settingsPath = Path.Combine(_directory, "settings.json");
        _secretPath = Path.Combine(_directory, "admin.secret");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private AdminSettingsStore NewStore(string? settingsPath = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = _secretPath,
            ["KnockBox:AdminSettingsPath"] = settingsPath ?? _settingsPath,
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        return new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
    }

    [Fact]
    public void A_fresh_server_has_no_overrides_and_is_not_in_maintenance()
    {
        var store = NewStore();
        Assert.False(store.MaintenanceMode);
        Assert.Null(store.MaintenanceMessage);
        Assert.Null(store.LoadError);
        Assert.Empty(store.Overrides);
        Assert.Equal(GameAvailability.Available, store.GetAvailability("anything"));
        Assert.True(store.CanCreateLobby("anything"));
        Assert.True(store.IsListed("anything"));
    }

    [Fact]
    public void Setting_availability_takes_effect_immediately_and_survives_a_restart()
    {
        Assert.Null(NewStore().SetAvailability("tictactoe", GameAvailability.Disabled));

        // A second store over the same file is what a restart looks like from here.
        var reloaded = NewStore();
        Assert.Equal(GameAvailability.Disabled, reloaded.GetAvailability("tictactoe"));
        Assert.False(reloaded.CanCreateLobby("tictactoe"));
        Assert.False(reloaded.IsListed("tictactoe"));
    }

    [Fact]
    public void Maintenance_mode_and_its_message_survive_a_restart()
    {
        Assert.Null(NewStore().SetMaintenance(true, "  Back at 09:00.  "));

        var reloaded = NewStore();
        Assert.True(reloaded.MaintenanceMode);
        Assert.Equal("Back at 09:00.", reloaded.MaintenanceMessage); // trimmed on the way in
        // Maintenance blocks CREATION for every game, without touching their availability.
        Assert.False(reloaded.CanCreateLobby("tictactoe"));
        Assert.True(reloaded.IsListed("tictactoe"));
    }

    [Fact]
    public void A_blank_maintenance_message_is_stored_as_null_not_as_whitespace()
    {
        var store = NewStore();
        store.SetMaintenance(true, "   ");
        Assert.Null(store.MaintenanceMessage);
    }

    [Fact]
    public void A_staged_game_is_hidden_but_still_startable()
    {
        var store = NewStore();
        store.SetAvailability("tictactoe", GameAvailability.Staged);
        // The whole point of the state: off the grid, but its direct link still works. If these two ever
        // agree, "staged" has collapsed into "disabled".
        Assert.False(store.IsListed("tictactoe"));
        Assert.True(store.CanCreateLobby("tictactoe"));
    }

    [Fact]
    public void Game_ids_are_matched_case_insensitively_like_the_catalog()
    {
        var store = NewStore();
        store.SetAvailability("TicTacToe", GameAvailability.Disabled);
        // GameCatalog keys its dictionary OrdinalIgnoreCase. If this store were stricter, an override
        // whose casing differed from the manifest would silently never apply.
        Assert.Equal(GameAvailability.Disabled, store.GetAvailability("tictactoe"));
        Assert.Equal(GameAvailability.Disabled, NewStore().GetAvailability("TICTACTOE"));
    }

    [Fact]
    public void Setting_a_game_back_to_available_removes_its_row_rather_than_recording_the_default()
    {
        var store = NewStore();
        store.SetAvailability("tictactoe", GameAvailability.Disabled);
        Assert.Single(store.Overrides);

        store.SetAvailability("tictactoe", GameAvailability.Available);
        // Otherwise the file grows a row per game ever touched, and "no override" and "explicitly
        // available" become two spellings of one state.
        Assert.Empty(store.Overrides);
        Assert.Empty(NewStore().Overrides);
    }

    [Fact]
    public void An_override_for_a_game_that_is_not_installed_is_preserved_across_saves()
    {
        var store = NewStore();
        store.SetAvailability("temporarily-missing", GameAvailability.Disabled);
        store.SetAvailability("another-game", GameAvailability.Staged);

        // A game whose files are briefly absent (a .kbg mid-copy, a mount that hasn't come up) must not
        // come back ENABLED just because an unrelated save happened while it was missing.
        var reloaded = NewStore();
        Assert.Equal(GameAvailability.Disabled, reloaded.GetAvailability("temporarily-missing"));
        Assert.Equal(GameAvailability.Staged, reloaded.GetAvailability("another-game"));
    }

    [Fact]
    public void A_corrupt_settings_file_falls_back_to_defaults_and_reports_why()
    {
        File.WriteAllText(_settingsPath, "{ this is not json");

        var store = NewStore();
        Assert.False(store.MaintenanceMode);
        Assert.Empty(store.Overrides);
        // Non-fatal, but never silent: from the outside "policy lost" and "policy ignored" look identical,
        // and this string is what DeploymentDiagnostics surfaces on the dashboard.
        Assert.NotNull(store.LoadError);
        Assert.Contains(_settingsPath, store.LoadError);
    }

    [Fact]
    public void A_successful_save_clears_an_earlier_read_failure()
    {
        File.WriteAllText(_settingsPath, "not json at all");
        var store = NewStore();
        Assert.NotNull(store.LoadError);

        Assert.Null(store.SetMaintenance(true, null));
        Assert.Null(store.LoadError);
        Assert.True(NewStore().MaintenanceMode); // and the file is valid again
    }

    [Fact]
    public void A_json_null_settings_file_falls_back_to_defaults()
    {
        File.WriteAllText(_settingsPath, "null");
        var store = NewStore();
        Assert.False(store.MaintenanceMode);
        Assert.NotNull(store.LoadError);
    }

    [Fact]
    public void Availability_is_written_as_a_camelCase_string_and_read_back_case_insensitively()
    {
        NewStore().SetAvailability("tictactoe", GameAvailability.Staged);

        // The file is meant to be readable and hand-editable, and the API reports these values lowercase —
        // the two must agree or an operator editing the file guesses wrong.
        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"staged\"", json);

        // A hand-edited (or older, PascalCase) value still loads.
        File.WriteAllText(_settingsPath, """{ "maintenanceMode": false, "games": { "tictactoe": "Disabled" } }""");
        Assert.Equal(GameAvailability.Disabled, NewStore().GetAvailability("tictactoe"));
    }

    [Fact]
    public void An_unknown_key_in_the_file_does_not_cost_the_other_overrides()
    {
        File.WriteAllText(_settingsPath, """
            {
              "maintenanceMode": true,
              "someFutureKey": 42,
              "games": { "keep-me": "disabled", "": "disabled" }
            }
            """);

        var store = NewStore();
        Assert.Null(store.LoadError);
        Assert.True(store.MaintenanceMode);
        Assert.Equal(GameAvailability.Disabled, store.GetAvailability("keep-me"));
        Assert.Single(store.Overrides); // the blank id is dropped, not fatal
    }

    [Fact]
    public void The_settings_file_defaults_to_sitting_beside_the_admin_password()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = _secretPath,
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        var store = new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);

        // That directory is already required to be writable and, in a container, on a persisted volume
        // outside the image — exactly what this file needs, so it defaults there rather than to the CWD.
        Assert.Equal(Path.GetDirectoryName(_secretPath), Path.GetDirectoryName(store.FilePath));
    }

    [Fact]
    public void A_change_that_cannot_be_written_is_still_applied_and_says_so()
    {
        // A directory where the file should be: every write attempt fails, but nothing about that should
        // stop an operator disabling a game right now.
        var blocked = Path.Combine(_directory, "blocked.json");
        Directory.CreateDirectory(blocked);

        var store = NewStore(blocked);
        var warning = store.SetAvailability("tictactoe", GameAvailability.Disabled);

        Assert.Equal(GameAvailability.Disabled, store.GetAvailability("tictactoe"));
        Assert.NotNull(warning);
        Assert.Contains("restart", warning);
    }
}
