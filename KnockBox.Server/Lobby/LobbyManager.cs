using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace KnockBox.Server.Lobbies;

/// <summary>Tracks active lobbies in memory. A server restart drops them all by design.</summary>
public sealed class LobbyManager
{
    private const int MAX_CODE_GENERATION_ATTEMPTS = 5;
    // Unambiguous alphabet (no 0/O/1/I) for human-readable 4-char lobby codes.
    private const string IdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new(StringComparer.OrdinalIgnoreCase);

    public Lobby? Get(string id) => _lobbies.TryGetValue(id, out var l) ? l : null;

    /// <summary>Creates a lobby with a unique code. Returns false (and a null <paramref name="lobby"/>)
    /// if a free code couldn't be found within <see cref="MAX_CODE_GENERATION_ATTEMPTS"/> tries.</summary>
    public bool TryCreate(string gameId, string hostId, int maxPlayers, [NotNullWhen(true)] out Lobby? lobby)
    {
        int attempt = 0;
        while (attempt++ < MAX_CODE_GENERATION_ATTEMPTS)
        {
            lobby = new Lobby(NewId(), gameId, hostId, maxPlayers);
            if (_lobbies.TryAdd(lobby.Id, lobby)) return true;
        }

        lobby = null;
        return false;
    }

    public void Remove(string id) => _lobbies.TryRemove(id, out _);

    /// <summary>Point-in-time snapshot of the active lobbies, so a caller (e.g. the reconnect-grace
    /// reaper) can iterate and remove without mutating the dictionary mid-enumeration.</summary>
    public IReadOnlyCollection<Lobby> Snapshot() => [.. _lobbies.Values];

    private static string NewId()
    {
        Span<char> buf = stackalloc char[4];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = IdAlphabet[Random.Shared.Next(IdAlphabet.Length)];
        return new string(buf);
    }
}
