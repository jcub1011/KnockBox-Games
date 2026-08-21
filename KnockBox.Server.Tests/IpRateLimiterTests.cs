using KnockBox.Server.Networking;
using Xunit;

namespace KnockBox.Server.Tests;

public class IpRateLimiterTests
{
    private static IpRateLimiter PerMinute(int attempts, MutableTimeProvider clock) =>
        new(attempts / 60.0, attempts, clock);

    [Fact]
    public void Allows_the_allowance_then_refuses()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var limiter = PerMinute(10, clock);

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryTake("10.0.0.1"), $"attempt {i + 1} of the allowance should be permitted");

        Assert.False(limiter.TryTake("10.0.0.1"));
    }

    [Fact]
    public void Refills_over_time()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var limiter = PerMinute(10, clock);
        for (var i = 0; i < 10; i++) limiter.TryTake("10.0.0.1");
        Assert.False(limiter.TryTake("10.0.0.1"));

        // 10/minute ⇒ one token every 6 seconds. Advance a little past that: asserting exactly on the
        // boundary would ride a floating-point edge (6 × 10/60.0 lands a hair under 1.0).
        clock.Advance(TimeSpan.FromSeconds(7));

        Assert.True(limiter.TryTake("10.0.0.1"));
        Assert.False(limiter.TryTake("10.0.0.1"));
    }

    [Fact]
    public void Buckets_are_per_ip_so_one_attacker_cannot_lock_out_an_admin()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var limiter = PerMinute(10, clock);

        for (var i = 0; i < 20; i++) limiter.TryTake("203.0.113.9");   // attacker burns their budget
        Assert.False(limiter.TryTake("203.0.113.9"));

        Assert.True(limiter.TryTake("10.0.0.1"));                      // the real admin is unaffected
    }

    [Fact]
    public void A_non_positive_rate_disables_the_limiter()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new IpRateLimiter(0, 0, clock);

        for (var i = 0; i < 1000; i++) Assert.True(limiter.TryTake("10.0.0.1"));
        Assert.Equal(0, limiter.TrackedIps);   // disabled ⇒ tracks nothing at all
    }

    [Fact]
    public void Idle_buckets_are_swept_so_distinct_source_ips_cannot_grow_the_map_forever()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var limiter = PerMinute(10, clock);

        for (var i = 0; i < 500; i++) limiter.TryTake($"198.51.100.{i}");
        Assert.Equal(500, limiter.TrackedIps);

        // Past the idle window every one of those buckets has refilled to full, so dropping them is
        // lossless. The next call triggers the sweep.
        clock.Advance(TimeSpan.FromMinutes(5));
        limiter.TryTake("10.0.0.1");

        Assert.Equal(1, limiter.TrackedIps);
    }

    [Fact]
    public void A_continuously_hammering_ip_never_exceeds_its_rate()
    {
        // The realistic attack: never go idle, so the bucket is never evicted, and keep trying. This also
        // crosses several sweep intervals — a sweep that reset an active bucket to full would show up here
        // as a grant count far above the rate.
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var limiter = PerMinute(10, clock);

        var granted = 0;
        for (var second = 0; second < 600; second++)   // 10 simulated minutes, one attempt per second
        {
            if (limiter.TryTake("203.0.113.9")) granted++;
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // 10 (initial burst) + 10/minute × 10 minutes ≈ 110 out of 600 attempts. The margin absorbs
        // refill-boundary rounding; what matters is that it is ~110 and not ~600.
        Assert.InRange(granted, 100, 120);
        Assert.Equal(1, limiter.TrackedIps);
    }
}

/// <summary>
/// The admin password throttle: a per-IP bucket for fair share plus a server-wide bucket for the CPU
/// ceiling. The second exists because the first keys on an address a client can choose.
/// </summary>
public class AdminLoginThrottleTests
{
    [Fact]
    public void One_client_is_held_to_the_per_ip_allowance()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new AdminLoginThrottle(perIpPerMinute: 10, globalPerMinute: 60, clock);

        for (var i = 0; i < 10; i++) Assert.Null(throttle.Refuse("10.0.0.1"));

        Assert.Equal("per-IP", throttle.Refuse("10.0.0.1"));
    }

    [Fact]
    public void A_rotating_client_address_still_hits_the_server_wide_ceiling()
    {
        // The bypass this bucket exists for: with ForwardedHeaders on and no KnownProxies, X-Forwarded-For
        // is whatever the caller writes, so every request can present a brand-new address and draw a fresh
        // per-IP budget. Without a global bound that is 600k-iteration PBKDF2 on demand, from an
        // unauthenticated request, until every core is busy and the game relay starves.
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new AdminLoginThrottle(perIpPerMinute: 10, globalPerMinute: 60, clock);

        var granted = 0;
        for (var i = 0; i < 500; i++)
            if (throttle.Refuse($"198.51.100.{i % 256}.{i}") is null) granted++;

        Assert.Equal(60, granted);
        Assert.Equal("server-wide", throttle.Refuse("203.0.113.9"));
    }

    [Fact]
    public void A_real_operator_is_unaffected_by_someone_else_burning_their_own_budget()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new AdminLoginThrottle(perIpPerMinute: 10, globalPerMinute: 60, clock);

        for (var i = 0; i < 20; i++) throttle.Refuse("203.0.113.9");
        Assert.Equal("per-IP", throttle.Refuse("203.0.113.9"));

        Assert.Null(throttle.Refuse("10.0.0.1"));
    }

    [Fact]
    public void Both_limits_refill_over_time()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new AdminLoginThrottle(perIpPerMinute: 10, globalPerMinute: 60, clock);

        for (var i = 0; i < 10; i++) throttle.Refuse("10.0.0.1");
        Assert.NotNull(throttle.Refuse("10.0.0.1"));

        clock.Advance(TimeSpan.FromSeconds(7)); // 10/min ⇒ a token every 6s, plus rounding margin
        Assert.Null(throttle.Refuse("10.0.0.1"));
    }

    [Fact]
    public void Zero_disables_either_limit()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var throttle = new AdminLoginThrottle(perIpPerMinute: 0, globalPerMinute: 0, clock);

        for (var i = 0; i < 500; i++) Assert.Null(throttle.Refuse("10.0.0.1"));
    }
}
