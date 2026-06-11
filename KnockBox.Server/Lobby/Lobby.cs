using KnockBox.Contracts;

namespace KnockBox.Server.Lobbies;

/// <summary>
/// In-memory membership for one lobby. The server owns no game state — only who is here, which
/// game, and which member is the authoritative <see cref="HostId"/> (the creator).
/// </summary>
public sealed class Lobby(string id, string gameId, string hostId, int maxPlayers)
{
    private readonly List<Player> _players = [];
    private readonly object _gate = new();

    public string Id { get; } = id;
    public string GameId { get; } = gameId;
    public string HostId { get; } = hostId;
    public int MaxPlayers { get; } = maxPlayers;

    /// <summary>
    /// Whether the lobby accepts new joins. The game owns this (the host sets it via
    /// <c>SetLobbyOpen</c>); the server never changes it. Open lobbies are listed and joinable;
    /// closed ones are hidden from the browser and reject new joins. Defaults to open on create.
    /// </summary>
    public bool Open { get; set; } = true;

    public IReadOnlyList<Player> Players { get { lock (_gate) return _players.ToArray(); } }

    public int Count { get { lock (_gate) return _players.Count; } }

    public bool Contains(string playerId) { lock (_gate) return _players.Any(p => p.Id == playerId); }

    /// <summary>Adds a player. Idempotent for an existing member (supports rejoin). False if full.</summary>
    public bool TryAdd(Player player)
    {
        lock (_gate)
        {
            if (_players.Any(p => p.Id == player.Id)) return true;
            if (_players.Count >= MaxPlayers) return false;
            _players.Add(player);
            return true;
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
}
