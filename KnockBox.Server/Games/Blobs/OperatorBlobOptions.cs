using System.Text.Json.Serialization;

namespace KnockBox.Server.Games.Blobs;

/// <summary>
/// An operator's overrides of the runtime-editable half of <see cref="BlobOptions"/>, edited from the
/// admin portal and persisted to <c>admin-settings.json</c>.
/// </summary>
/// <remarks>
/// <para>The sibling of <see cref="OperatorAuthorityOptions"/> and
/// <see cref="Networking.OperatorLimits"/> in every respect: every member is nullable so the settings
/// file records only what an operator actually changed, and <see cref="ApplyTo"/> lays them over the
/// configured baseline. A SEPARATE record rather than more fields on <c>OperatorLimits</c>, for the
/// reason that type's sibling already establishes — <c>OperatorLimits</c>' whole contract is
/// <c>ApplyTo(ServerLimits)</c>, and members it silently ignored would make its central invariant a
/// lie. Blob quotas are not relay limits; they are not checked per frame and they are not read by
/// <c>TokenBucket</c>.</para>
/// <para>Three knobs are missing, and each omission is deliberate. <see cref="BlobOptions.Root"/> is a
/// mount an operator arranged before the process started and a resolved
/// <see cref="Hosting.ContentPaths"/> member; switching it at runtime would orphan everything already
/// written and leave the startup sweep pointed at the wrong directory. <see cref="BlobOptions.Enabled"/>
/// gates DI wiring and the bootstrap directory creation, so an "edit" would apply only to a restart — a
/// knob that lies about when it takes effect. <see cref="BlobOptions.SweepInterval"/> is the sweep
/// <em>cadence</em>, and the house rule is that a cadence is fixed while its window is read live —
/// deriving the interval from the window is the trap that forced <c>DisconnectGraceSeconds</c> to stay
/// startup-only. <see cref="GraceMinutes"/> is the window here, and it <em>is</em> editable. The portal
/// reports all three read-only.</para>
/// <para>Minutes on the wire, <see cref="TimeSpan"/> in the record: the portal edits a number and the
/// settings file is offered for hand-editing, so the persisted unit must be a plain integer — but every
/// consumer wants a duration. <see cref="ApplyTo"/> is where that conversion lives.</para>
/// <para><see cref="BlobOptions.LobbyQuotaBytesByGame"/> is not here either, and that one is a
/// structural choice rather than a "cannot". Per-game quotas are keyed by game id, which puts them with
/// game availability and update policy in <c>AdminSettings</c>' per-game dictionaries rather than in a
/// flat overrides record — the admin portal edits them on the Games tab, next to the other two, and the
/// provider lays them on in <see cref="BlobOptionsProvider.ApplyPerGameQuotas"/>.</para>
/// </remarks>
public sealed record OperatorBlobOptions(
    long? MaxBlobBytes = null,
    long? LobbyQuotaBytes = null,
    long? TotalQuotaBytes = null,
    int? GraceMinutes = null,
    int? MaxUploadsPerLobby = null)
{
    /// <summary>No overrides at all — the configured values stand. What a fresh deployment has.</summary>
    public static readonly OperatorBlobOptions None = new();

    /// <summary>True when nothing is overridden, so the settings file can omit the object entirely.</summary>
    /// <remarks>
    /// <c>[JsonIgnore]</c> because this record IS the persisted shape: a computed property would otherwise
    /// be written into a file an operator is invited to hand-edit, as a field that looks settable and isn't.
    /// </remarks>
    [JsonIgnore]
    public bool IsEmpty => this == None;

    /// <summary>Lays these overrides over the configured options, leaving every unset member alone.</summary>
    public BlobOptions ApplyTo(BlobOptions configured) => configured with
    {
        MaxBlobBytes = MaxBlobBytes ?? configured.MaxBlobBytes,
        LobbyQuotaBytes = LobbyQuotaBytes ?? configured.LobbyQuotaBytes,
        TotalQuotaBytes = TotalQuotaBytes ?? configured.TotalQuotaBytes,
        Grace = GraceMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : configured.Grace,
        MaxUploadsPerLobby = MaxUploadsPerLobby ?? configured.MaxUploadsPerLobby,
    };

    /// <summary>
    /// Why these overrides can't be accepted, or null when they can. The admin API refuses on this;
    /// <c>AdminSettingsStore</c> uses it to drop a hand-edited object rather than honour it.
    /// </summary>
    /// <remarks>
    /// <para>Takes no baseline argument, like <see cref="OperatorAuthorityOptions.Validate"/> and unlike
    /// <see cref="Networking.OperatorLimits.Validate"/>: the dangerous combination there is a property of
    /// the MERGED limits, whereas every knob here is an absolute size with no relationship to the
    /// others. In particular a total quota smaller than the per-lobby one is <b>not</b> refused — it is a
    /// coherent policy ("one lobby may use everything, but only one at a time"), and refusing it would
    /// block an operator lowering the total before lowering the per-lobby number.</para>
    /// <para>The ceilings are absurdly high on purpose — they exist to catch a fat-fingered extra digit
    /// and a negative, not to second-guess an operator sizing their own host. 1 TiB is well past any
    /// plausible blob root and still a long way short of overflowing <see cref="long"/> when summed.</para>
    /// <para>Messages name the camelCase WIRE key, not the record member, because the portal surfaces
    /// them verbatim beside the field the operator is looking at.</para>
    /// </remarks>
    public string? Validate()
    {
        const long maxBytes = 1024L * 1024 * 1024 * 1024;   // 1 TiB

        if (Bytes(MaxBlobBytes, maxBytes) is { } a) return $"blobMaxBytes {a}";
        if (Bytes(LobbyQuotaBytes, maxBytes) is { } b) return $"blobLobbyQuotaBytes {b}";
        if (Bytes(TotalQuotaBytes, maxBytes) is { } c) return $"blobTotalQuotaBytes {c}";
        // Seven days. Past that a "grace window" is not one anybody is really drawing a distinction from
        // "forever", and it would pin an abandoned upload's slot for a week.
        if (GraceMinutes is < 0 or > 10_080)
            return "blobGraceMinutes must be between 0 and 10080 (0 = no grace window).";
        if (MaxUploadsPerLobby is < 0 or > 1_000)
            return "blobMaxUploadsPerLobby must be between 0 and 1000 (0 = unlimited).";
        return null;

        static string? Bytes(long? value, long max) => value switch
        {
            null => null,
            < 0 => "must not be negative (0 = no limit).",
            var v when v > max => $"must be at most {max} bytes (0 = no limit).",
            _ => null,
        };
    }
}
