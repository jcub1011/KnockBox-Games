using System.Collections.Concurrent;

namespace KnockBox.Server.Networking;

/// <summary>
/// Per-game counters for the message relay, so the admin portal can show what each game actually costs
/// this server.
///
/// Games are HTML5/WASM and execute in the player's browser, so it is tempting to call them free
/// server-side. They aren't. Every connected socket holds a bounded outbound channel plus a writer task,
/// and a <c>to:"all"</c> broadcast serializes once but sends once per recipient — so a chatty game with
/// large rosters costs real CPU, memory and bandwidth here even with no server-side logic at all. These
/// counters make that visible per game, which is the difference between "the server is busy" and
/// "<em>this</em> game is why".
///
/// Counters are cumulative since process start and never reset: rates are the reader's job (two polls and
/// a subtraction), which keeps this side lock-free and means two viewers can't spoil each other's numbers
/// by resetting.
/// </summary>
public sealed class RelayMetrics
{
    private readonly ConcurrentDictionary<string, Counters> _byGame = new(StringComparer.OrdinalIgnoreCase);

    // Mutable reference type rather than a struct: Interlocked needs a stable field address, which a
    // struct stored by value in a ConcurrentDictionary does not have.
    private sealed class Counters
    {
        public long FramesIn;
        public long FramesOut;
        public long BytesIn;
        public long BytesOut;
        public long FramesDropped;
    }

    /// <summary>One game's relay totals since process start.</summary>
    /// <param name="FramesIn">Frames accepted from clients for relay.</param>
    /// <param name="FramesOut">Frames handed to a recipient socket — a fan-out counts once per recipient.</param>
    /// <param name="FramesDropped">Frames the relay accepted but delivered nowhere.</param>
    public readonly record struct GameRelay(
        string GameId,
        long FramesIn,
        long FramesOut,
        long BytesIn,
        long BytesOut,
        long FramesDropped)
    {
        /// <summary>Average recipients per relayed frame — how much a broadcast multiplies. 0 with no traffic.</summary>
        public double FanOut => FramesIn == 0 ? 0 : (double)FramesOut / FramesIn;
    }

    /// <summary>
    /// Records one relayed frame. <paramref name="recipients"/> is how many sockets it was sent to (0 when
    /// the target wasn't connected), and <paramref name="bytes"/> the serialized frame size.
    /// </summary>
    public void RecordRelay(string gameId, int recipients, int bytes)
    {
        var counters = _byGame.GetOrAdd(gameId, static _ => new Counters());
        Interlocked.Increment(ref counters.FramesIn);
        Interlocked.Add(ref counters.BytesIn, bytes);
        if (recipients <= 0) return;
        Interlocked.Add(ref counters.FramesOut, recipients);
        Interlocked.Add(ref counters.BytesOut, (long)bytes * recipients);
    }

    /// <summary>Records frames the relay refused to deliver (no member, no attached socket, a payload
    /// rejected by the server-authority envelope rules).</summary>
    public void RecordDropped(string gameId)
    {
        var counters = _byGame.GetOrAdd(gameId, static _ => new Counters());
        Interlocked.Increment(ref counters.FramesDropped);
    }

    /// <summary>Point-in-time totals per game, busiest first.</summary>
    public IReadOnlyList<GameRelay> Snapshot()
    {
        var result = new List<GameRelay>(_byGame.Count);
        foreach (var (gameId, c) in _byGame)
        {
            result.Add(new GameRelay(
                gameId,
                Volatile.Read(ref c.FramesIn),
                Volatile.Read(ref c.FramesOut),
                Volatile.Read(ref c.BytesIn),
                Volatile.Read(ref c.BytesOut),
                Volatile.Read(ref c.FramesDropped)));
        }
        result.Sort((a, b) => b.FramesOut.CompareTo(a.FramesOut));
        return result;
    }

    /// <summary>Drops counters for games no longer in the catalog, so an uninstalled game doesn't hold a
    /// row forever. Wired to <c>GameCatalog.Discovered</c> in Program.cs.</summary>
    public void Prune(IEnumerable<string> liveGameIds)
    {
        // Case-insensitive, matching GameCatalog's own id comparison — otherwise a manifest id whose
        // casing differs from the dictionary key would prune a live game's counters every pass.
        var live = new HashSet<string>(liveGameIds, StringComparer.OrdinalIgnoreCase);
        foreach (var gameId in _byGame.Keys)
            if (!live.Contains(gameId))
                _byGame.TryRemove(gameId, out _);
    }
}
