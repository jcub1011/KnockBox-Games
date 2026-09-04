using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Blobs;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Networking;
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

    // ── Runtime limit overrides (§2.2) ─────────────────────────────────────────

    [Fact]
    public void A_fresh_server_has_no_limit_overrides()
    {
        Assert.True(NewStore().Limits.IsEmpty);
    }

    [Fact]
    public void Limit_overrides_survive_a_restart()
    {
        Assert.Null(NewStore().SetLimits(new OperatorLimits(
            ControlMessagesPerSecond: 2, MaxLobbies: 40, MaxLobbiesPerGame: 8)));

        var reloaded = NewStore().Limits;
        Assert.Equal(2, reloaded.ControlMessagesPerSecond);
        Assert.Equal(40, reloaded.MaxLobbies);
        Assert.Equal(8, reloaded.MaxLobbiesPerGame);
        // Everything not set stays null — the file records what was changed, not a snapshot of all ten.
        Assert.Null(reloaded.GameMessagesPerSecond);
        Assert.Null(reloaded.MaxConnectionsPerIp);
    }

    [Fact]
    public void Reverting_every_limit_removes_the_object_rather_than_recording_nulls()
    {
        var store = NewStore();
        store.SetLimits(new OperatorLimits(MaxLobbies: 40));
        Assert.False(store.Limits.IsEmpty);

        store.SetLimits(OperatorLimits.None);
        Assert.True(store.Limits.IsEmpty);
        Assert.True(NewStore().Limits.IsEmpty);
        // Same reasoning as availability: no override and "explicitly the configured value" must not
        // become two spellings of one state. The key itself stays (nulls are written, as they are for
        // maintenanceMessage) — what must not survive is an object full of nulls that reads like policy.
        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"limits\": null", json);
        Assert.DoesNotContain("maxLobbies", json);
    }

    [Fact]
    public void The_saved_limits_object_contains_only_settable_fields()
    {
        NewStore().SetLimits(new OperatorLimits(MaxLobbies: 40));

        // The record IS the persisted shape, and the file is meant to be hand-edited. A computed property
        // serialized alongside the real ones reads like a field an operator could set.
        var json = File.ReadAllText(_settingsPath);
        Assert.DoesNotContain("isEmpty", json);
        Assert.Contains("\"maxLobbies\": 40", json);
    }

    [Fact]
    public void Overrides_are_laid_over_configuration_leaving_the_rest_alone()
    {
        var configured = ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());
        var merged = new OperatorLimits(ControlMessagesPerSecond: 1, MaxLobbies: 3).ApplyTo(configured);

        Assert.Equal(1, merged.ControlMessagesPerSecond);
        Assert.Equal(3, merged.MaxLobbies);
        Assert.Equal(configured.GameMessagesPerSecond, merged.GameMessagesPerSecond);
        Assert.Equal(configured.HandshakeTimeout, merged.HandshakeTimeout);
    }

    [Theory]
    [InlineData(-1, null, "maxLobbies")]
    [InlineData(null, -5, "maxConnectionsPerIp")]
    public void Out_of_range_overrides_are_refused_naming_the_field(int? maxLobbies, int? maxPerIp, string expected)
    {
        var configured = ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());
        var error = new OperatorLimits(MaxLobbies: maxLobbies, MaxConnectionsPerIp: maxPerIp)
            .Validate(configured);
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_burst_below_one_against_a_live_rate_is_refused_as_a_self_inflicted_outage()
    {
        var configured = ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());

        // Only the burst is set, so the rate comes from configuration (5/s) — which is exactly why this is
        // judged on the MERGED limits. A zero burst there refuses every lobby operation, forever, for
        // everyone, and the only way back is hand-editing this file.
        var error = new OperatorLimits(ControlMessagesBurst: 0).Validate(configured);
        Assert.NotNull(error);
        Assert.Contains("controlMessagesBurst", error);

        // Turning the whole limit off is a legitimate choice, and stays one.
        Assert.Null(new OperatorLimits(ControlMessagesPerSecond: 0, ControlMessagesBurst: 0).Validate(configured));
    }

    // ── Server-authority overrides ─────────────────────────────────────────────
    // The concurrent-lobby cap and the parsed-module idle window. Stored as their OWN object rather than
    // inside "limits", because that key means "ServerLimits overrides" to anyone hand-editing the file —
    // and the two lobby caps it would collide with count different things.

    [Fact]
    public void A_fresh_server_has_no_authority_overrides()
    {
        Assert.True(NewStore().AuthorityLimits.IsEmpty);
    }

    [Fact]
    public void Authority_overrides_survive_a_restart()
    {
        Assert.Null(NewStore().SetAuthorityLimits(new OperatorAuthorityOptions(
            MaxLobbies: 25, ModuleCacheIdleMinutes: 5)));

        var reloaded = NewStore().AuthorityLimits;
        Assert.Equal(25, reloaded.MaxLobbies);
        Assert.Equal(5, reloaded.ModuleCacheIdleMinutes);
    }

    [Fact]
    public void Authority_overrides_and_limit_overrides_are_two_objects_in_the_file_not_one()
    {
        var store = NewStore();
        store.SetLimits(new OperatorLimits(MaxLobbies: 40));
        store.SetAuthorityLimits(new OperatorAuthorityOptions(MaxLobbies: 4));

        // Both are called "maxLobbies" inside their own object and they are DIFFERENT caps: 40 lobbies
        // platform-wide, 4 of them running server-side logic. Folding the authority pair into "limits"
        // would make one of those two numbers unreachable, so the split is pinned rather than commented.
        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"limits\"", json);
        Assert.Contains("\"authority\"", json);
        Assert.Contains("\"maxLobbies\": 40", json);
        Assert.Contains("\"maxLobbies\": 4,", json);

        // And a restart still tells them apart.
        var reloaded = NewStore();
        Assert.Equal(40, reloaded.Limits.MaxLobbies);
        Assert.Equal(4, reloaded.AuthorityLimits.MaxLobbies);
    }

    [Fact]
    public void Blob_overrides_are_a_third_object_and_survive_a_restart()
    {
        var store = NewStore();
        store.SetLimits(new OperatorLimits(MaxLobbies: 40));
        store.SetAuthorityLimits(new OperatorAuthorityOptions(MaxLobbies: 4));
        store.SetBlobLimits(new OperatorBlobOptions(TotalQuotaBytes: 5_368_709_120));

        // Three objects, not one, for the reason the pair above are two: "limits" means ServerLimits
        // overrides to anyone hand-editing this file, and a blob quota is enforced on an upload rather
        // than on a frame.
        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"blobs\"", json);
        Assert.Contains("\"totalQuotaBytes\": 5368709120", json);

        // Save() builds AdminSettings POSITIONALLY on purpose, so that adding a member and forgetting to
        // write it is a compile error rather than a field that silently never persists. This is the
        // assertion that would have caught it if the positional call had been a named one.
        var reloaded = NewStore();
        Assert.Equal(40, reloaded.Limits.MaxLobbies);
        Assert.Equal(4, reloaded.AuthorityLimits.MaxLobbies);
        Assert.Equal(5_368_709_120, reloaded.BlobLimits.TotalQuotaBytes);
    }

    [Fact]
    public void A_per_game_blob_quota_is_recorded_by_absence_and_survives_a_restart()
    {
        var store = NewStore();
        Assert.Null(store.SetBlobQuota("dnd-mapper", 4_294_967_296));
        Assert.Null(store.SetBlobQuota("sound-board", -1));

        var reloaded = NewStore();
        Assert.Equal(4_294_967_296, reloaded.BlobQuotas["dnd-mapper"]);
        // Negative is kept: BlobOptions.LobbyQuotaFor reads it as "no per-lobby cap for this game", which
        // is a policy an operator may genuinely want, so dropping it as junk would silently reinstate the
        // cap they removed.
        Assert.Equal(-1, reloaded.BlobQuotas["sound-board"]);
        // Game ids are OrdinalIgnoreCase everywhere else in this server, so a lookup that missed on
        // casing would present as an override the portal shows but the store never applies.
        Assert.True(reloaded.BlobQuotas.ContainsKey("DND-Mapper"));

        // Cleared by REMOVING the row, not by writing the server default into it — otherwise "no
        // override" becomes two things, and raising the default later would not reach this game.
        Assert.Null(store.SetBlobQuota("dnd-mapper", null));
        Assert.False(store.BlobQuotas.ContainsKey("dnd-mapper"));
        Assert.DoesNotContain("dnd-mapper", File.ReadAllText(_settingsPath));

        // And the whole map goes when its last row does, rather than persisting as an empty object.
        store.SetBlobQuota("sound-board", null);
        Assert.Contains("\"blobQuotas\": null", File.ReadAllText(_settingsPath));
    }

    [Fact]
    public void A_hand_edited_per_game_blob_quota_of_zero_is_dropped_rather_than_honoured()
    {
        // A quota field somebody typed 0 into reads as "I am clearing this", not as this server's usual
        // "no limit" — and honouring it would be the one place a 0 quietly did the opposite of what an
        // operator meant. The admin route refuses 0 outright; this is the hand-edited file's version of
        // the same rule, and it drops the row instead of rejecting the whole file.
        File.WriteAllText(_settingsPath, """
            { "blobQuotas": { "dnd-mapper": 0, "sound-board": 100 } }
            """);

        var store = NewStore();
        Assert.False(store.BlobQuotas.ContainsKey("dnd-mapper"));
        Assert.Equal(100, store.BlobQuotas["sound-board"]);
    }

    [Fact]
    public void OperatorBlobOptions_Validate_enforces_1_TiB_cap()
    {
        const long maxBytes = 1024L * 1024 * 1024 * 1024;
        var valid = new OperatorBlobOptions(MaxBlobBytes: maxBytes, LobbyQuotaBytes: maxBytes, TotalQuotaBytes: maxBytes);
        Assert.Null(valid.Validate());

        var overflowMaxBlob = new OperatorBlobOptions(MaxBlobBytes: maxBytes + 1);
        Assert.NotNull(overflowMaxBlob.Validate());

        var overflowLobby = new OperatorBlobOptions(LobbyQuotaBytes: maxBytes + 1);
        Assert.NotNull(overflowLobby.Validate());

        var overflowTotal = new OperatorBlobOptions(TotalQuotaBytes: maxBytes + 1);
        Assert.NotNull(overflowTotal.Validate());
    }

    [Fact]
    public void Reverting_every_authority_knob_removes_the_object_rather_than_recording_nulls()
    {
        var store = NewStore();
        store.SetAuthorityLimits(new OperatorAuthorityOptions(ModuleCacheIdleMinutes: 5));
        Assert.False(store.AuthorityLimits.IsEmpty);

        store.SetAuthorityLimits(OperatorAuthorityOptions.None);
        Assert.True(store.AuthorityLimits.IsEmpty);
        Assert.True(NewStore().AuthorityLimits.IsEmpty);

        var json = File.ReadAllText(_settingsPath);
        Assert.Contains("\"authority\": null", json);
        Assert.DoesNotContain("moduleCacheIdleMinutes", json);
        // The record IS the persisted shape, so a computed property must not read like a settable field.
        Assert.DoesNotContain("isEmpty", json);
    }

    [Fact]
    public void Authority_overrides_are_laid_over_configuration_leaving_the_rest_alone()
    {
        var configured = AuthorityOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var merged = new OperatorAuthorityOptions(MaxLobbies: 12).ApplyTo(configured);

        Assert.Equal(12, merged.MaxLobbies);
        // Untouched, including the per-engine constraints, which are deliberately not editable at all.
        Assert.Equal(configured.ModuleCacheIdle, merged.ModuleCacheIdle);
        Assert.Equal(configured.CallTimeout, merged.CallTimeout);
        Assert.Equal(configured.MaxMemoryBytes, merged.MaxMemoryBytes);
    }

    [Fact]
    public void The_shipped_authority_defaults_are_an_uncapped_lobby_count_and_a_thirty_minute_cache()
    {
        // Both defaults are load-bearing and neither is obvious. Unlimited: a refusal nobody configured is
        // worse than letting the host (in Docker, mem_limit) be the bound. Thirty minutes: a window of 0
        // would ship the eviction switched off, which is a feature nobody gets.
        var configured = AuthorityOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(0, configured.MaxLobbies);
        Assert.Equal(TimeSpan.FromMinutes(30), configured.ModuleCacheIdle);
    }

    [Theory]
    [InlineData(-1, null, "authorityMaxLobbies")]
    [InlineData(null, -5, "authorityModuleCacheIdleMinutes")]
    [InlineData(null, 10_081, "authorityModuleCacheIdleMinutes")]
    public void Out_of_range_authority_overrides_are_refused_naming_the_field(
        int? maxLobbies, int? idleMinutes, string expected)
    {
        var error = new OperatorAuthorityOptions(maxLobbies, idleMinutes).Validate();
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Zero_is_legal_for_both_authority_knobs_and_means_two_different_things()
    {
        // Unlimited lobbies, and keep the parsed module for the process lifetime. Neither is a mistake,
        // and a validator that rejected 0 would make the shipped default unreachable from the portal.
        Assert.Null(new OperatorAuthorityOptions(MaxLobbies: 0, ModuleCacheIdleMinutes: 0).Validate());

        var configured = AuthorityOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var merged = new OperatorAuthorityOptions(MaxLobbies: 0, ModuleCacheIdleMinutes: 0).ApplyTo(configured);
        Assert.Equal(0, merged.MaxLobbies);
        Assert.Equal(TimeSpan.Zero, merged.ModuleCacheIdle);
    }

    // ── Banned room codes (§2.4) ───────────────────────────────────────────────

    [Fact]
    public void A_fresh_server_blocks_no_room_codes()
    {
        Assert.True(NewStore().RoomCodes.IsEmpty);
    }

    [Fact]
    public void The_room_code_blocklist_survives_a_restart_compiled()
    {
        Assert.Null(NewStore().SetRoomCodes(RoomCodeFilter.Compile(["XQ"], ["Q7*"])));

        var reloaded = NewStore().RoomCodes;
        Assert.Equal(["XQ"], reloaded.Words);
        Assert.Equal(["Q7*"], reloaded.Patterns);
        // Compiled, not just stored: the lobby-create path reads this per generated code.
        Assert.True(reloaded.IsBlocked("KXQ2"));
        Assert.True(reloaded.IsBlocked("Q7ZZ"));
        Assert.False(reloaded.IsBlocked("ABCD"));
    }

    [Fact]
    public void An_empty_blocklist_removes_the_object_rather_than_recording_empty_lists()
    {
        var store = NewStore();
        store.SetRoomCodes(RoomCodeFilter.Compile(["XQ"], null));
        store.SetRoomCodes(RoomCodeFilter.Empty);

        Assert.True(NewStore().RoomCodes.IsEmpty);
        Assert.Contains("\"roomCodes\": null", File.ReadAllText(_settingsPath));
    }

    [Fact]
    public void A_hand_edited_blocklist_keeps_its_usable_entries()
    {
        File.WriteAllText(_settingsPath, """
        {
          "roomCodes": { "words": ["XQ", "TOOLONG", ""], "patterns": ["A?", "!!"] }
        }
        """);

        var store = NewStore();
        Assert.Null(store.LoadError);
        Assert.Equal(["XQ"], store.RoomCodes.Words);
        Assert.Equal(["A?"], store.RoomCodes.Patterns);
    }

    // ── Update schedule ────────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_server_records_no_update_schedule()
    {
        // Null, not the default object: "never chose" and "chose the same thing the default happens to
        // be" are different facts, and only the second should survive a change of default.
        Assert.Null(NewStore().UpdateSchedule);
    }

    [Fact]
    public void An_update_schedule_survives_a_restart()
    {
        Assert.Null(NewStore().SetUpdateSchedule(
            new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Tuesday, 14)));

        var reloaded = NewStore().UpdateSchedule;
        Assert.NotNull(reloaded);
        Assert.Equal(UpdateCadence.Weekly, reloaded.Cadence);
        Assert.Equal(DayOfWeek.Tuesday, reloaded.DayOfWeek);
        Assert.Equal(14, reloaded.HourUtc);
    }

    [Fact]
    public void Clearing_the_schedule_removes_the_object_rather_than_recording_the_default()
    {
        var store = NewStore();
        store.SetUpdateSchedule(new UpdateSchedule(UpdateCadence.Hourly));
        store.SetUpdateSchedule(null);

        Assert.Null(NewStore().UpdateSchedule);
        Assert.Contains("\"schedule\": null", File.ReadAllText(_settingsPath));
    }

    [Fact]
    public void The_schedule_is_written_as_names_an_operator_can_hand_edit()
    {
        NewStore().SetUpdateSchedule(new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Friday, 6));

        var written = File.ReadAllText(_settingsPath);
        Assert.Contains("\"cadence\": \"weekly\"", written);
        Assert.Contains("\"dayOfWeek\": \"friday\"", written);
    }

    [Fact]
    public void A_hand_edited_schedule_with_an_impossible_hour_falls_back_to_the_default()
    {
        // Normalized on the way IN, so the timer arithmetic downstream never sees an hour of 99.
        File.WriteAllText(_settingsPath, """
        {
          "schedule": { "cadence": "daily", "dayOfWeek": "sunday", "hourUtc": 99 }
        }
        """);

        var store = NewStore();
        Assert.Null(store.LoadError);
        Assert.Equal(UpdateSchedule.Default.HourUtc, store.UpdateSchedule!.HourUtc);
    }

    // ── Player announcement (§4.1) ─────────────────────────────────────────────

    [Fact]
    public void An_announcement_survives_a_restart()
    {
        var posted = new PlatformAnnouncement("a1", "Maintenance at 09:00.",
            new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero), "warning", "word-rush");
        Assert.Null(NewStore().SetAnnouncement(posted));

        // Persisted for the same reason maintenance mode is: a notice about a window that vanished on the
        // next deploy would be worse than not posting one.
        var reloaded = NewStore().Announcement;
        Assert.NotNull(reloaded);
        Assert.Equal(("a1", "Maintenance at 09:00.", "warning", "word-rush"),
            (reloaded.Id, reloaded.Text, reloaded.Severity, reloaded.GameId));
    }

    [Fact]
    public void Clearing_an_announcement_removes_it()
    {
        var store = NewStore();
        store.SetAnnouncement(new PlatformAnnouncement("a1", "Hi", DateTimeOffset.UnixEpoch));
        store.SetAnnouncement(null);

        Assert.Null(store.Announcement);
        Assert.Null(NewStore().Announcement);
    }

    [Fact]
    public void A_hand_edited_announcement_is_completed_rather_than_dropped()
    {
        // No id, no timestamp, an unknown severity. Every one of those is fixable, and an operator who
        // edited the file by hand meant to say something.
        File.WriteAllText(_settingsPath, """
        { "announcement": { "text": "  Server moving on Friday.  ", "severity": "URGENT" } }
        """);

        var announcement = NewStore().Announcement;
        Assert.NotNull(announcement);
        Assert.Equal("Server moving on Friday.", announcement.Text);
        Assert.NotEqual("", announcement.Id);            // a dismissal needs something to key on
        Assert.NotEqual(default, announcement.PostedAt);
        // An unrecognised severity becomes a class name in the shell; it reads as info rather than being
        // trusted through.
        Assert.Equal("info", announcement.Severity);
    }

    [Fact]
    public void An_announcement_with_no_text_is_not_an_announcement()
    {
        File.WriteAllText(_settingsPath, """{ "announcement": { "id": "a1", "text": "   " } }""");
        Assert.Null(NewStore().Announcement);
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

    [Fact]
    public void A_null_row_in_a_hand_edited_list_is_dropped_rather_than_crashing_the_server()
    {
        // `[null]` is valid JSON and nullable annotations are erased at runtime, so this deserializes to a
        // list holding a null. Dereferencing it threw an NRE — which is neither IOException nor
        // JsonException, so it escaped Load()'s catch, out of the constructor, and stopped the host
        // booting. A hand-edited typo must cost the operator that row, not their server.
        File.WriteAllText(_settingsPath, """
            {
              "maintenanceMode": true,
              "sources": [null, { "id": "mirror", "name": "Mirror",
                                  "catalogUrl": "https://example.com/CATALOG.json",
                                  "downloadBaseUrl": "https://example.com" }],
              "webhooks": [null, { "id": "ops", "name": "Ops", "url": "https://example.com/hook" }]
            }
            """);

        var store = NewStore();

        // The rest of the file still loaded: the bad row is dropped on its own, not with its neighbours.
        Assert.True(store.MaintenanceMode);
        Assert.Equal("mirror", Assert.Single(store.Sources).Id);
        Assert.Equal("ops", Assert.Single(store.Webhooks).Id);
    }

    [Fact]
    public void The_official_marketplace_can_be_switched_off_and_stays_off_across_a_restart()
    {
        // Two API error messages and the operator guide all say the built-in source is "disable-able but
        // never removable". Its enabled flag was hard-coded true, so that was advice with nothing behind
        // it. It has no row in `sources` (it is built from configuration), hence its own key.
        var store = NewStore();
        Assert.True(store.OfficialSourceEnabled);

        Assert.True(store.SetSourceEnabled(MarketplaceSourceRegistry.OfficialId, false, out var warning));
        Assert.Null(warning);
        Assert.False(store.OfficialSourceEnabled);
        Assert.False(NewStore().OfficialSourceEnabled);   // survives the restart

        Assert.True(store.SetSourceEnabled(MarketplaceSourceRegistry.OfficialId, true, out _));
        Assert.True(NewStore().OfficialSourceEnabled);
    }

    [Fact]
    public void Disabling_a_registered_source_keeps_its_configuration()
    {
        // Disable, not remove: the point is that the URLs survive so it can be switched back on.
        var store = NewStore();
        store.UpsertSource(new RegisteredMarketplace(
            "mirror", "Mirror", "https://example.com/CATALOG.json", "https://example.com"));

        Assert.True(store.SetSourceEnabled("mirror", false, out _));

        var source = Assert.Single(NewStore().Sources);
        Assert.False(source.Enabled);
        Assert.Equal("https://example.com/CATALOG.json", source.CatalogUrl);

        Assert.False(store.SetSourceEnabled("nosuch", false, out _));
    }
}
