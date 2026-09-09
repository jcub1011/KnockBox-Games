namespace KnockBox.Server.Games.Blobs;

/// <summary>
/// Knobs for the blob-share store (<c>KnockBox:Blob*</c> config). Games upload shared media here
/// because it cannot cross the relay: the frame cap is a non-configurable 512 KiB and an oversize frame
/// closes the socket, so a 20 MB battlemap has no route through <c>/ws</c> at all.
/// </summary>
/// <remarks>
/// <para><b>Every non-positive cap disables that individual check</b>, the convention
/// <see cref="GamePackageLimits"/> and <see cref="Networking.ServerLimits"/> both follow. It has to be
/// honoured at <em>every</em> enforcement point, not just the obvious one: <c>MaxPackageBytes=0</c> is
/// documented as "no limit" and was read literally in two places, where it set a 4096-byte Kestrel cap
/// and refused every upload while complaining about a 0-byte limit. The same trap exists here three
/// times over — the per-blob cap, the per-lobby quota and the server-wide quota.</para>
/// <para>The two quotas are the difference between a bounded feature and a disk-fill vector.
/// <see cref="Words.AuthorityWordService"/> is the cautionary example: it bounds
/// <c>MaxWordFileBytes</c> per file and sums nothing, so N games × cap really is unbounded there. A
/// per-item cap alone is not a quota.</para>
/// <para><see cref="Enabled"/>, <see cref="Root"/> and <see cref="SweepInterval"/> are startup-only and
/// the portal says so; the caps and <see cref="Grace"/> are read live through
/// <see cref="BlobOptionsProvider"/> so an operator can raise a quota without a
/// restart. <see cref="LobbyQuotaBytesByGame"/> is the per-game override of
/// <see cref="LobbyQuotaBytes"/>, and unlike the rest it lives in persisted operator policy
/// (<c>AdminSettings.BlobQuotas</c>) rather than configuration — a map keyed by game id is a thing an
/// operator edits per title, not a deployment constant.</para>
/// </remarks>
public sealed record BlobOptions(
    // Master switch. When false the store is still registered and still answers, refusing every write
    // with a reason the game can show — the house rule that a disabled feature is a refusal with an
    // explanation, never a route that is not there.
    bool Enabled,
    // Where content lives. Resolved by ContentPaths, so this carries the resolved absolute path rather
    // than the raw config value.
    string Root,
    // Largest single blob. Enforced against BYTES WRITTEN, never Content-Length — that header is the
    // client's claim, and a chunked request has none at all.
    long MaxBlobBytes,
    // Aggregate a single lobby's registrations may reference. Charged per DISTINCT hash: a lobby that
    // registers one file under two logical ids is charged once, because the quota is about disk and the
    // file on disk is one file.
    long LobbyQuotaBytes,
    // Aggregate across the whole server, summed over distinct content. THE cap that makes this feature
    // bounded; without it the per-lobby number is just N × cap.
    long TotalQuotaBytes,
    // How long freshly uploaded bytes are protected before any handle references them. Bytes land before
    // register is called, so refcount is 0 for that window and a sweep would otherwise delete what an
    // upload is still producing.
    TimeSpan Grace,
    // Backstop sweep cadence (0 = no sweeper), and STARTUP-ONLY: a cadence is fixed for the process
    // while its window (Grace, above) is read live, which is the house rule DisconnectGraceSeconds
    // exists to illustrate. The refcount is the mechanism; this only catches what it cannot — an
    // abandoned upload's provisional entry, and a delete that failed because the file was still open.
    TimeSpan SweepInterval,
    // Concurrent uploads one lobby may have in flight. Bounds the abandoned-PUT hole: the grace window
    // is claimed before the first byte, keyed by the hash in the URL, so a client that opens a PUT and
    // sends nothing leaves a grace-protected entry behind. Bounded and self-expiring, but without this
    // it could be used to churn the table.
    int MaxUploadsPerLobby,
    // Per-game override of LobbyQuotaBytes, keyed by game id. Null when no operator has set one, which
    // is the common case; laid on by BlobOptionsProvider.Apply from persisted policy.
    IReadOnlyDictionary<string, long>? LobbyQuotaBytesByGame = null)
{
    public const long DefaultMaxBlobBytes = 100L * 1024 * 1024;
    public const long DefaultLobbyQuotaBytes = 1024L * 1024 * 1024;
    public const long DefaultTotalQuotaBytes = 20L * 1024 * 1024 * 1024;
    public const int DefaultGraceMinutes = 5;
    public const int DefaultSweepSeconds = 300;
    public const int DefaultMaxUploadsPerLobby = 4;

    /// <summary>Sensible values for every knob but <see cref="Root"/>, which only
    /// <see cref="Hosting.ContentPaths"/> can supply. Exists so <see cref="FromConfiguration"/> and the
    /// tests never repeat a literal.</summary>
    public static BlobOptions Default { get; } = new(
        Enabled: true,
        Root: "blobs",
        MaxBlobBytes: DefaultMaxBlobBytes,
        LobbyQuotaBytes: DefaultLobbyQuotaBytes,
        TotalQuotaBytes: DefaultTotalQuotaBytes,
        Grace: TimeSpan.FromMinutes(DefaultGraceMinutes),
        SweepInterval: TimeSpan.FromSeconds(DefaultSweepSeconds),
        MaxUploadsPerLobby: DefaultMaxUploadsPerLobby);

    /// <summary>
    /// The per-lobby ceiling for a lobby of <paramref name="gameId"/>: that game's operator override when
    /// one is set, otherwise the server-wide <see cref="LobbyQuotaBytes"/>.
    /// </summary>
    /// <remarks>
    /// A <b>negative</b> override is honoured as "no per-lobby cap for this game", the same
    /// non-positive-disables convention every other cap here follows — but <b>zero is not</b>, because a
    /// map with an explicit <c>0</c> reads far more like "this game may store nothing" than like
    /// "unlimited", and an operator who typed 0 into a quota field did not mean to remove the quota.
    /// This is the one place the convention is deliberately narrowed, and the asymmetry is worth the
    /// surprise it avoids.
    /// </remarks>
    public long LobbyQuotaFor(string gameId) =>
        LobbyQuotaBytesByGame is { } map && map.TryGetValue(gameId, out var bytes)
            ? bytes
            : LobbyQuotaBytes;

    /// <summary>
    /// Reads the configured knobs. <paramref name="root"/> comes from <see cref="Hosting.ContentPaths"/>
    /// rather than being re-read here, so there is exactly one place that resolves a content root.
    /// </summary>
    public static BlobOptions FromConfiguration(IConfiguration config, string root) => new(
        config.GetValue("KnockBox:BlobsEnabled", Default.Enabled),
        root,
        config.GetValue("KnockBox:BlobMaxBytes", DefaultMaxBlobBytes),
        config.GetValue("KnockBox:BlobLobbyQuotaBytes", DefaultLobbyQuotaBytes),
        config.GetValue("KnockBox:BlobTotalQuotaBytes", DefaultTotalQuotaBytes),
        TimeSpan.FromMinutes(config.GetValue("KnockBox:BlobGraceMinutes", DefaultGraceMinutes)),
        TimeSpan.FromSeconds(config.GetValue("KnockBox:BlobSweepSeconds", DefaultSweepSeconds)),
        config.GetValue("KnockBox:BlobMaxUploadsPerLobby", DefaultMaxUploadsPerLobby));
}
