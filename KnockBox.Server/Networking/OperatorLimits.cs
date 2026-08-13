namespace KnockBox.Server.Networking;

/// <summary>
/// An operator's overrides of the runtime-editable half of <see cref="ServerLimits"/>, persisted with
/// the rest of admin policy. Every member is <b>nullable, and null means "use the default"</b>
/// — the same record-by-absence discipline availability and update policy already use, so the settings
/// file holds only what was actually changed and "revert to the default" is a removal rather than a
/// second way to say the same number.
/// </summary>
/// <remarks>
/// <para><b>Only part of <see cref="ServerLimits"/> is here, on purpose.</b> Three knobs are deliberately
/// startup-only:</para>
/// <list type="bullet">
/// <item><c>HandshakeTimeout</c> and <c>DisconnectGrace</c> — the reconnect reaper's timer interval is
/// derived from the grace window when the host starts, and whether the timer exists at all depends on it
/// being non-zero. A live edit would half-apply, which is worse than not offering it.</item>
/// <item>Both <c>AdminLoginAttemptsPerMinute</c> caps — those bound PBKDF2 CPU for an unauthenticated
/// caller. A lock that can be unlocked from inside the room it protects is not a lock; they stay in
/// configuration, where changing them needs host access.</item>
/// </list>
/// </remarks>
public sealed record OperatorLimits(
    double? GameMessagesPerSecond = null,
    double? GameMessagesBurst = null,
    double? ControlMessagesPerSecond = null,
    double? ControlMessagesBurst = null,
    int? LobbyCreatesPerMinute = null,
    int? MaxConnectionsPerIp = null,
    int? MaxLobbies = null,
    int? MaxLobbiesPerGame = null)
{
    /// <summary>No overrides at all — the defaults stand. What a fresh deployment has.</summary>
    public static readonly OperatorLimits None = new();

    /// <summary>True when nothing is overridden, so the settings file can omit the object entirely.</summary>
    /// <remarks>
    /// <c>[JsonIgnore]</c> because this record IS the persisted shape: a computed property would otherwise be
    /// written into a file an operator is invited to hand-edit, as a field that looks settable and isn't.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => this == None;

    /// <summary>Lays these overrides over the defaults, leaving every unset member alone.</summary>
    public ServerLimits ApplyTo(ServerLimits configured) => configured with
    {
        GameMessagesPerSecond = GameMessagesPerSecond ?? configured.GameMessagesPerSecond,
        GameMessagesBurst = GameMessagesBurst ?? configured.GameMessagesBurst,
        ControlMessagesPerSecond = ControlMessagesPerSecond ?? configured.ControlMessagesPerSecond,
        ControlMessagesBurst = ControlMessagesBurst ?? configured.ControlMessagesBurst,
        LobbyCreatesPerMinute = LobbyCreatesPerMinute ?? configured.LobbyCreatesPerMinute,
        MaxConnectionsPerIp = MaxConnectionsPerIp ?? configured.MaxConnectionsPerIp,
        MaxLobbies = MaxLobbies ?? configured.MaxLobbies,
        MaxLobbiesPerGame = MaxLobbiesPerGame ?? configured.MaxLobbiesPerGame,
    };

    /// <summary>
    /// Why these overrides can't be accepted, or null when they can. The admin API refuses on this;
    /// <c>AdminSettingsStore</c> uses it to drop a hand-edited object that would lock players out rather
    /// than honour it.
    /// </summary>
    /// <param name="configured">The baseline these overrides will be laid over. Needed because the
    /// dangerous combinations are properties of the MERGED limits: setting only a burst to zero against
    /// a configured non-zero rate is exactly as fatal as setting both.</param>
    /// <remarks>
    /// The ceilings are absurdly high on purpose — they exist to catch a fat-fingered extra digit and a
    /// negative, not to second-guess an operator sizing their own host. The one rule with teeth is the
    /// burst floor: a rate above zero with a burst below one refuses <em>every</em> message forever,
    /// which for the control plane means nobody can create or join a lobby again until someone edits the
    /// settings file by hand. That is a self-inflicted outage, not a policy, so it is refused.
    /// </remarks>
    public string? Validate(ServerLimits configured)
    {
        if (Range(GameMessagesPerSecond, 0, 100_000) is { } a) return $"gameMessagesPerSecond {a}";
        if (Range(GameMessagesBurst, 0, 100_000) is { } b) return $"gameMessagesBurst {b}";
        if (Range(ControlMessagesPerSecond, 0, 100_000) is { } c) return $"controlMessagesPerSecond {c}";
        if (Range(ControlMessagesBurst, 0, 100_000) is { } d) return $"controlMessagesBurst {d}";
        if (Range(LobbyCreatesPerMinute, 0, 100_000) is { } e) return $"lobbyCreatesPerMinute {e}";
        if (Range(MaxConnectionsPerIp, 0, 65_535) is { } f) return $"maxConnectionsPerIp {f}";
        if (Range(MaxLobbies, 0, 1_000_000) is { } g) return $"maxLobbies {g}";
        if (Range(MaxLobbiesPerGame, 0, 1_000_000) is { } h) return $"maxLobbiesPerGame {h}";

        var merged = ApplyTo(configured);
        if (Starved(merged.GameMessagesPerSecond, merged.GameMessagesBurst))
            return "gameMessagesBurst must be at least 1 when gameMessagesPerSecond is above 0, " +
                   "or every game message is refused.";
        if (Starved(merged.ControlMessagesPerSecond, merged.ControlMessagesBurst))
            return "controlMessagesBurst must be at least 1 when controlMessagesPerSecond is above 0, " +
                   "or every lobby operation is refused.";
        return null;

        static string? Range(double? value, double min, double max) => value switch
        {
            null => null,
            { } v when double.IsNaN(v) || double.IsInfinity(v) => "must be a number.",
            { } v when v < min || v > max => $"must be between {min} and {max}.",
            _ => null,
        };

        static bool Starved(double rate, double burst) => rate > 0 && burst < 1;
    }
}
