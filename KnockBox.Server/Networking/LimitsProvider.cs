namespace KnockBox.Server.Networking;

/// <summary>
/// The limits in force right now: the configured baseline with the operator's overrides laid over it.
/// Everything on the connection paths reads <see cref="Current"/> instead of holding a
/// <see cref="ServerLimits"/> of its own, so an edit in the admin portal applies without a restart —
/// including to sockets that are already open.
/// </summary>
/// <remarks>
/// <para>Reads are lock-free: an immutable record is swapped in a single reference write, the same
/// discipline <c>GameCatalog</c>, <c>AdminSettingsStore</c> and <c>GameLifecycleGate</c> use, because
/// <see cref="Current"/> is read once per inbound frame.</para>
/// <para><b>The merge lives here rather than at the call sites.</b> Both startup and the admin endpoint
/// call <see cref="Apply"/> with whatever the settings store holds, so there is one place that decides
/// what "configured plus overrides" means. Two callers each doing their own <c>with</c> expression is how
/// the portal and the next restart end up disagreeing about the effective limit.</para>
/// </remarks>
public sealed class LimitsProvider
{
    private volatile ServerLimits _current;
    private volatile OperatorLimits _overrides = OperatorLimits.None;

    public LimitsProvider(ServerLimits configured)
    {
        Configured = configured;
        _current = configured;
    }

    /// <summary>The limits from configuration, untouched: the <b>defaults</b>, which is what the portal calls
    /// them beside each field — an operator reading a value they never set calls it a default, not a
    /// "configured value". What "revert" reverts to.</summary>
    public ServerLimits Configured { get; }

    /// <summary>The limits in force. Read on every inbound frame; never cache it.</summary>
    public ServerLimits Current => _current;

    /// <summary>What the operator has overridden, for reporting. <see cref="OperatorLimits.None"/> on a
    /// deployment that has never touched them.</summary>
    public OperatorLimits Overrides => _overrides;

    /// <summary>Publishes a new set of overrides over the configured baseline. Null clears them.</summary>
    public void Apply(OperatorLimits? overrides)
    {
        var next = overrides ?? OperatorLimits.None;
        // Order matters only for a reader that inspects both, and no such reader exists on a hot path:
        // publish the effective limits first so nothing ever sees overrides that aren't in force yet.
        _current = next.ApplyTo(Configured);
        _overrides = next;
    }
}
