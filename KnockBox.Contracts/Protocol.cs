namespace KnockBox.Contracts;

/// <summary>
/// Wire-protocol versioning. The SDKs (web/knockbox.js, the Godot addon) are copied into games and
/// can outlive server upgrades, so the first frame of each role (<see cref="HelloMessage"/> /
/// <see cref="AttachMessage"/>) declares the version it speaks. The server accepts anything up to
/// <see cref="Version"/> (a missing field — pre-versioning clients — reads as 0 and is treated as
/// version 1) and terminally rejects anything newer, so an old server fails a new SDK loudly
/// instead of misrouting it. <see cref="WelcomeMessage"/>/<see cref="ReadyMessage"/> echo the
/// server's version so an SDK can warn when it is the older side.
/// </summary>
public static class KnockBoxProtocol
{
    public const int Version = 1;
}
