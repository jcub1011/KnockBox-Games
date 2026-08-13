using KnockBox.Server.Networking;
using Xunit;

namespace KnockBox.Server.Tests;

public class TokenBucketTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Burst_is_consumable_immediately_then_takes_fail()
    {
        var time = new MutableTimeProvider(T0);
        var bucket = new TokenBucket(ratePerSecond: 1, burst: 3, time);

        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void Tokens_refill_at_the_configured_rate()
    {
        var time = new MutableTimeProvider(T0);
        var bucket = new TokenBucket(ratePerSecond: 2, burst: 2, time);
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        time.Advance(TimeSpan.FromMilliseconds(500)); // 2/s × 0.5s = 1 token back
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void Refill_is_capped_at_the_burst()
    {
        var time = new MutableTimeProvider(T0);
        var bucket = new TokenBucket(ratePerSecond: 10, burst: 2, time);

        time.Advance(TimeSpan.FromMinutes(5)); // would be 3000 tokens uncapped
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void Non_positive_rate_disables_limiting()
    {
        var bucket = new TokenBucket(ratePerSecond: 0, burst: 0, new MutableTimeProvider(T0));
        for (var i = 0; i < 1000; i++) Assert.True(bucket.TryTake());
    }

    // ── Live limits (the operator-editable shape) ─────────────────────────────

    [Fact]
    public void A_live_bucket_tightens_without_being_rebuilt()
    {
        var time = new MutableTimeProvider(T0);
        var limit = new RateLimit(PerSecond: 10, Burst: 10);
        var bucket = new TokenBucket(() => limit, time);

        Assert.True(bucket.TryTake()); // 9 of 10 tokens left

        // What an operator does mid-flood. The bucket is the SAME instance a running socket holds, and the
        // tokens it had banked under the old burst are clamped away rather than spent: one take, not nine.
        limit = new RateLimit(PerSecond: 1, Burst: 1);
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }

    [Fact]
    public void A_live_bucket_loosens_without_being_rebuilt()
    {
        var time = new MutableTimeProvider(T0);
        var limit = new RateLimit(PerSecond: 1, Burst: 1);
        var bucket = new TokenBucket(() => limit, time);

        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        limit = new RateLimit(PerSecond: 100, Burst: 100);
        time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 100; i++) Assert.True(bucket.TryTake());
    }

    [Fact]
    public void A_live_bucket_can_be_disabled_and_re_enabled()
    {
        var time = new MutableTimeProvider(T0);
        var limit = new RateLimit(PerSecond: 1, Burst: 1);
        var bucket = new TokenBucket(() => limit, time);
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());

        limit = new RateLimit(PerSecond: 0, Burst: 0);
        for (var i = 0; i < 100; i++) Assert.True(bucket.TryTake());

        // Re-enabling must not hand out a windfall from the disabled stretch: the bucket refills to the
        // burst and no further, however long it spent switched off.
        limit = new RateLimit(PerSecond: 1, Burst: 2);
        time.Advance(TimeSpan.FromHours(1));
        Assert.True(bucket.TryTake());
        Assert.True(bucket.TryTake());
        Assert.False(bucket.TryTake());
    }
}

public class IpConnectionGateTests
{
    [Fact]
    public void Caps_concurrent_entries_per_ip_and_releases_on_exit()
    {
        var gate = new IpConnectionGate(maxPerIp: 2);

        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.False(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("5.6.7.8")); // other IPs unaffected

        gate.Exit("1.2.3.4");
        Assert.True(gate.TryEnter("1.2.3.4"));
    }

    [Fact]
    public void Zero_disables_the_cap()
    {
        var gate = new IpConnectionGate(maxPerIp: 0);
        for (var i = 0; i < 100; i++) Assert.True(gate.TryEnter("1.2.3.4"));
    }

    [Fact]
    public void Exit_without_enter_is_harmless()
    {
        var gate = new IpConnectionGate(maxPerIp: 1);
        gate.Exit("1.2.3.4");
        Assert.True(gate.TryEnter("1.2.3.4"));
    }

    [Fact]
    public void A_live_cap_applies_to_the_next_connection()
    {
        var max = 4;
        var gate = new IpConnectionGate(() => max);
        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("1.2.3.4"));

        max = 2;
        Assert.False(gate.TryEnter("1.2.3.4")); // already at the new cap
        gate.Exit("1.2.3.4");
        Assert.True(gate.TryEnter("1.2.3.4")); // back under it
    }

    [Fact]
    public void Connections_admitted_while_the_cap_was_off_still_count_when_it_comes_on()
    {
        // The reason the gate counts even while disabled. Skipping the bookkeeping would leave these
        // three uncounted, so turning the cap on would admit three MORE before refusing — and the Exits
        // would then drive the count negative-ish, leaving the IP permanently mis-tracked.
        var max = 0;
        var gate = new IpConnectionGate(() => max);
        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("1.2.3.4"));
        Assert.True(gate.TryEnter("1.2.3.4"));

        max = 3;
        Assert.False(gate.TryEnter("1.2.3.4"));

        gate.Exit("1.2.3.4");
        gate.Exit("1.2.3.4");
        gate.Exit("1.2.3.4");
        Assert.Equal(0, gate.TrackedIps); // and nothing is left behind
    }
}
