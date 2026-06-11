using System.Collections.Concurrent;

namespace KnockBox.Server.Lobbies;

/// <summary>Tracks active lobbies in memory. A server restart drops them all by design.</summary>
public sealed class LobbyManager
{
    // Unambiguous alphabet (no 0/O/1/I) for human-readable 4-char lobby codes.
    private const string IdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new(StringComparer.OrdinalIgnoreCase);

    public Lobby? Get(string id) => _lobbies.TryGetValue(id, out var l) ? l : null;

    public Lobby Create(string gameId, string hostId, int maxPlayers)
    {
        while (true)
        {
            var lobby = new Lobby(NewId(), gameId, hostId, maxPlayers);
            if (_lobbies.TryAdd(lobby.Id, lobby)) return lobby;
        }
    }

    public void Remove(string id) => _lobbies.TryRemove(id, out _);

    private static string NewId()
    {
        Span<char> buf = stackalloc char[4];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = IdAlphabet[Random.Shared.Next(IdAlphabet.Length)];
        return new string(buf);
    }
}
