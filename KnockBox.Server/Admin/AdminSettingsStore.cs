using System.Text.Json;
using KnockBox.Server.Security;

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
        var settings = new AdminSettings(_state.MaintenanceMode, _state.MaintenanceMessage, _state.Games);
        var temp = FilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllBytes(temp,
                JsonSerializer.SerializeToUtf8Bytes(settings, AdminSettingsJsonContext.Default.AdminSettings));
            File.Move(temp, FilePath, overwrite: true);
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
        IReadOnlyDictionary<string, GameAvailability> Games)
    {
        public static readonly State Default =
            new(false, null, new Dictionary<string, GameAvailability>(GameIdComparer));

        public static State From(AdminSettings settings)
        {
            var games = new Dictionary<string, GameAvailability>(GameIdComparer);
            foreach (var (id, state) in settings.Games ?? Default.Games)
            {
                // Skip junk rather than reject the whole file: one bad key must not cost the operator
                // every other override they set.
                if (string.IsNullOrWhiteSpace(id) || state == GameAvailability.Available) continue;
                games[id] = state;
            }
            return new State(settings.MaintenanceMode, settings.MaintenanceMessage, games);
        }
    }
}
