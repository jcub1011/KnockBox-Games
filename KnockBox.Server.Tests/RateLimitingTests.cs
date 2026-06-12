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
}
