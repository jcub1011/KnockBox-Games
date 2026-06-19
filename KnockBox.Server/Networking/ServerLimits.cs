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
    TimeSpan DisconnectGrace)
{
    public static ServerLimits FromConfiguration(IConfiguration config) => new(
        TimeSpan.FromSeconds(config.GetValue("KnockBox:HandshakeTimeoutSeconds", 10)),
        config.GetValue("KnockBox:GameMessagesPerSecond", 30.0),
        config.GetValue("KnockBox:GameMessagesBurst", 60.0),
        config.GetValue("KnockBox:ControlMessagesPerSecond", 5.0),
        config.GetValue("KnockBox:ControlMessagesBurst", 10.0),
        config.GetValue("KnockBox:LobbyCreatesPerMinute", 10),
        config.GetValue("KnockBox:MaxConnectionsPerIp", 32),
        TimeSpan.FromSeconds(config.GetValue("KnockBox:DisconnectGraceSeconds", 60)));
}
