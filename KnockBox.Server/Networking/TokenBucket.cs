namespace KnockBox.Server.Networking;

/// <summary>
/// Minimal token-bucket rate limiter: capacity <paramref name="burst"/>, refilled continuously at
/// <paramref name="ratePerSecond"/>. A non-positive rate disables limiting (every take succeeds).
/// Thread-safe. Uses <see cref="TimeProvider"/> wall-clock time so tests drive it deterministically.
/// </summary>
public sealed class TokenBucket(double ratePerSecond, double burst, TimeProvider time)
{
    private readonly Lock _gate = new();
    private readonly double _burst = burst;   // constant capacity cap
    private DateTimeOffset _last = time.GetUtcNow();
    private double _tokens = burst;            // current token count (starts full)

    public bool TryTake()
    {
        if (ratePerSecond <= 0) return true;
        lock (_gate)
        {
            var now = time.GetUtcNow();
            _tokens = Math.Min(_burst, _tokens + (now - _last).TotalSeconds * ratePerSecond);
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
