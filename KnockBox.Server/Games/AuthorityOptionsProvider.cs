namespace KnockBox.Server.Games;

/// <summary>
/// The server-authority options in force right now: the configured baseline with the operator's overrides
/// laid over it. The sibling of <see cref="Networking.LimitsProvider"/>, and it exists for the same reason —
/// so an edit in the admin portal applies without a restart.
/// </summary>
/// <remarks>
/// <para>Only two members of <see cref="AuthorityOptions"/> are actually read through here:
/// <see cref="AuthorityOptions.MaxLobbies"/> (checked in <c>ServerAuthorityManager.TryStart</c>) and
/// <see cref="AuthorityOptions.ModuleCacheIdle"/> (read by the module-cache sweep). Everything else is a
/// per-engine construction value or a discovery-time cap and keeps being read from the captured record —
/// see the remarks on <see cref="OperatorAuthorityOptions"/> for which and why.</para>
/// <para>Reads are lock-free: an immutable record swapped in a single reference write, the same discipline
/// as <c>LimitsProvider</c>, <c>GameCatalog</c> and <c>AdminSettingsStore</c>.</para>
/// <para><b>The merge lives here rather than at the call sites</b>, for the reason its sibling gives:
/// startup rehydration and the admin endpoint both call <see cref="Apply"/>, so there is one place that
/// decides what "configured plus overrides" means. Two callers each writing their own <c>with</c>
/// expression is how the portal and the next restart end up disagreeing.</para>
/// </remarks>
public sealed class AuthorityOptionsProvider
{
    private volatile AuthorityOptions _current;
    private volatile OperatorAuthorityOptions _overrides = OperatorAuthorityOptions.None;

    public AuthorityOptionsProvider(AuthorityOptions configured)
    {
        Configured = configured;
        _current = configured;
    }

    /// <summary>The options from configuration, untouched: the <b>defaults</b>, which is what the portal
    /// calls them beside each field. What "revert" reverts to.</summary>
    public AuthorityOptions Configured { get; }

    /// <summary>The options in force. Read when a lobby is created and on every cache sweep; never cache it.</summary>
    public AuthorityOptions Current => _current;

    /// <summary>What the operator has overridden, for reporting.
    /// <see cref="OperatorAuthorityOptions.None"/> on a deployment that has never touched them.</summary>
    public OperatorAuthorityOptions Overrides => _overrides;

    /// <summary>Publishes a new set of overrides over the configured baseline. Null clears them.</summary>
    public void Apply(OperatorAuthorityOptions? overrides)
    {
        var next = overrides ?? OperatorAuthorityOptions.None;
        // Publish the effective options first, so nothing ever sees overrides that aren't in force yet.
        _current = next.ApplyTo(Configured);
        _overrides = next;
    }
}
