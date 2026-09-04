namespace KnockBox.Server.Games.Blobs;

/// <summary>
/// The blob-store options in force right now: the configured baseline with the operator's overrides laid
/// over it. The sibling of <see cref="AuthorityOptionsProvider"/> and
/// <see cref="Networking.LimitsProvider"/>, and it exists for the same reason — so an edit in the admin
/// portal applies without a restart.
/// </summary>
/// <remarks>
/// <para>Reads are lock-free: an immutable record swapped in a single reference write, the same
/// discipline as its two siblings, <c>GameCatalog</c> and <c>AdminSettingsStore</c>.</para>
/// <para><b>Writes take a lock, and that is the one divergence from both siblings.</b> They each have a
/// single setter, so a lone reference write is the whole publish. This provider has <em>two</em>
/// independent override sources that compose into one effective record — the flat
/// <see cref="OperatorBlobOptions"/> and the per-game quota map, which the admin portal edits on two
/// different tabs. Without the lock, an <see cref="Apply"/> and an
/// <see cref="ApplyPerGameQuotas"/> racing each other both read the other's field, both merge, and the
/// later write silently discards the earlier change. That would present as "I set the quota for one game
/// and it went back to the default a moment later", which is close to undiagnosable.</para>
/// <para><b>The merge lives here rather than at the call sites</b>, for the reason its siblings give:
/// startup rehydration and two admin endpoints all publish, so there is one place that decides what
/// "configured plus overrides" means. Two callers each writing their own <c>with</c> expression is how
/// the portal and the next restart end up disagreeing.</para>
/// </remarks>
public sealed class BlobOptionsProvider
{
    private static readonly IReadOnlyDictionary<string, long> NoPerGameQuotas =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private volatile BlobOptions _current;
    private volatile OperatorBlobOptions _overrides = OperatorBlobOptions.None;

    // Null rather than an empty map when untouched, so the common case carries no dictionary at all and
    // BlobOptions.LobbyQuotaFor short-circuits on a null check instead of a lookup.
    private volatile IReadOnlyDictionary<string, long>? _perGameQuotas;

    public BlobOptionsProvider(BlobOptions configured)
    {
        Configured = configured;
        _current = configured;
    }

    /// <summary>The options from configuration, untouched: the <b>defaults</b>, which is what the portal
    /// calls them beside each field. What "revert" reverts to.</summary>
    public BlobOptions Configured { get; }

    /// <summary>The options in force. Read on every upload and every sweep; never cache it.</summary>
    public BlobOptions Current => _current;

    /// <summary>What the operator has overridden, for reporting.
    /// <see cref="OperatorBlobOptions.None"/> on a deployment that has never touched them.</summary>
    public OperatorBlobOptions Overrides => _overrides;

    /// <summary>The per-game per-lobby quota overrides, for reporting. Empty when none are set.</summary>
    public IReadOnlyDictionary<string, long> PerGameQuotas => _perGameQuotas ?? NoPerGameQuotas;

    /// <summary>Publishes a new set of flat overrides over the configured baseline. Null clears them.</summary>
    public void Apply(OperatorBlobOptions? overrides)
    {
        var next = overrides ?? OperatorBlobOptions.None;
        lock (_gate)
        {
            // Publish the effective options first, so nothing ever sees overrides that aren't in force yet.
            _current = Merge(next, _perGameQuotas);
            _overrides = next;
        }
    }

    /// <summary>
    /// Publishes the per-game per-lobby quota map. Null or empty clears it, so a deployment that has
    /// never set one carries no dictionary.
    /// </summary>
    public void ApplyPerGameQuotas(IReadOnlyDictionary<string, long>? quotas)
    {
        var next = quotas is { Count: > 0 } ? quotas : null;
        lock (_gate)
        {
            _current = Merge(_overrides, next);
            _perGameQuotas = next;
        }
    }

    private BlobOptions Merge(
        OperatorBlobOptions overrides, IReadOnlyDictionary<string, long>? perGameQuotas) =>
        overrides.ApplyTo(Configured) with { LobbyQuotaBytesByGame = perGameQuotas };
}
