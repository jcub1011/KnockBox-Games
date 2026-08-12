namespace KnockBox.Server.Networking;

/// <summary>
/// The rate limit in front of the admin password endpoints (<c>/admin/api/auth/{login,setup}</c>).
///
/// Every attempt costs a 600k-iteration PBKDF2 (~0.4 s of one core, by design), which makes these the only
/// unauthenticated way to make this server do real work: unthrottled they are both a guessing oracle and a
/// CPU-exhaustion lever that starves the WebSocket relay the games depend on.
///
/// TWO buckets, because they answer different questions:
/// <list type="bullet">
/// <item><b>Per IP</b> — fair share. Keeps one caller from spending everybody else's allowance, so a real
/// operator can still log in while someone hammers the endpoint from elsewhere.</item>
/// <item><b>Server-wide</b> — the CPU ceiling. The per-IP bucket is only as trustworthy as the address it
/// keys on, and behind a proxy that address comes from <c>X-Forwarded-For</c> — a header the client writes.
/// A caller rotating it gets a fresh per-IP bucket per request, which is no limit at all for the thing that
/// actually costs the server. This bound holds regardless of what anyone claims to be.</item>
/// </list>
/// Login and setup deliberately share both buckets: they are equally expensive, and an attacker able to
/// spend the login budget on setup attempts would simply have two budgets.
/// </summary>
public sealed class AdminLoginThrottle
{
    private readonly IpRateLimiter _perIp;
    private readonly TokenBucket _global;

    /// <param name="perIpPerMinute">Attempts per minute per client address. <c>0</c> disables.</param>
    /// <param name="globalPerMinute">Attempts per minute across all callers. <c>0</c> disables.</param>
    public AdminLoginThrottle(int perIpPerMinute, int globalPerMinute, TimeProvider time)
    {
        _perIp = new IpRateLimiter(perIpPerMinute / 60.0, perIpPerMinute, time);
        _global = new TokenBucket(globalPerMinute / 60.0, globalPerMinute, time);
    }

    /// <summary>
    /// Consumes one attempt for <paramref name="ip"/>. Returns null when the attempt may proceed, else
    /// which limit refused it (<c>"per-IP"</c> / <c>"server-wide"</c>) — worth logging, because the two
    /// mean very different things to an operator reading the log.
    /// </summary>
    public string? Refuse(string ip)
    {
        // Per-IP first, so a single abusive caller exhausts its own share before it can touch the global
        // allowance the rest of the world is drawing on.
        if (!_perIp.TryTake(ip)) return "per-IP";
        return _global.TryTake() ? null : "server-wide";
    }
}
