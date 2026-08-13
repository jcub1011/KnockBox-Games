namespace KnockBox.Server.Admin;

/// <summary>Where a game is in an install/update cycle right now. Never persisted.</summary>
public enum GameLifecycle
{
    /// <summary>Nothing in flight. The overwhelmingly common state.</summary>
    Idle,

    /// <summary>An update is ready and waiting for the game's running lobbies to end. New lobbies are refused.</summary>
    Draining,

    /// <summary>Files are being swapped right now. New lobbies are refused.</summary>
    Updating,
}

/// <summary>
/// The transient half of game policy: which games are mid-update, laid over the persisted operator
/// policy in <see cref="AdminSettingsStore"/>.
/// </summary>
/// <remarks>
/// It COMPOSES over the settings store rather than extending it, because that class's entire contract is
/// "this is the part that survives a restart" — and these states must not. Lobbies are in-memory, so
/// after a restart every game has zero lobbies and a persisted "draining" would be stale by
/// construction; a server killed mid-update would come back with a game permanently unlaunchable and no
/// obvious cause. Losing a pending drain on restart is strictly better: the update is simply offered
/// again, and with no lobbies left it applies at once.
///
/// Reads are lock-free (a volatile immutable snapshot swapped atomically, the same discipline as the
/// settings store and <c>GameCatalog</c>) because <c>WebSocketHandler</c> calls them on the lobby-create
/// path. The map is empty except during an update, so the common case is one dictionary miss.
/// </remarks>
public sealed class GameLifecycleGate(AdminSettingsStore settings) : IPlatformPolicy
{
    private volatile IReadOnlyDictionary<string, GameLifecycle> _states =
        new Dictionary<string, GameLifecycle>(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _writeGate = new();

    public bool MaintenanceMode => settings.MaintenanceMode;
    public string? MaintenanceMessage => settings.MaintenanceMessage;

    /// <summary>What the engine is doing to this game, if anything.</summary>
    public GameLifecycle StateOf(string gameId) =>
        _states.TryGetValue(gameId, out var state) ? state : GameLifecycle.Idle;

    /// <summary>Every game that is not idle. Small by construction — usually empty.</summary>
    public IReadOnlyDictionary<string, GameLifecycle> States => _states;

    /// <summary>
    /// Operator policy AND the engine's own gate. Both must allow it: an update in flight blocks a new
    /// lobby even for a game the operator has left perfectly available.
    /// </summary>
    public bool CanCreateLobby(string gameId) =>
        settings.CanCreateLobby(gameId) && StateOf(gameId) == GameLifecycle.Idle;

    /// <summary>
    /// Unchanged by lifecycle: a game being updated stays in the catalog. Removing it would make the
    /// grid flicker games in and out on a timescale players can see, and the refusal message below says
    /// far more than an absent tile does.
    /// </summary>
    public bool IsListed(string gameId) => settings.IsListed(gameId);

    public string? UnavailableReason(string gameId) => StateOf(gameId) switch
    {
        GameLifecycle.Draining =>
            "This game is being updated as soon as the games already running finish. Try again shortly.",
        GameLifecycle.Updating =>
            "This game is being updated right now. Try again in a moment.",
        _ => null,
    };

    /// <summary>Marks a game as draining or updating.</summary>
    public void Enter(string gameId, GameLifecycle state)
    {
        if (state == GameLifecycle.Idle) { Leave(gameId); return; }

        lock (_writeGate)
        {
            var next = new Dictionary<string, GameLifecycle>(_states, StringComparer.OrdinalIgnoreCase)
            {
                [gameId] = state,
            };
            _states = next;
        }
    }

    /// <summary>Returns a game to idle. Safe to call for a game that was never gated.</summary>
    public void Leave(string gameId)
    {
        lock (_writeGate)
        {
            if (!_states.ContainsKey(gameId)) return;
            var next = new Dictionary<string, GameLifecycle>(_states, StringComparer.OrdinalIgnoreCase);
            next.Remove(gameId);
            _states = next;
        }
    }
}
