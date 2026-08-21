using System.Text.Json;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Webhooks;

namespace KnockBox.Server.Admin;

/// <summary>
/// Loads and persists <see cref="AdminSettings"/>, and answers the policy questions the lobby-create
/// path asks on every request.
///
/// Two properties matter here. First, <b>reads are lock-free</b>: an immutable snapshot is swapped
/// atomically, the same discipline <c>GameCatalog</c> uses for its game dictionary, because
/// <see cref="GetAvailability"/> is called while a player is creating a lobby and must never queue
/// behind an operator's toggle. Second, <b>a change takes effect in memory even if it cannot be
/// written</b>: the setters return the persistence error rather than rejecting the change, so an
/// unwritable settings file degrades to "applied until restart" instead of an operator's disable
/// silently doing nothing.
/// </summary>
public sealed class AdminSettingsStore : IPlatformPolicy
{
    /// <summary>The file's name when <c>KnockBox:AdminSettingsPath</c> isn't set. It lands beside the
    /// admin password file, which is already required to be writable and, in a container, on a
    /// persisted volume outside the image — exactly the properties this file needs.</summary>
    private const string DefaultFileName = "admin-settings.json";

    // Game ids are compared OrdinalIgnoreCase to match GameCatalog's dictionary. Anything stricter
    // would let an override keyed with different casing than the manifest silently never apply.
    private static readonly StringComparer GameIdComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ILogger<AdminSettingsStore> _logger;
    // Serializes writers (read-modify-write of the snapshot, plus the file write). Readers never take it.
    private readonly Lock _writeGate = new();

    private volatile State _state;

    public AdminSettingsStore(IConfiguration config, AdminAuthService auth, ILogger<AdminSettingsStore> logger)
    {
        _logger = logger;

        var configured = config["KnockBox:AdminSettingsPath"];
        FilePath = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Path.GetDirectoryName(auth.SecretFilePath) ?? AppContext.BaseDirectory, DefaultFileName);

        _state = Load();
    }

    /// <summary>Where the settings file lives. Surfaced so an error message can name it.</summary>
    public string FilePath { get; }

    /// <summary>Why the settings file could not be read, or null when it loaded (or simply doesn't
    /// exist yet). Reported through <c>DeploymentDiagnostics</c> so an operator learns that their saved
    /// policy is not in effect, instead of discovering it when a disabled game reappears.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Whether new lobby creation is blocked platform-wide. Running sessions are unaffected.</summary>
    public bool MaintenanceMode => _state.MaintenanceMode;

    /// <summary>Operator-supplied reason shown to a player whose lobby creation was refused.</summary>
    public string? MaintenanceMessage => _state.MaintenanceMessage;

    /// <summary>This game's availability, defaulting to <see cref="GameAvailability.Available"/> for
    /// any game with no override recorded.</summary>
    public GameAvailability GetAvailability(string gameId) =>
        _state.Games.TryGetValue(gameId, out var state) ? state : GameAvailability.Available;

    /// <summary>Whether players may start a new lobby for this game right now — false in maintenance
    /// mode, and false for a disabled game. Staged games stay startable; see
    /// <see cref="GameAvailability.Staged"/>.</summary>
    public bool CanCreateLobby(string gameId) =>
        !MaintenanceMode && GetAvailability(gameId) != GameAvailability.Disabled;

    /// <summary>Whether this game appears in the catalog players browse.</summary>
    public bool IsListed(string gameId) => GetAvailability(gameId) == GameAvailability.Available;

    /// <summary>
    /// Always null: persisted policy has nothing specific to add beyond the generic refusal. The
    /// transient half of policy (<see cref="GameLifecycleGate"/>) is what answers this.
    /// </summary>
    public string? UnavailableReason(string gameId) => null;

    /// <summary>Every recorded override. Entries for games that aren't currently installed are kept
    /// deliberately — see <see cref="Save"/>.</summary>
    public IReadOnlyDictionary<string, GameAvailability> Overrides => _state.Games;

    /// <summary>
    /// Sets a game's availability. Returns null on success, or a message explaining why the change
    /// could not be persisted (it is in effect regardless, until the next restart).
    /// </summary>
    public string? SetAvailability(string gameId, GameAvailability state)
    {
        lock (_writeGate)
        {
            var games = new Dictionary<string, GameAvailability>(_state.Games, GameIdComparer);
            // Available is the default, so record it by removing the override rather than storing it.
            // Otherwise the file accumulates a row per game ever touched, and "no override" and
            // "explicitly available" become two ways to say one thing.
            if (state == GameAvailability.Available) games.Remove(gameId);
            else games[gameId] = state;

            _state = _state with { Games = games };
            _logger.LogInformation("Admin set game {GameId} availability to {State}.", gameId, state);
            return Save();
        }
    }

    /// <summary>Extra marketplaces the operator registered. The official one is not in here — it is built in.</summary>
    public IReadOnlyList<RegisteredMarketplace> Sources => _state.Sources;

    /// <summary>What the server may do on its own when a newer version of this game appears.</summary>
    public UpdatePolicy GetUpdatePolicy(string gameId) =>
        _state.Updates.TryGetValue(gameId, out var policy) ? policy : UpdatePolicy.Manual;

    /// <summary>Every game enrolled in automatic updates. Games on Manual are absent, not listed.</summary>
    public IReadOnlyDictionary<string, UpdatePolicy> UpdatePolicies => _state.Updates;

    /// <summary>
    /// Sets a game's update policy. Returns null on success, or a message explaining why it could not be
    /// persisted (it is in effect regardless, until the next restart).
    /// </summary>
    public string? SetUpdatePolicy(string gameId, UpdatePolicy policy)
    {
        lock (_writeGate)
        {
            var updates = new Dictionary<string, UpdatePolicy>(_state.Updates, GameIdComparer);
            if (policy == UpdatePolicy.Manual) updates.Remove(gameId);
            else updates[gameId] = policy;

            _state = _state with { Updates = updates };
            _logger.LogInformation("Admin set game {GameId} update policy to {Policy}.", gameId, policy);
            return Save();
        }
    }

    /// <summary>
    /// Adds a marketplace, or replaces the one with the same id. Returns null on success, or a message
    /// explaining why it could not be persisted (it is in effect regardless, until the next restart).
    /// </summary>
    public string? UpsertSource(RegisteredMarketplace source)
    {
        lock (_writeGate)
        {
            var sources = _state.Sources
                .Where(s => !string.Equals(s.Id, source.Id, StringComparison.OrdinalIgnoreCase))
                .Append(source)
                .ToList();
            _state = _state with { Sources = sources };
            _logger.LogInformation("Admin registered marketplace {SourceId} ({Url}).",
                source.Id, source.CatalogUrl);
            return Save();
        }
    }

    /// <summary>
    /// Whether the built-in official marketplace is switched on. Extra sources carry their own flag on
    /// their row; the official one has no row to carry it, so it lives here.
    /// </summary>
    public bool OfficialSourceEnabled => !_state.OfficialSourceDisabled;

    /// <summary>
    /// Enables or disables a marketplace, official or otherwise, without losing its configuration.
    /// Returns false when there is no such source; otherwise null on success, or a message explaining why
    /// it could not be persisted (it is in effect regardless, until the next restart).
    /// </summary>
    /// <remarks>
    /// The official source is handled here rather than being refused, because the portal and two API error
    /// messages already tell operators to disable it instead of removing it — and until this existed, that
    /// was advice with nothing behind it.
    /// </remarks>
    public bool SetSourceEnabled(string id, bool enabled, out string? warning)
    {
        lock (_writeGate)
        {
            if (string.Equals(id, MarketplaceSourceRegistry.OfficialId, StringComparison.OrdinalIgnoreCase))
            {
                _state = _state with { OfficialSourceDisabled = !enabled };
                _logger.LogInformation("Admin {Action} the official marketplace.",
                    enabled ? "enabled" : "disabled");
                warning = Save();
                return true;
            }

            var index = -1;
            for (var i = 0; i < _state.Sources.Count; i++)
                if (string.Equals(_state.Sources[i].Id, id, StringComparison.OrdinalIgnoreCase)) index = i;

            if (index < 0)
            {
                warning = null;
                return false;
            }

            var sources = _state.Sources.ToList();
            sources[index] = sources[index] with { Enabled = enabled };
            _state = _state with { Sources = sources };
            _logger.LogInformation("Admin {Action} marketplace {SourceId}.",
                enabled ? "enabled" : "disabled", id);
            warning = Save();
            return true;
        }
    }

    /// <summary>Removes a registered marketplace. False when there was no such id.</summary>
    public bool RemoveSource(string id, out string? warning)
    {
        lock (_writeGate)
        {
            var sources = _state.Sources
                .Where(s => !string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sources.Count == _state.Sources.Count)
            {
                warning = null;
                return false;
            }

            _state = _state with { Sources = sources };
            _logger.LogInformation("Admin removed marketplace {SourceId}.", id);
            warning = Save();
            return true;
        }
    }

    /// <summary>
    /// The operator's overrides of the runtime-editable limits, or <see cref="OperatorLimits.None"/> when
    /// they have never been touched. Held as data: this class stores policy, and
    /// <see cref="LimitsProvider"/> is what lays it over the configured baseline.
    /// </summary>
    public OperatorLimits Limits => _state.Limits;

    /// <summary>
    /// Records new limit overrides. Returns null on success, or a message explaining why they could not
    /// be persisted (they are in effect regardless, until the next restart).
    /// </summary>
    /// <remarks>
    /// The caller publishes them to <see cref="LimitsProvider"/> — deliberately not done here, because
    /// this class is read on the lobby-create path and has no business holding a reference to the
    /// networking layer. Validation is the caller's job too, for the same reason: the merged answer
    /// depends on the configured baseline, which lives with the provider.
    /// </remarks>
    public string? SetLimits(OperatorLimits limits)
    {
        lock (_writeGate)
        {
            // Empty is the default, recorded by absence — the same trick availability and update policy
            // use, so reverting every field leaves no trace rather than a row full of nulls.
            _state = _state with { Limits = limits.IsEmpty ? OperatorLimits.None : limits };
            _logger.LogInformation("Admin changed the runtime limit overrides.");
            return Save();
        }
    }

    /// <summary>
    /// The operator's chosen update schedule, or null when they have never set one — in which case the
    /// configured default (<c>KnockBox:MarketplaceUpdate*</c>) stands. Same record-by-absence shape as
    /// <see cref="Limits"/>, and for the same reason: "I never chose" and "I chose the same thing the
    /// default happens to be" are different facts, and only the second should survive a change of default.
    /// </summary>
    public UpdateSchedule? UpdateSchedule => _state.Schedule;

    /// <summary>
    /// Records a new update schedule, or null to fall back to the configured default. Returns null on
    /// success, or a message explaining why it could not be persisted (it is in effect regardless, until
    /// the next restart).
    /// </summary>
    /// <remarks>
    /// Re-arming the timer is the caller's job, not this one's — the same split as <see cref="SetLimits"/>
    /// and <see cref="LimitsProvider"/>. This class records policy; <see cref="UpdateScheduler"/> acts on it.
    /// </remarks>
    public string? SetUpdateSchedule(UpdateSchedule? schedule)
    {
        lock (_writeGate)
        {
            var next = schedule?.Normalize();
            _state = _state with { Schedule = next };
            _logger.LogInformation("Admin set the marketplace update schedule to {Schedule}.",
                next?.Describe() ?? "the configured default");
            return Save();
        }
    }

    /// <summary>
    /// The compiled room-code blocklist. Compiled once per change rather than per draw: the lobby-create
    /// path reads this on every code it generates.
    /// </summary>
    public RoomCodeFilter RoomCodes => _state.RoomCodes;

    /// <summary>The live player-facing announcement, or null when none is posted.</summary>
    public PlatformAnnouncement? Announcement => _state.Announcement;

    /// <summary>Registered outbound webhook endpoints, in registration order.</summary>
    public IReadOnlyList<WebhookEndpoint> Webhooks => _state.Webhooks;

    /// <summary>
    /// Adds a webhook endpoint, or replaces the one with the same id. Returns null on success, or a message
    /// explaining why it could not be persisted (it is in effect regardless, until the next restart).
    /// </summary>
    public string? UpsertWebhook(WebhookEndpoint endpoint)
    {
        lock (_writeGate)
        {
            var webhooks = _state.Webhooks
                .Where(w => !string.Equals(w.Id, endpoint.Id, StringComparison.OrdinalIgnoreCase))
                .Append(endpoint)
                .ToList();
            _state = _state with { Webhooks = webhooks };
            // The URL is not logged: an endpoint URL is a bearer credential — anyone holding a Discord or
            // Slack webhook URL can post to that channel — and the log file is read by more people than
            // the settings file is.
            _logger.LogInformation("Admin registered webhook {WebhookId} ({Events} event(s)).",
                endpoint.Id, endpoint.Events?.Count ?? 0);
            return Save();
        }
    }

    /// <summary>Removes a webhook endpoint. False when there was no such id.</summary>
    public bool RemoveWebhook(string id, out string? warning)
    {
        lock (_writeGate)
        {
            var webhooks = _state.Webhooks
                .Where(w => !string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (webhooks.Count == _state.Webhooks.Count)
            {
                warning = null;
                return false;
            }

            _state = _state with { Webhooks = webhooks };
            _logger.LogInformation("Admin removed webhook {WebhookId}.", id);
            warning = Save();
            return true;
        }
    }

    /// <summary>
    /// Posts an announcement, replacing any current one, or clears it with null. Returns null on success,
    /// or a message explaining why it could not be persisted (it is in effect regardless, until restart).
    /// </summary>
    /// <remarks>
    /// Telling the connected players is the CALLER's job, not this method's — the same division the
    /// maintenance toggle already follows. This class is read on the lobby-create path and must not hold a
    /// reference to the connection registry; and a change has to survive being un-broadcastable (nobody
    /// connected, a socket wedged) rather than being rolled back because a fan-out went badly.
    /// </remarks>
    public string? SetAnnouncement(PlatformAnnouncement? announcement)
    {
        lock (_writeGate)
        {
            _state = _state with { Announcement = announcement };
            if (announcement is null) _logger.LogInformation("Admin cleared the player announcement.");
            else
                _logger.LogInformation("Admin posted a {Severity} announcement{Scope}: {Text}",
                    announcement.Severity,
                    announcement.GameId is null ? " for the whole platform" : $" for '{announcement.GameId}'",
                    announcement.Text);
            return Save();
        }
    }

    /// <summary>
    /// Replaces the room-code blocklist. Returns null on success, or a message explaining why it could not
    /// be persisted (it is in effect regardless, until the next restart).
    /// </summary>
    /// <remarks>
    /// A full replacement rather than add/remove operations, because the portal edits the list as a whole
    /// and a two-call shape would let a failed second call leave a list nobody asked for. Validation of how
    /// MUCH it blocks belongs to the caller — the store's job is to record policy, not to have opinions
    /// about the code space.
    /// </remarks>
    public string? SetRoomCodes(RoomCodeFilter filter)
    {
        lock (_writeGate)
        {
            _state = _state with { RoomCodes = filter };
            _logger.LogInformation(
                "Admin set the room-code blocklist: {Words} word(s), {Patterns} pattern(s).",
                filter.Words.Count, filter.Patterns.Count);
            return Save();
        }
    }

    /// <summary>
    /// Turns global maintenance mode on or off. Returns null on success, or a message explaining why
    /// the change could not be persisted (it is in effect regardless, until the next restart).
    /// </summary>
    public string? SetMaintenance(bool enabled, string? message)
    {
        lock (_writeGate)
        {
            var trimmed = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
            _state = _state with { MaintenanceMode = enabled, MaintenanceMessage = trimmed };
            _logger.LogWarning("Admin turned global maintenance mode {State}.", enabled ? "ON" : "off");
            return Save();
        }
    }

    // ── Persistence ───────────────────────────────────────────────────────────
    private State Load()
    {
        if (!File.Exists(FilePath))
        {
            _logger.LogInformation("No admin settings file at {Path}; starting with platform defaults.", FilePath);
            return State.Default;
        }

        try
        {
            var json = File.ReadAllBytes(FilePath);
            var parsed = JsonSerializer.Deserialize(json, AdminSettingsJsonContext.Default.AdminSettings);
            if (parsed is null)
            {
                LoadError = $"'{FilePath}' contains JSON null; using platform defaults.";
                _logger.LogWarning("Admin settings file {Path} deserialized to null; using defaults.", FilePath);
                return State.Default;
            }

            var state = State.From(parsed);
            _logger.LogInformation(
                "Loaded admin settings from {Path}: maintenance {Maintenance}, {Overrides} game override(s).",
                FilePath, state.MaintenanceMode ? "ON" : "off", state.Games.Count);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Never fatal. A settings file we can't read must not stop the server booting — but the
            // operator has to be told, because from the outside "policy lost" looks like "policy ignored".
            LoadError = $"'{FilePath}' could not be read ({ex.Message}); using platform defaults, and " +
                        "saving from the portal will overwrite it.";
            _logger.LogError(ex, "Could not read admin settings from {Path}; using platform defaults.", FilePath);
            return State.Default;
        }
    }

    /// <summary>
    /// Writes the current state. Call under <see cref="_writeGate"/>. Returns null on success or the
    /// failure message.
    /// </summary>
    /// <remarks>
    /// Write-temp-then-move, so a crash or a full disk mid-write leaves the previous settings intact
    /// rather than a truncated file that then fails to parse on the next boot.
    /// <para>
    /// Overrides for games that aren't currently installed are written back untouched. A game whose
    /// files are briefly absent — a `.kbg` mid-copy, a mount that hasn't come up yet — must not come
    /// back <em>enabled</em> just because a save happened while it was missing.
    /// </para>
    /// </remarks>
    private string? Save()
    {
        // Positional on purpose — a named-argument version of this call would let a new field be added to
        // AdminSettings and quietly never be written, which is a data-loss bug that compiles.
        var settings = new AdminSettings(
            _state.MaintenanceMode, _state.MaintenanceMessage, _state.Games, _state.Sources, _state.Updates,
            _state.Limits.IsEmpty ? null : _state.Limits,
            _state.RoomCodes.IsEmpty
                ? null
                : new BannedRoomCodes(_state.RoomCodes.Words, _state.RoomCodes.Patterns),
            _state.Announcement,
            _state.Webhooks.Count == 0 ? null : _state.Webhooks,
            _state.OfficialSourceDisabled,
            _state.Schedule);
        var temp = FilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllBytes(temp,
                JsonSerializer.SerializeToUtf8Bytes(settings, AdminSettingsJsonContext.Default.AdminSettings));
            // Retried briefly, and synchronously: this runs under _writeGate, which is a
            // System.Threading.Lock, so awaiting here would not compile. That is also why the retry
            // budget is bounded so tightly — every other admin write is behind this lock.
            AtomicFile.MoveWithRetry(temp, FilePath);
            LoadError = null; // a successful write supersedes an earlier read failure
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not persist admin settings to {Path}.", FilePath);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            return $"The change is active now but could not be saved to '{FilePath}' ({ex.Message}), " +
                   "so it will be lost on restart.";
        }
    }

    // The immutable snapshot readers see. A record so a setter can produce the next one with `with`
    // and publish it in a single reference write.
    private sealed record State(
        bool MaintenanceMode,
        string? MaintenanceMessage,
        IReadOnlyDictionary<string, GameAvailability> Games,
        IReadOnlyList<RegisteredMarketplace> Sources,
        IReadOnlyDictionary<string, UpdatePolicy> Updates,
        OperatorLimits Limits,
        RoomCodeFilter RoomCodes,
        PlatformAnnouncement? Announcement,
        IReadOnlyList<WebhookEndpoint> Webhooks,
        bool OfficialSourceDisabled,
        UpdateSchedule? Schedule)
    {
        public static readonly State Default =
            new(false, null, new Dictionary<string, GameAvailability>(GameIdComparer), [],
                new Dictionary<string, UpdatePolicy>(GameIdComparer), OperatorLimits.None,
                RoomCodeFilter.Empty, null, [], false, null);

        public static State From(AdminSettings settings)
        {
            var updates = new Dictionary<string, UpdatePolicy>(GameIdComparer);
            foreach (var (id, policy) in settings.Updates ?? Default.Updates)
            {
                // Manual is the default, recorded by ABSENCE — the same trick the availability map uses
                // for Available, so the file doesn't accumulate a row per game ever looked at.
                if (string.IsNullOrWhiteSpace(id) || policy == UpdatePolicy.Manual) continue;
                updates[id] = policy;
            }

            var games = new Dictionary<string, GameAvailability>(GameIdComparer);
            foreach (var (id, state) in settings.Games ?? Default.Games)
            {
                // Skip junk rather than reject the whole file: one bad key must not cost the operator
                // every other override they set.
                if (string.IsNullOrWhiteSpace(id) || state == GameAvailability.Available) continue;
                games[id] = state;
            }

            // Same discipline for the source list: a hand-edited row missing its URL is dropped, not
            // fatal, and a duplicate id keeps the first. The null check is not redundant with the
            // element type: `"sources": [null]` is valid JSON, nullable annotations are erased at
            // runtime, and dereferencing it here would throw an NRE past Load()'s catch filter — turning
            // a hand-edited typo into a server that will not boot.
            var sources = new List<RegisteredMarketplace>();
            foreach (var source in settings.Sources ?? [])
            {
                if (source is null
                    || !MarketplaceSourceRegistry.IsValidId(source.Id)
                    || !MarketplaceClient.IsAllowedUrl(source.CatalogUrl)
                    || !MarketplaceClient.IsAllowedUrl(source.DownloadBaseUrl)
                    || sources.Any(s => string.Equals(s.Id, source.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                sources.Add(source);
            }

            // Limits are kept as read. They can't be judged here — whether a partial override is safe
            // depends on the configured baseline it will be laid over, which lives with LimitsProvider,
            // so Program.cs validates them at startup and ignores an unusable object with a warning.
            // Compile() drops unusable entries rather than rejecting the list, the same way a marketplace
            // row missing its URL is dropped — this file may have been hand-edited.
            var roomCodes = RoomCodeFilter.Compile(settings.RoomCodes?.Words, settings.RoomCodes?.Patterns);

            // An announcement with no text is not an announcement — a hand-edited object missing its
            // message would otherwise render as an empty banner nobody can explain.
            var announcement = string.IsNullOrWhiteSpace(settings.Announcement?.Text)
                ? null
                : Normalize(settings.Announcement);

            // Same discipline as the marketplace list: a row whose id or URL is unusable is dropped on its
            // own rather than costing the operator the rest of their endpoints.
            var webhooks = new List<WebhookEndpoint>();
            foreach (var hook in settings.Webhooks ?? [])
            {
                if (hook is null
                    || !MarketplaceSourceRegistry.IsValidId(hook.Id)
                    || !WebhookDispatcher.IsAllowedUrl(hook.Url)
                    || webhooks.Any(w => string.Equals(w.Id, hook.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                webhooks.Add(hook);
            }

            return new State(settings.MaintenanceMode, settings.MaintenanceMessage, games, sources, updates,
                settings.Limits ?? OperatorLimits.None, roomCodes, announcement, webhooks,
                settings.OfficialSourceDisabled,
                // Normalized rather than validated: an out-of-range hour is dropped to the default like any
                // other hand-edited junk here, and normalizing on the way IN means the timer arithmetic
                // downstream never has to defend against an hour of 25.
                settings.Schedule?.Normalize());
        }

        /// <summary>
        /// Fills in what a hand-edited announcement may be missing: an id (a dismissal is remembered
        /// against it, so it must exist), a timestamp, and a severity we recognise.
        /// </summary>
        private static PlatformAnnouncement Normalize(PlatformAnnouncement announcement) => announcement with
        {
            Id = string.IsNullOrWhiteSpace(announcement.Id) ? Guid.NewGuid().ToString("N") : announcement.Id,
            Text = announcement.Text.Trim(),
            Severity = AnnouncementSeverity(announcement.Severity),
            PostedAt = announcement.PostedAt == default ? DateTimeOffset.UtcNow : announcement.PostedAt,
            GameId = string.IsNullOrWhiteSpace(announcement.GameId) ? null : announcement.GameId.Trim(),
        };
    }

    /// <summary>
    /// The severities the shell knows how to draw. An unrecognised one reads as "info" rather than being
    /// passed through: it ends up in a CSS class name, and trusting an arbitrary string there is how a
    /// settings file becomes a styling injection.
    /// </summary>
    public static string AnnouncementSeverity(string? severity) =>
        string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase) ? "warning" : "info";
}
