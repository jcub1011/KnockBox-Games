using System.Net;
using System.Net.Sockets;

namespace KnockBox.Server.Webhooks;

/// <summary>
/// Decides whether an address is one this server will refuse to POST a webhook to.
///
/// A webhook URL is chosen by whoever holds the admin session, and the portal's "send test" button
/// awaits the delivery and reports the upstream status — which makes an unrestricted target list two
/// things at once: a port scanner for the network the server sits in, and a blind POST at any service
/// on it. Neither is an escalation <em>inside</em> KnockBox (that operator can already install a package
/// that runs code in this process); the value is pivoting OUTWARD, from a host other machines trust by
/// address to services the caller cannot dial themselves. The classic target is a cloud instance's
/// metadata endpoint at 169.254.169.254, which authenticates nothing it can reach.
/// </summary>
/// <remarks>
/// <para>Pure and address-based, deliberately: the check that matters runs at CONNECT time on the
/// address actually dialled (see <c>MarketplaceClient.CreateHttpClient</c>'s connect callback), because
/// a rule applied to the URL string is defeated by a hostname that resolves public once and loopback a
/// moment later, and by a redirect to somewhere else entirely.</para>
/// <para>Refusing is the default rather than the option because the endpoints this feature exists for —
/// Discord, Slack, PagerDuty — are all on the public internet. A deployment whose notifier really is on
/// the LAN sets <c>KnockBox:WebhookAllowPrivateTargets</c>.</para>
/// </remarks>
public static class PrivateAddressGuard
{
    /// <summary>The knob that turns this off, named in every message so the refusal is actionable.</summary>
    public const string Knob = "KnockBox:WebhookAllowPrivateTargets";

    /// <summary>
    /// True when this address belongs to the host itself, the local network, or a link-local range that
    /// no third-party notification service could legitimately live on.
    /// </summary>
    public static bool IsBlocked(IPAddress address)
    {
        // An IPv6-mapped IPv4 address (::ffff:127.0.0.1) is the same destination wearing a different
        // family, so unwrap before judging — otherwise every rule below is one URL spelling away from
        // being skipped.
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] switch
            {
                10 => true,                              // 10.0.0.0/8
                127 => true,                             // covered by IsLoopback, kept for the whole /8
                169 when b[1] == 254 => true,            // 169.254.0.0/16 link-local — the metadata range
                172 when b[1] is >= 16 and <= 31 => true,// 172.16.0.0/12
                192 when b[1] == 168 => true,            // 192.168.0.0/16
                100 when b[1] is >= 64 and <= 127 => true,// 100.64.0.0/10 carrier-grade NAT
                0 => true,                               // 0.0.0.0/8 "this network"
                >= 224 => true,                          // multicast and reserved
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            // fc00::/7 unique-local. IsIPv6UniqueLocal exists only on newer surfaces than this targets
            // uniformly, and the test is one byte.
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }

    /// <summary>
    /// The refusal message for a blocked destination. One wording, so the connect-time refusal and the
    /// registration-time pre-check cannot describe the same rule differently.
    /// </summary>
    public static string Refusal(string host) =>
        $"'{host}' resolves to a loopback, link-local or private address. Webhooks are only delivered to " +
        $"public addresses; set {Knob}=true to allow internal endpoints.";
}
