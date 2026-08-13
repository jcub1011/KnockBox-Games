namespace KnockBox.Server.Admin;

/// <summary>
/// The operator policy questions the lobby/catalog paths ask. Narrow on purpose: the relay has no
/// business knowing about settings files, sessions or availability enums — only whether this game may
/// be started and whether players should see it. <see cref="AdminSettingsStore"/> is the real
/// implementation.
/// </summary>
/// <remarks>
/// Implementations are called on request threads (once per lobby create, once per catalog listing), so
/// they must be cheap and lock-free to read.
/// </remarks>
public interface IPlatformPolicy
{
    /// <summary>Whether new lobby creation is blocked platform-wide. Running sessions are unaffected.</summary>
    bool MaintenanceMode { get; }

    /// <summary>Operator-supplied reason to show a player refused during maintenance, if any.</summary>
    string? MaintenanceMessage { get; }

    /// <summary>Whether players may start a new lobby for this game right now.</summary>
    bool CanCreateLobby(string gameId);

    /// <summary>Whether this game appears in the catalog players browse.</summary>
    bool IsListed(string gameId);

    /// <summary>
    /// Why this game can't be started right now, when there is something specific to say — e.g. that it
    /// is mid-update. Null means "no reason beyond the generic one", which is the answer whenever
    /// <see cref="CanCreateLobby"/> is true.
    /// </summary>
    /// <remarks>
    /// This exists so a game being updated stays LISTED and refuses with an explanation, rather than
    /// vanishing from the grid and reappearing a minute later — which reads as a broken platform. It is
    /// deliberately shaped like <see cref="MaintenanceMessage"/>: a string the relay passes through
    /// without interpreting, so <c>WebSocketHandler</c> still knows nothing about updates or packages.
    /// </remarks>
    string? UnavailableReason(string gameId);
}

/// <summary>Policy implementations that aren't backed by operator settings.</summary>
public static class PlatformPolicy
{
    /// <summary>
    /// Everything allowed and listed — the behaviour of this server before the admin portal existed.
    /// Used by tests that exercise the lobby/relay flows and have no interest in operator policy.
    /// </summary>
    public static IPlatformPolicy OpenPlatform { get; } = new AllowAll();

    private sealed class AllowAll : IPlatformPolicy
    {
        public bool MaintenanceMode => false;
        public string? MaintenanceMessage => null;
        public bool CanCreateLobby(string gameId) => true;
        public bool IsListed(string gameId) => true;
        public string? UnavailableReason(string gameId) => null;
    }
}
