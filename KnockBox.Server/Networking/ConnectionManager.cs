using System.Collections.Concurrent;
using System.Text.Json;
using KnockBox.Contracts;

namespace KnockBox.Server.Networking;

/// <summary>
/// Registry of live connections keyed by playerId, plus the serialization helpers used to push
/// messages out. Lobby membership lives in the LobbyManager; this type only resolves a playerId
/// to its socket and writes bytes.
/// </summary>
public sealed class ConnectionManager
{
    /// <summary>camelCase + case-insensitive, matching the wire shapes in <see cref="Message"/>.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, Connection> _byPlayer = new();

    public void Add(Connection c) => _byPlayer[c.PlayerId] = c;

    /// <summary>Removes the connection only if it is still the registered one (guards reconnect races).</summary>
    public void Remove(Connection c) => _byPlayer.TryRemove(KeyValuePair.Create(c.PlayerId, c));

    public Connection? Get(string playerId) => _byPlayer.TryGetValue(playerId, out var c) ? c : null;

    public static byte[] Serialize(Message message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, SerializerOptions);

    /// <summary>Send a message to a single player if connected.</summary>
    public void SendTo(string playerId, Message message)
    {
        if (_byPlayer.TryGetValue(playerId, out var c))
            c.Send(Serialize(message));
    }

    /// <summary>Send already-serialized bytes to a single player (used for fan-out — serialize once).</summary>
    public void SendRawTo(string playerId, byte[] bytes)
    {
        if (_byPlayer.TryGetValue(playerId, out var c))
            c.Send(bytes);
    }
}
