using System.Net;
using KnockBox.Server.Webhooks;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Which destinations a webhook may be delivered to. The portal's "send test" button awaits the delivery
/// and reports the upstream status, so an unrestricted target list is a port scanner for the network the
/// server sits in — usable by anyone holding an admin session, against services that trust this host by
/// address and that the caller cannot reach themselves.
/// </summary>
public class PrivateAddressGuardTests
{
    [Theory]
    // Loopback, in both families and both spellings.
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    // The unspecified address, which routes to the local host.
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("0.10.0.1")]
    // RFC1918.
    [InlineData("10.0.0.7")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    // Carrier-grade NAT.
    [InlineData("100.64.0.1")]
    // Link-local — 169.254.169.254 is the cloud metadata endpoint, which authenticates nothing that can
    // reach it and is the single highest-value target this rule exists for.
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    // Unique-local (fc00::/7) and multicast.
    [InlineData("fd00::1")]
    [InlineData("fc00::1")]
    [InlineData("239.1.1.1")]
    public void Blocks_addresses_a_notification_service_could_not_live_on(string address) =>
        Assert.True(PrivateAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("140.82.121.4")]        // github.com
    [InlineData("162.159.128.233")]     // discord.com
    // Deliberately NOT blocked: adjacent to a blocked range but public.
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.253.0.1")]
    [InlineData("192.167.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("2606:4700::1111")]
    public void Allows_the_public_internet(string address) =>
        Assert.False(PrivateAddressGuard.IsBlocked(IPAddress.Parse(address)));

    [Fact]
    public void An_IPv4_mapped_private_address_is_still_private()
    {
        // The same destination wearing a different family. Judging it unwrapped is what stops every rule
        // above being one URL spelling away from being skipped.
        Assert.True(PrivateAddressGuard.IsBlocked(IPAddress.Parse("::ffff:10.0.0.7")));
        Assert.True(PrivateAddressGuard.IsBlocked(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.False(PrivateAddressGuard.IsBlocked(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Fact]
    public void The_refusal_names_the_knob_that_lifts_it()
    {
        // A deployment whose notifier really is on the LAN has to be able to find the setting from the
        // error alone — it is the only thing they will see.
        var refusal = PrivateAddressGuard.Refusal("agent.internal");
        Assert.Contains("agent.internal", refusal, StringComparison.Ordinal);
        Assert.Contains("KnockBox:WebhookAllowPrivateTargets", refusal, StringComparison.Ordinal);
    }
}
