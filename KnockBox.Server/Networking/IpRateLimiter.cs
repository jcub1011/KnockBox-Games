using System.Collections.Concurrent;

namespace KnockBox.Server.Networking;

/// <summary>
/// Per-IP token-bucket rate limiter for HTTP endpoints whose *work* is worth protecting, not just their
/// bandwidth. Built for the admin login: every attempt runs a deliberately expensive PBKDF2 (600k
/// iterations, see <see cref="Security.AdminAuthService"/>), so an unthrottled endpoint is both a
/// password-guessing oracle and a lever for burning the server's CPU from an unauthenticated request —
/// which on this server starves the WebSocket relay the games depend on. A non-positive rate disables
/// limiting. Thread-safe.
///
/// Behind a reverse proxy this is only meaningful with <c>KnockBox:ForwardedHeaders</c> enabled —
/// otherwise every request carries the proxy's IP and shares one bucket. That is a fail-CLOSED
/// degradation (everyone shares the limit) rather than fail-open, so it is safe by default.
/// </summary>
/// <remarks>
/// Unlike the per-connection buckets in <c>WebSocketHandler</c>, these are keyed by a value an attacker
/// supplies, so the map is swept: without that, a stream of distinct source IPs would grow it without
/// bound. Eviction is lossless rather than a heuristic — a bucket idle for longer than its own refill
/// time (<c>burst / rate</c>) has necessarily refilled to full capacity, so it is indistinguishable from
/// a freshly created one and dropping it cannot let anyone exceed the limit.
/// </remarks>
public sealed class IpRateLimiter
{
    private readonly double _ratePerSecond;
    private readonly double _burst;
    private readonly TimeProvider _time;
    private readonly TimeSpan _idleWindow;
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly Lock _sweepGate = new();
    private DateTimeOffset _nextSweep;

    public IpRateLimiter(double ratePerSecond, double burst, TimeProvider time)
    {
        _ratePerSecond = ratePerSecond;
        _burst = burst;
        _time = time;
        // How long until an untouched bucket is certainly full again. Floored at a minute so a very slow
        // rate doesn't make the sweep interval absurdly long, and so the map is tidied regularly.
        var refill = ratePerSecond > 0 ? TimeSpan.FromSeconds(burst / ratePerSecond) : TimeSpan.Zero;
        _idleWindow = refill < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : refill;
        _nextSweep = time.GetUtcNow() + _idleWindow;
    }

    /// <summary>Consumes one token for <paramref name="ip"/>. False ⇒ the caller should refuse the request.</summary>
    public bool TryTake(string ip)
    {
        if (_ratePerSecond <= 0) return true;

        var now = _time.GetUtcNow();
        SweepIfDue(now);

        var entry = _entries.GetOrAdd(ip, _ => new Entry(new TokenBucket(_ratePerSecond, _burst, _time)));
        entry.Touch(now);
        return entry.Bucket.TryTake();
    }

    /// <summary>Buckets currently tracked. For tests/diagnostics — proves the sweep actually reclaims.</summary>
    public int TrackedIps => _entries.Count;

    private void SweepIfDue(DateTimeOffset now)
    {
        // One sweeper at a time; everyone else proceeds straight to their bucket rather than blocking.
        // The sweep is pure maintenance, so a skipped attempt costs nothing but a slightly larger map.
        if (now < _nextSweep) return;
        lock (_sweepGate)
        {
            if (now < _nextSweep) return;
            _nextSweep = now + _idleWindow;
        }

        foreach (var (ip, entry) in _entries)
        {
            if (now - entry.LastSeen < _idleWindow) continue;
            // Remove only if still the entry we judged: a concurrent TryTake may have just touched it.
            var stale = _entries.TryGetValue(ip, out var current) && ReferenceEquals(current, entry)
                        && now - entry.LastSeen >= _idleWindow;
            if (stale) _entries.TryRemove(KeyValuePair.Create(ip, entry));
        }
    }

    private sealed class Entry(TokenBucket bucket)
    {
        private long _lastSeenTicks;

        public TokenBucket Bucket { get; } = bucket;

        public DateTimeOffset LastSeen => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);

        public void Touch(DateTimeOffset now) => Interlocked.Exchange(ref _lastSeenTicks, now.UtcTicks);
    }
}
