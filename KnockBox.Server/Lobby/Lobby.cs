using KnockBox.Contracts;

namespace KnockBox.Server.Lobbies;

/// <summary>
/// In-memory membership for one lobby. The server owns no game state — only who is here, which
/// game, and which member is the authoritative <see cref="HostId"/> (the creator).
/// </summary>
public sealed class Lobby(string id, string gameId, string hostId, int maxPlayers)
{
    private readonly List<Player> _players = [];
    // Players the host has kicked. A kick is permanent for this lobby: kicked ids are refused by
    // TryAdd so they cannot rejoin (rejoin otherwise bypasses the Open/membership checks).
    private readonly HashSet<string> _kicked = [];
    private readonly Lock _gate = new();

    public string Id { get; } = id;
    public string GameId { get; } = gameId;
    public string HostId { get; } = hostId;
    public int MaxPlayers { get; } = maxPlayers;

    private bool _open = true;

    /// <summary>
    /// Whether the lobby accepts new joins. The game owns this (the host sets it via
    /// <c>SetLobbyOpen</c>); the server never changes it. Open lobbies are listed and joinable;
    /// closed ones are hidden from the browser and reject new joins. Defaults to open on create.
    /// Guarded by <c>_gate</c> like the rest of the lobby state: it's written on the host's thread
    /// and read on joiners' threads.
    /// </summary>
    public bool Open
    {
        get { lock (_gate) return _open; }
        set { lock (_gate) _open = value; }
    }

    public IReadOnlyList<Player> Players { get { lock (_gate) return [.. _players]; } }

    public int Count { get { lock (_gate) return _players.Count; } }

    public bool Contains(string playerId) { lock (_gate) return _players.Any(p => p.Id == playerId); }

    /// <summary>True if this player was kicked from the lobby (and is barred from rejoining).</summary>
    public bool IsKicked(string playerId) { lock (_gate) return _kicked.Contains(playerId); }

    /// <summary>Adds a player. Idempotent for an existing member (supports rejoin). False if full
    /// or if the player was kicked from this lobby.</summary>
    public bool TryAdd(Player player)
    {
        lock (_gate)
        {
            if (_kicked.Contains(player.Id)) return false;
            if (_players.Any(p => p.Id == player.Id)) return true;
            if (_players.Count >= MaxPlayers) return false;
            _players.Add(player);
            return true;
        }
    }

    /// <summary>
    /// Adds a player, assigning a display name unique within THIS lobby. If <paramref name="requestedName"/>
    /// collides (case-insensitively) with an existing member, a " (n)" suffix (n = 2, 3, …) is appended.
    /// The rename is lobby-scoped: only the stored <see cref="Player"/> carries it — the caller's own
    /// name is never touched, so the player keeps their normal name in other lobbies. Idempotent for an
    /// existing member (rejoin): returns the Player already on the roster, preserving the name assigned
    /// when they first joined. Returns false (with a null player) if the lobby is full or the player was kicked.
    /// </summary>
    public bool TryAddUnique(string playerId, string requestedName, out Player? player)
    {
        lock (_gate)
        {
            if (_kicked.Contains(playerId)) { player = null; return false; }
            var existing = _players.FirstOrDefault(p => p.Id == playerId);
            if (existing is not null) { player = existing; return true; } // rejoin: keep assigned name
            if (_players.Count >= MaxPlayers) { player = null; return false; }
            player = new Player(playerId, UniqueName(requestedName));
            _players.Add(player);
            return true;
        }
    }

    // Lowest non-colliding name: the request itself, else "<name> (n)" for n = 2,3,… Bounded by
    // membership (k members ⇒ at most k taken names ⇒ a free one within k+1 tries). Call under _gate.
    private string UniqueName(string requested)
    {
        bool Taken(string n) => _players.Any(p => string.Equals(p.DisplayName, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(requested)) return requested;
        for (var n = 2; ; n++)
        {
            var candidate = $"{requested} ({n})";
            if (!Taken(candidate)) return candidate;
        }
    }

    public bool Remove(string playerId)
    {
        lock (_gate)
        {
            var idx = _players.FindIndex(p => p.Id == playerId);
            if (idx < 0) return false;
            _players.RemoveAt(idx);
            return true;
        }
    }

    /// <summary>Removes a player and bars them from rejoining this lobby. Returns true if they were
    /// a member. Idempotent — recording the kick stands even if they had already left.</summary>
    public bool Kick(string playerId)
    {
        lock (_gate)
        {
            _kicked.Add(playerId);
            var idx = _players.FindIndex(p => p.Id == playerId);
            if (idx < 0) return false;
            _players.RemoveAt(idx);
            return true;
        }
    }
}
