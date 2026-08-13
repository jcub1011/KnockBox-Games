namespace KnockBox.Server.Networking;

/// <summary>
/// One bucket's capacity and refill rate. A non-positive <paramref name="PerSecond"/> disables the
/// bucket entirely.
/// </summary>
/// <remarks>
/// A pair rather than two arguments so a bucket whose limits are operator-editable reads BOTH numbers
/// in one call — reading them separately could pair a new rate with an old burst mid-edit.
/// </remarks>
public readonly record struct RateLimit(double PerSecond, double Burst);

/// <summary>
/// Minimal token-bucket rate limiter: capacity <see cref="RateLimit.Burst"/>, refilled continuously at
/// <see cref="RateLimit.PerSecond"/>. A non-positive rate disables limiting (every take succeeds).
/// Thread-safe. Uses <see cref="TimeProvider"/> wall-clock time so tests drive it deterministically.
/// </summary>
/// <remarks>
/// The limits are read from a delegate on every <see cref="TryTake"/> rather than captured, so an
/// operator who edits a rate limit in the admin portal changes the behaviour of sockets that are
/// ALREADY OPEN. Capturing them at construction was the original design and it made the portal control
/// almost useless: the connections a flood is coming from are by definition already connected. The cost
/// is one delegate call per frame, which is nothing beside the message handling that follows it.
/// </remarks>
public sealed class TokenBucket
{
    private readonly Func<RateLimit> _limit;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTimeOffset _last;
    private double _tokens;

    /// <summary>A bucket with fixed limits — the shape for everything that isn't operator-editable.</summary>
    public TokenBucket(double ratePerSecond, double burst, TimeProvider time)
        : this(() => new RateLimit(ratePerSecond, burst), time) { }

    /// <summary>A bucket whose limits are re-read on every take. See the class remarks.</summary>
    public TokenBucket(Func<RateLimit> limit, TimeProvider time)
    {
        _limit = limit;
        _time = time;
        _last = time.GetUtcNow();
        _tokens = limit().Burst; // starts full
    }

    public bool TryTake()
    {
        var (ratePerSecond, burst) = _limit();
        if (ratePerSecond <= 0) return true;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            // Math.Min against the CURRENT burst, so lowering the cap takes effect immediately instead
            // of waiting for a bucket filled under the old one to drain.
            _tokens = Math.Min(burst, _tokens + (now - _last).TotalSeconds * ratePerSecond);
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
