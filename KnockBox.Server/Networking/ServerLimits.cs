namespace KnockBox.Server.Networking;

/// <summary>
/// Abuse-protection knobs for a public-facing server (<c>KnockBox:*</c> config). Every limit can be
/// disabled with <c>0</c>; the defaults are sized for casual party games (a host broadcasting state
/// ~20×/s stays well under <see cref="GameMessagesPerSecond"/>) while stopping a hostile client from
/// squatting sockets, spamming the relay (each game frame fans out O(lobby size)), or churning lobby
/// codes.
/// </summary>
public sealed record ServerLimits(
    TimeSpan HandshakeTimeout,
    double GameMessagesPerSecond,
    double GameMessagesBurst,
    double ControlMessagesPerSecond,
    double ControlMessagesBurst,
    int LobbyCreatesPerMinute,
    int MaxConnectionsPerIp,
    // Grace window a member is kept in their lobby after their shell socket drops, so a tab refresh
    // or brief network blip doesn't kick them out. 0 disables grace (immediate removal on drop).
    TimeSpan DisconnectGrace,
    // Per-IP cap on admin password attempts. Unlike the limits above this guards CPU rather than
    // bandwidth: each attempt runs a 600k-iteration PBKDF2 (~0.4s of one core), so an unthrottled
    // endpoint lets an unauthenticated caller both guess passwords and starve the game relay.
    int AdminLoginAttemptsPerMinute,
    // Cap on admin password attempts across ALL callers. The per-IP limit above is only as trustworthy
    // as the IP it keys on, and behind a proxy that IP comes from X-Forwarded-For — a header the client
    // writes. An attacker rotating it gets a fresh per-IP budget per request, which turns the per-IP cap
    // into no cap at all for the one thing that actually costs the server: PBKDF2. This bound holds
    // regardless of what any caller claims to be, so it is the CPU ceiling; the per-IP cap remains what
    // keeps one bad actor from spending everyone else's share of it.
    int AdminLoginAttemptsPerMinuteGlobal)
{
    public static ServerLimits FromConfiguration(IConfiguration config) => new(
        TimeSpan.FromSeconds(config.GetValue("KnockBox:HandshakeTimeoutSeconds", 10)),
        config.GetValue("KnockBox:GameMessagesPerSecond", 30.0),
        config.GetValue("KnockBox:GameMessagesBurst", 60.0),
        config.GetValue("KnockBox:ControlMessagesPerSecond", 5.0),
        config.GetValue("KnockBox:ControlMessagesBurst", 10.0),
        config.GetValue("KnockBox:LobbyCreatesPerMinute", 10),
        config.GetValue("KnockBox:MaxConnectionsPerIp", 32),
        TimeSpan.FromSeconds(config.GetValue("KnockBox:DisconnectGraceSeconds", 60)),
        config.GetValue("KnockBox:AdminLoginAttemptsPerMinute", 10),
        // 60/min ≈ one hash per second ≈ 40% of one core spent on PBKDF2 at the very worst — enough
        // headroom that a room full of operators never notices, low enough that the relay keeps running.
        config.GetValue("KnockBox:AdminLoginAttemptsPerMinuteGlobal", 60));
}
