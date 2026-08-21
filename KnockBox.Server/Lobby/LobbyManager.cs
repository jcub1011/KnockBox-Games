using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace KnockBox.Server.Lobbies;

/// <summary>Tracks active lobbies in memory. A server restart drops them all by design.</summary>
/// <param name="clock">Stamps <see cref="Lobby.CreatedAt"/>. Optional so the many tests that build a
/// bare manager keep working; production passes the registered <see cref="TimeProvider"/>.</param>
/// <param name="codeFilter">The operator's blocklist for generated codes, read per draw so an edit in the
/// portal applies without a restart. Optional, and absent means block nothing — which is what the many
/// tests that build a bare manager want, and what a deployment that never configures it gets.</param>
public sealed class LobbyManager(TimeProvider? clock = null, Func<RoomCodeFilter>? codeFilter = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<RoomCodeFilter> _codeFilter = codeFilter ?? (() => RoomCodeFilter.Empty);

    private const int MAX_CODE_GENERATION_ATTEMPTS = 5;

    /// <summary>
    /// Redraws allowed per placement attempt when the blocklist rejects a code.
    /// </summary>
    /// <remarks>
    /// A blocked draw must NOT consume a placement attempt: those five exist for code collisions, and
    /// spending them on a blocklist would make an operator's word list quietly raise the failure rate of
    /// lobby creation. With the admin API refusing any list that removes more than half the code space,
    /// sixteen consecutive blocked draws has a probability under 1 in 65,000.
    /// </remarks>
    private const int MAX_CODE_DRAWS_PER_ATTEMPT = 16;

    /// <summary>Length of a room code. Part of the protocol: players read these aloud.</summary>
    public const int CodeLength = 4;

    /// <summary>Unambiguous alphabet (no 0/O/1/I) for human-readable 4-char lobby codes.</summary>
    public const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Lobby> _lobbies = new(StringComparer.OrdinalIgnoreCase);

    public Lobby? Get(string id) => _lobbies.TryGetValue(id, out var l) ? l : null;

    /// <summary>Count of active lobbies. Cheap (no snapshot allocation) — for the memory diagnostics log.</summary>
    public int Count => _lobbies.Count;

    /// <summary>
    /// Active lobbies running one game. Walks the dictionary rather than keeping a per-game index: the
    /// callers are a lobby create (a per-player rate-limited operation) and the admin dashboard, and the
    /// whole collection is small enough that an index would be bookkeeping to keep correct across every
    /// remove path in exchange for nothing measurable.
    /// </summary>
    public int CountForGame(string gameId)
    {
        var count = 0;
        foreach (var lobby in _lobbies.Values)
            if (string.Equals(lobby.GameId, gameId, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    /// <summary>Creates a lobby with a unique code. Returns false (and a null <paramref name="lobby"/>)
    /// if a free code couldn't be found within <see cref="MAX_CODE_GENERATION_ATTEMPTS"/> tries.</summary>
    public bool TryCreate(string gameId, string hostId, int maxPlayers, [NotNullWhen(true)] out Lobby? lobby,
        bool isServerAuthority = false)
    {
        var now = _clock.GetUtcNow();
        var filter = _codeFilter();
        int attempt = 0;
        while (attempt++ < MAX_CODE_GENERATION_ATTEMPTS)
        {
            if (!TryDrawCode(filter, out var code)) break;
            lobby = new Lobby(code, gameId, hostId, maxPlayers, now, isServerAuthority);
            if (_lobbies.TryAdd(lobby.Id, lobby)) return true;
        }

        lobby = null;
        return false;
    }

    public void Remove(string id) => _lobbies.TryRemove(id, out _);

    /// <summary>Point-in-time snapshot of the active lobbies, so a caller (e.g. the reconnect-grace
    /// reaper) can iterate and remove without mutating the dictionary mid-enumeration.</summary>
    public IReadOnlyCollection<Lobby> Snapshot() => [.. _lobbies.Values];

    /// <summary>Draws a code the operator's blocklist allows, or fails when it blocks too much.</summary>
    private static bool TryDrawCode(RoomCodeFilter filter, out string code)
    {
        Span<char> buf = stackalloc char[CodeLength];
        for (var draw = 0; draw < MAX_CODE_DRAWS_PER_ATTEMPT; draw++)
        {
            for (var i = 0; i < buf.Length; i++)
                buf[i] = CodeAlphabet[Random.Shared.Next(CodeAlphabet.Length)];

            // The common case is an empty blocklist, which the filter answers without reading the code.
            if (filter.IsBlockedUpper(buf)) continue;
            code = new string(buf);
            return true;
        }

        code = "";
        return false;
    }
}
