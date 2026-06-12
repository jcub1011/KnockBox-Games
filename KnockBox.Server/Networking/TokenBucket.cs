namespace KnockBox.Server.Networking;

/// <summary>
/// Minimal token-bucket rate limiter: capacity <paramref name="burst"/>, refilled continuously at
/// <paramref name="ratePerSecond"/>. A non-positive rate disables limiting (every take succeeds).
/// Thread-safe. Uses <see cref="TimeProvider"/> wall-clock time so tests drive it deterministically.
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _ratePerSecond;
    private readonly double _burst;
    private readonly TimeProvider _time;
    private double _tokens;
    private DateTimeOffset _last;

    public TokenBucket(double ratePerSecond, double burst, TimeProvider time)
    {
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _time = time;
        _tokens = burst;
        _last = time.GetUtcNow();
    }

    public bool TryTake()
    {
        if (_ratePerSecond <= 0) return true;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _tokens = Math.Min(_burst, _tokens + (now - _last).TotalSeconds * _ratePerSecond);
            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}
