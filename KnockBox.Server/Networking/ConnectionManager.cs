using System.Collections.Concurrent;
using System.Text.Json;
using KnockBox.Contracts;

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
    /// <summary>camelCase + case-insensitive, matching the wire shapes in <see cref="Message"/>.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Connection> _byPlayer = new();      // control role
    private readonly ConcurrentDictionary<string, Connection> _gameByPlayer = new();  // data role

    public void Add(Connection c) => _byPlayer[c.PlayerId] = c;

    /// <summary>Removes the connection only if it is still the registered one (guards reconnect races).</summary>
    public void Remove(Connection c) => _byPlayer.TryRemove(KeyValuePair.Create(c.PlayerId, c));

    public Connection? Get(string playerId) => _byPlayer.TryGetValue(playerId, out var c) ? c : null;

    // ── Data-role (game) connections ─────────────────────────────────────────
    public void AddGame(Connection c) => _gameByPlayer[c.PlayerId] = c;
    public void RemoveGame(Connection c) => _gameByPlayer.TryRemove(KeyValuePair.Create(c.PlayerId, c));

    public static byte[] Serialize(Message message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);

    /// <summary>Send a message to a single player's control connection if connected.</summary>
    public void SendTo(string playerId, Message message)
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
}
