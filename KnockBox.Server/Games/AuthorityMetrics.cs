using System.Collections.Concurrent;
using System.Diagnostics;

namespace KnockBox.Server.Games;

/// <summary>
/// Per-game cost of running server-authority modules: how many module calls this server has executed for a
/// game, how long they took, and how many threw.
/// </summary>
/// <remarks>
/// <para><b>This is the only real per-game CPU on the server.</b> Games are HTML5/WASM and run in the
/// player's browser, so a plain game costs this process nothing but relay (see <c>RelayMetrics</c>). A
/// server-authority game is the exception: its module executes here, in a Jint engine, on the lobby's actor
/// thread. Reporting a CPU figure for every game would be inventing numbers; reporting it for the games that
/// actually have one is the honest version, and it is measured rather than estimated.</para>
/// <para><b>What "CPU" means here:</b> elapsed time inside <c>IAuthorityRuntime</c> calls on the actor's
/// drain task. The engine is single-threaded and the calls are synchronous CPU-bound JavaScript, so elapsed
/// and CPU agree to within scheduler noise — and the alternative (per-thread CPU counters) would be both
/// platform-specific and blind to the fact that a lobby's actor may be a different thread on each item.</para>
/// <para>Cumulative and never reset, like every other counter here: a rate needs two samples and that is the
/// reader's job. Lock-free, because it is written from every authority actor and read by the dashboard.</para>
/// </remarks>
public sealed class AuthorityMetrics
{
    // A class, not a struct: Interlocked needs a stable field address.
    private sealed class Counters
    {
        public long Calls;
        public long Ticks;       // stopwatch ticks, converted on read
        public long Errors;
        public long MaxCallTicks;
        public long NearBudget;  // calls that reached the warn fraction of their budget
        public long Overruns;    // calls that blew it outright
    }

    private readonly ConcurrentDictionary<string, Counters> _byGame = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What one game's authority modules have cost since the server started.</summary>
    /// <param name="Calls">Module invocations executed (ticks, intents, roster hooks, snapshots).</param>
    /// <param name="CpuSeconds">Total time spent inside them. See the class remarks on what this measures.</param>
    /// <param name="MaxCallMs">The slowest single call — the number that catches a module that occasionally
    /// stalls, which an average over thousands of cheap ticks hides completely.</param>
    /// <param name="NearBudgetCalls">Calls that reached the configured warn fraction of the per-call
    /// budget. The leading indicator: a module that is occasionally close is one bad turn — or one busier
    /// host — away from having its lobbies closed, and neither the average nor the max says how OFTEN
    /// that happens.</param>
    /// <param name="Overruns">Calls that blew the budget outright. Non-zero means players have already
    /// been affected: a dropped tick at best, a closed lobby once they stack up.</param>
    public readonly record struct GameAuthority(
        string GameId,
        long Calls,
        double CpuSeconds,
        long Errors,
        double MaxCallMs,
        long NearBudgetCalls,
        long Overruns)
    {
        /// <summary>Mean call cost in milliseconds, or 0 when nothing has run.</summary>
        public double AverageCallMs => Calls == 0 ? 0 : CpuSeconds * 1000 / Calls;
    }

    /// <summary>Records one completed module call.</summary>
    /// <param name="elapsedTicks">A <see cref="Stopwatch"/> tick count, not a <see cref="TimeSpan"/> tick count.</param>
    /// <param name="failed">The call threw or tripped a constraint.</param>
    /// <param name="nearBudget">The call reached the configured warn fraction of its per-call budget.</param>
    public void RecordCall(string gameId, long elapsedTicks, bool failed = false, bool nearBudget = false)
    {
        var counters = _byGame.GetOrAdd(gameId, static _ => new Counters());
        Interlocked.Increment(ref counters.Calls);
        Interlocked.Add(ref counters.Ticks, elapsedTicks);
        if (failed) Interlocked.Increment(ref counters.Errors);
        if (nearBudget) Interlocked.Increment(ref counters.NearBudget);

        // Compare-exchange loop rather than a lock: contention is per game and this is off the hot path of
        // everything except a very busy authority game.
        var observed = Interlocked.Read(ref counters.MaxCallTicks);
        while (elapsedTicks > observed)
        {
            var previous = Interlocked.CompareExchange(ref counters.MaxCallTicks, elapsedTicks, observed);
            if (previous == observed) break;
            observed = previous;
        }
    }

    /// <summary>Records one call that exceeded its per-call budget rather than merely approaching it.</summary>
    /// <remarks>Separate from the <c>failed</c> flag on <see cref="RecordCall"/>, which also counts a
    /// module that threw: "your game is buggy" and "your game is too slow for this server" are different
    /// problems with different fixes, and a single error count cannot tell an operator which they have.</remarks>
    public void RecordOverrun(string gameId) =>
        Interlocked.Increment(ref _byGame.GetOrAdd(gameId, static _ => new Counters()).Overruns);

    /// <summary>Point-in-time snapshot, busiest game first. Allocates a list per call; the dashboard's rate.</summary>
    public IReadOnlyList<GameAuthority> Snapshot()
    {
        var rows = new List<GameAuthority>(_byGame.Count);
        foreach (var (gameId, c) in _byGame)
        {
            var ticks = Volatile.Read(ref c.Ticks);
            rows.Add(new GameAuthority(
                gameId,
                Volatile.Read(ref c.Calls),
                (double)ticks / Stopwatch.Frequency,
                Volatile.Read(ref c.Errors),
                (double)Volatile.Read(ref c.MaxCallTicks) * 1000 / Stopwatch.Frequency,
                Volatile.Read(ref c.NearBudget),
                Volatile.Read(ref c.Overruns)));
        }
        rows.Sort((a, b) => b.CpuSeconds.CompareTo(a.CpuSeconds));
        return rows;
    }

    /// <summary>One game's row, or null when it has never run an authority module.</summary>
    public GameAuthority? For(string gameId) =>
        _byGame.TryGetValue(gameId, out var c)
            ? new GameAuthority(gameId, Volatile.Read(ref c.Calls),
                (double)Volatile.Read(ref c.Ticks) / Stopwatch.Frequency,
                Volatile.Read(ref c.Errors),
                (double)Volatile.Read(ref c.MaxCallTicks) * 1000 / Stopwatch.Frequency,
                Volatile.Read(ref c.NearBudget),
                Volatile.Read(ref c.Overruns))
            : null;

    /// <summary>
    /// Drops counters for games that no longer exist, so an uninstalled game doesn't hold a dashboard row
    /// forever. Wired to <c>GameCatalog.Discovered</c>, exactly like <c>RelayMetrics.Prune</c>.
    /// </summary>
    public void Prune(IEnumerable<string> liveGameIds)
    {
        var live = new HashSet<string>(liveGameIds, StringComparer.OrdinalIgnoreCase);
        foreach (var gameId in _byGame.Keys)
            if (!live.Contains(gameId)) _byGame.TryRemove(gameId, out _);
    }
}
