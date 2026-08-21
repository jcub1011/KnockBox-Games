using System.Collections.Concurrent;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Networking;

/// <summary>
/// Registry of live connections keyed by playerId, plus the serialization helpers used to push
/// messages out. Lobby membership lives in the LobbyManager; this type only resolves a playerId
/// to its socket and writes bytes.
///
/// A player has two independent connections while in a game: the <b>control</b> connection (the
/// shell's socket, identity-token authenticated) and the <b>game</b> connection (the game iframe's
/// own socket on the game origin, ticket authenticated). They are tracked in separate maps because
/// a single playerId is present in both at once.
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, Connection> _byPlayer = new();      // control role
    private readonly ConcurrentDictionary<string, Connection> _gameByPlayer = new();  // data role

    public void Add(Connection c) => _byPlayer[c.PlayerId] = c;

    /// <summary>Removes the connection only if it is still the registered one (guards reconnect
    /// races). Returns true if this connection was the registered one and was removed; false if a
    /// newer connection for the same player had already superseded it.</summary>
    public bool Remove(Connection c) => _byPlayer.TryRemove(KeyValuePair.Create(c.PlayerId, c));

    public Connection? Get(string playerId) => _byPlayer.TryGetValue(playerId, out var c) ? c : null;

    // ── Data-role (game) connections ─────────────────────────────────────────
    public Connection? GetGame(string playerId) => _gameByPlayer.TryGetValue(playerId, out var c) ? c : null;
    public void AddGame(Connection c) => _gameByPlayer[c.PlayerId] = c;
    public void RemoveGame(Connection c) => _gameByPlayer.TryRemove(KeyValuePair.Create(c.PlayerId, c));

    public static byte[] Serialize(IMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, KnockBoxProtocolContext.Default.IMessage);

    /// <summary>Send a message to a single player's control connection if connected.</summary>
    public void SendTo(string playerId, IMessage message)
    {
        if (_byPlayer.TryGetValue(playerId, out var c))
            c.Send(Serialize(message));
    }

    /// <summary>Send already-serialized bytes to a single player's control connection (fan-out — serialize once).</summary>
    public void SendRawTo(string playerId, byte[] bytes)
    {
        if (_byPlayer.TryGetValue(playerId, out var c))
            c.Send(bytes);
    }

    /// <summary>Send already-serialized bytes to a single player's game connection, if attached.</summary>
    public void SendRawToGame(string playerId, byte[] bytes)
    {
        if (_gameByPlayer.TryGetValue(playerId, out var c))
            c.Send(bytes);
    }

    /// <summary>True if the player currently has an attached game (data-role) connection.</summary>
    public bool HasGameConnection(string playerId) => _gameByPlayer.ContainsKey(playerId);

    /// <summary>
    /// Sends one message to <b>every</b> connected control socket, and reports how many it reached.
    /// Serializes once, like every other fan-out here.
    /// </summary>
    /// <remarks>
    /// The only platform-wide fan-out in the server; everything else is scoped to a lobby. Two things
    /// follow from that and are deliberate:
    /// <list type="bullet">
    /// <item>It iterates the dictionary directly rather than taking a <see cref="ControlConnections"/>
    /// snapshot: an operator's announcement should not allocate an array of every player on the server. A
    /// concurrent join or drop simply lands on one side of the enumeration, which for a banner is fine —
    /// a player who connects during it gets the announcement on connect anyway.</item>
    /// <item>Control sockets overflow with <see cref="OutboundOverflow.CloseOnFull"/>, so a frame sent to a
    /// socket whose queue is already full tears that connection down. That is the intended policy for the
    /// control plane (its events are precious), and it means this method can, in principle, disconnect a
    /// wedged client. Acceptable — a client too backed up to receive a banner is not receiving lobby
    /// events either.</item>
    /// </list>
    /// </remarks>
    public int BroadcastToAllControl(IMessage message)
    {
        var bytes = Serialize(message);
        var sent = 0;
        foreach (var connection in _byPlayer.Values)
        {
            connection.Send(bytes);
            sent++;
        }
        return sent;
    }

    // ── Observability (admin portal) ──────────────────────────────────────────
    /// <summary>Live control (shell) sockets. Also the count of connected players, since a player
    /// holds exactly one control socket. Cheap — no snapshot allocation.</summary>
    public int ControlCount => _byPlayer.Count;

    /// <summary>Live game (data-role) sockets — one per player currently inside a game.</summary>
    public int GameCount => _gameByPlayer.Count;

    /// <summary>Point-in-time snapshot of the live game sockets, for per-game relay accounting. The
    /// dictionaries are the authority on which sockets exist, so aggregate over this rather than
    /// keeping a parallel counter that could drift from it.</summary>
    public IReadOnlyCollection<Connection> GameConnections() => [.. _gameByPlayer.Values];

    /// <summary>Point-in-time snapshot of the live control sockets.</summary>
    public IReadOnlyCollection<Connection> ControlConnections() => [.. _byPlayer.Values];
}
