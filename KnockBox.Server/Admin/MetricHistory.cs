namespace KnockBox.Server.Admin;

/// <summary>
/// A bounded time series of platform counters, so the dashboard can draw a graph instead of a single number
/// (spec §5.2).
/// </summary>
/// <remarks>
/// <para><b>Why the server keeps this rather than the browser.</b> Every counter here is cumulative and the
/// portal already differences consecutive polls — but it holds exactly one prior sample, so switching tabs,
/// reloading, or opening the portal on another machine starts the picture from nothing. Sampling here means
/// the history is a property of the server, not of one open tab, and the graph is populated the moment an
/// operator arrives — which is precisely when they want it, because something has gone wrong.</para>
/// <para><b>Bounded, explicitly.</b> A ring of <see cref="Capacity"/> samples: at the default 15-second
/// interval that is an hour, and the memory is a fixed handful of numbers per sample plus one small row per
/// game seen in that sample. Nothing here grows with uptime — this is the one new long-lived structure in
/// the feature, so its bound is stated rather than assumed.</para>
/// <para>Reads are cursor-based (<c>?after=seq</c>), the same house pattern as <c>AdminLogBuffer</c> and
/// <c>PackageJobRegistry</c>: ordinary polling becomes a stream with no SSE and no second socket role.</para>
/// </remarks>
public sealed class MetricHistory
{
    /// <summary>Samples retained when <c>KnockBox:MetricHistoryPoints</c> isn't set. 240 × 15s = one hour.</summary>
    public const int DefaultCapacity = 240;

    /// <summary>Per-game row inside one sample. Cumulative totals, exactly as the counters report them.</summary>
    public readonly record struct GameSample(
        string GameId,
        int Lobbies,
        long FramesOut,
        long BytesOut,
        long FramesDropped,
        double AuthorityCpuSeconds);

    /// <summary>One point in time.</summary>
    /// <param name="Sequence">Monotonic, so a client can ask for "everything after 41".</param>
    /// <param name="CpuSeconds">Process CPU seconds, cumulative — the reader differences it.</param>
    public readonly record struct Sample(
        long Sequence,
        DateTimeOffset At,
        double CpuSeconds,
        long WorkingSetMb,
        long ManagedHeapMb,
        int Lobbies,
        int Players,
        int GameSockets,
        int AuthorityLobbies,
        IReadOnlyList<GameSample> Games);

    private readonly Sample[] _ring;
    private readonly Lock _gate = new();
    private long _nextSequence = 1;
    private long _written;

    public MetricHistory(int capacity = DefaultCapacity) => _ring = new Sample[Math.Max(8, capacity)];

    /// <summary>How many samples are retained at most.</summary>
    public int Capacity => _ring.Length;

    /// <summary>Samples currently held.</summary>
    public int Count { get { lock (_gate) return (int)Math.Min(_written, _ring.Length); } }

    /// <summary>The newest sequence number, or 0 when nothing has been recorded.</summary>
    public long LastSequence { get { lock (_gate) return _nextSequence - 1; } }

    /// <summary>Records one sample, evicting the oldest when full.</summary>
    public void Add(Sample sample)
    {
        lock (_gate)
        {
            var stamped = sample with { Sequence = _nextSequence++ };
            _ring[(int)(_written % _ring.Length)] = stamped;
            _written++;
        }
    }

    /// <summary>
    /// Samples newer than <paramref name="afterSequence"/>, oldest first. 0 returns everything retained,
    /// which is what a portal entering the tab asks for.
    /// </summary>
    public IReadOnlyList<Sample> Read(long afterSequence = 0)
    {
        lock (_gate)
        {
            var held = (int)Math.Min(_written, _ring.Length);
            var start = _written - held;
            var result = new List<Sample>(held);
            for (var i = 0L; i < held; i++)
            {
                var sample = _ring[(int)((start + i) % _ring.Length)];
                if (sample.Sequence > afterSequence) result.Add(sample);
            }
            return result;
        }
    }
}
