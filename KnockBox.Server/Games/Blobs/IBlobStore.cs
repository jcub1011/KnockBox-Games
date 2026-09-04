namespace KnockBox.Server.Games.Blobs;

/// <summary>How an upload ended. Everything but <see cref="Stored"/> and <see cref="AlreadyPresent"/>
/// is a refusal the caller turns into a status code.</summary>
public enum BlobIngestOutcome
{
    /// <summary>The bytes were hashed, verified against the declared hash, and published.</summary>
    Stored,

    /// <summary>The server already held these bytes. No body was read — see <c>BlobApi</c> on
    /// <c>Expect: 100-continue</c>, which is what keeps the common case from transmitting them.</summary>
    AlreadyPresent,

    /// <summary>The store is switched off, or its root is not writable.</summary>
    Disabled,

    /// <summary>The declared hash is not 64 lowercase hex characters.</summary>
    HashRejected,

    /// <summary>The bytes hash to something other than what the URL declared. The one real attack this
    /// design admits, and the reason the hash is verified rather than trusted.</summary>
    HashMismatch,

    /// <summary>Past <c>BlobMaxBytes</c>, measured on bytes written.</summary>
    TooLarge,

    /// <summary>Past this lobby's per-lobby quota.</summary>
    LobbyQuotaExceeded,

    /// <summary>Past the server-wide quota.</summary>
    TotalQuotaExceeded,

    /// <summary>This lobby already has <c>BlobMaxUploadsPerLobby</c> uploads in flight.</summary>
    TooManyUploads,

    /// <summary>The disk refused the write. Distinct from the quota outcomes because it is the
    /// operator's problem, not the game's.</summary>
    WriteFailed,
}

/// <summary>The result of an upload, and the bytes it actually wrote.</summary>
/// <param name="Bytes">Bytes written, or the length already on disk for
/// <see cref="BlobIngestOutcome.AlreadyPresent"/>.</param>
/// <param name="Error">Operator- or game-facing reason, set on every refusal. Never a stack trace: this
/// string reaches a game, and through it a player.</param>
public readonly record struct BlobIngestResult(
    BlobIngestOutcome Outcome, long Bytes = 0, string? Error = null)
{
    public bool Success =>
        Outcome is BlobIngestOutcome.Stored or BlobIngestOutcome.AlreadyPresent;
}

/// <summary>How a registration ended.</summary>
public enum BlobRegisterOutcome
{
    /// <summary>A new handle now references the content.</summary>
    Registered,

    /// <summary>This exact <c>(lobby, logicalId, hash)</c> was already registered. A success, and
    /// deliberately <b>not</b> a second reference — see <c>BlobStore.Register</c> on idempotence.</summary>
    AlreadyRegistered,

    /// <summary>The store is switched off, or its root is not writable.</summary>
    Disabled,

    /// <summary>The hash is not 64 lowercase hex characters.</summary>
    HashRejected,

    /// <summary>No content with that hash is on disk, or it is still an upload in flight. Upload first.</summary>
    UnknownHash,

    /// <summary>The logical id is empty, too long, or carries a control character.</summary>
    LogicalIdRejected,

    /// <summary>Registering would put this lobby past its per-lobby quota.</summary>
    LobbyQuotaExceeded,
}

/// <summary>The result of a registration, and the read token it produced.</summary>
/// <param name="Token">The opaque read key, on success only. The caller turns it into a URL; the store
/// deliberately does not know the route it is mounted at.</param>
public readonly record struct BlobRegisterResult(
    BlobRegisterOutcome Outcome, string? Token = null, string? Error = null)
{
    public bool Success =>
        Outcome is BlobRegisterOutcome.Registered or BlobRegisterOutcome.AlreadyRegistered;
}

/// <summary>Everything the serving path needs to hand a blob to the static-file middleware.</summary>
/// <param name="RelativePath">Shard-relative, forward-slashed (<c>ab/abcd…</c>), for appending to the
/// blob mount's request path.</param>
public readonly record struct BlobReadTarget(
    string Sha256, string RelativePath, string ContentType, long Length);

/// <summary>
/// Content-addressed, lobby-scoped storage for the media a game cannot send over the relay.
/// </summary>
/// <remarks>
/// <para>The problem: <c>/ws</c> has a non-configurable 512 KiB frame cap whose overage closes the
/// socket, a rate budget whose violation is a terminal disconnect no SDK reconnects from, and a
/// <c>DropOldest</c> outbound queue with no ack or retransmit anywhere in the protocol. A 20 MB
/// battlemap has no route through it, and no amount of chunking changes that.</para>
/// <para>Shaped like <see cref="Words.IAuthorityWordService"/>, which is already the same "logical name
/// → shared content" indirection: a game registers bytes under <em>its own</em> id and the mapping to
/// content lives server-side. The differences are that content here is a file rather than a heap
/// structure, and that lifetime is refcounted rather than swept — the word service's root set (the game
/// catalog) is externally enumerable, and a lobby's blob handles are not. Only this store knows them, so
/// there is nothing to mark against.</para>
/// <para><b>Duplicate registrations are independent handles.</b> Two registrations of the same bytes can
/// be released independently, whether they are in different lobbies or in one lobby under two logical
/// ids, even though a single file backs both. That is what makes dedup invisible to the game, and it is
/// why the refcount counts handles rather than lobbies.</para>
/// </remarks>
public interface IBlobStore
{
    /// <summary>
    /// Why writes are refused, or null when they work. The store is registered and answering even when
    /// switched off, so a game gets an explanation rather than a 404 from a route that is not there —
    /// the same choice <c>PackageManager.InstallBlockedReason</c> makes for portal installs.
    /// </summary>
    string? WriteBlockedReason();

    /// <summary>True when the server already holds these bytes, so a client can skip the upload.</summary>
    bool Has(string? sha256);

    /// <summary>
    /// True when the server holds these bytes, and extends their grace window so a subsequent register
    /// does not race deletion if existing handles are released.
    /// </summary>
    bool Touch(string? sha256);

    /// <summary>
    /// The read token for <paramref name="sha256"/> — an unguessable key derived from the hash under a
    /// per-process secret.
    /// </summary>
    /// <remarks>
    /// <b>The token exists because a bare hash is not a capability.</b> A hash is derived from the bytes,
    /// so it is unguessable only for content the requester does not already have: anyone holding the same
    /// commercial map pack can compute the hashes of every map in it and probe an unauthenticated read
    /// route for which ones a DM uploaded. That is a possession oracle, and spoilers are most of what a
    /// game's asset privacy is for. Keying reads on a MAC of the hash closes it while keeping the URL
    /// headerless, which <c>&lt;img src&gt;</c> and every engine loader require.
    /// </remarks>
    string TokenFor(string sha256);

    /// <summary>
    /// Resolves a read token to a file on disk. False when the token is malformed, its tag does not
    /// verify, no content matches, or the content is still an upload in flight.
    /// </summary>
    bool TryResolveToken(string? token, out BlobReadTarget target);

    /// <summary>
    /// Streams <paramref name="body"/> to disk, hashing as it goes, and publishes it as
    /// <paramref name="sha256"/> once the bytes agree. Peak managed memory is one pooled 80 KB buffer
    /// regardless of the blob's size, which is the whole point.
    /// </summary>
    /// <param name="lobbyId">From the verified ticket, never from the request. A client cannot upload
    /// into another lobby's quota.</param>
    Task<BlobIngestResult> ReceiveAsync(
        string lobbyId, string gameId, string sha256, string? contentType, Stream body,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Points <paramref name="logicalId"/> at already-uploaded content, taking a reference that lives
    /// until the handle is released or the lobby closes.
    /// </summary>
    /// <remarks>
    /// Idempotent for the same <c>(lobbyId, logicalId, sha256)</c>. Re-registering an existing logical id
    /// against a <em>different</em> hash releases the old reference and takes the new one, in that order,
    /// as one operation.
    /// </remarks>
    BlobRegisterResult Register(
        string lobbyId, string gameId, string logicalId, string sha256, string? contentType = null);

    /// <summary>
    /// Releases one handle. <b>An unknown handle is a no-op success</b>, so a game can call this
    /// defensively without tracking what it registered.
    /// </summary>
    /// <returns>True when a handle was actually released.</returns>
    bool Unregister(string lobbyId, string logicalId);

    /// <summary>The read token for a handle this lobby holds, or null if it holds none.</summary>
    string? TokenForHandle(string lobbyId, string logicalId);

    /// <summary>
    /// Releases every handle a lobby holds, deleting whatever that leaves unreferenced. Called from both
    /// teardown paths — <c>WebSocketHandler.CloseLobbyIfDark</c> for the normal one and
    /// <c>LobbyCloser</c> for a lobby closed out from under its players — so a game never has to run
    /// cleanup a crashed session would skip.
    /// </summary>
    void ReleaseLobby(string lobbyId);

    /// <summary>
    /// Drops unreferenced content whose grace window has expired, returning the hashes dropped so the
    /// caller can log them. The <b>backstop</b>, not the mechanism: the refcount already deletes on the
    /// last release, and this catches what it cannot — an abandoned upload's provisional entry, and a
    /// delete that failed because the file was still open.
    /// </summary>
    IReadOnlyList<string> Sweep();

    /// <summary>
    /// Deletes the entire blob root. Correct only at startup, and unconditionally correct there:
    /// lobbies are in-memory and die with the process, and the ticket secret is regenerated per process,
    /// so after a restart every blob on disk is orphaned by definition.
    /// </summary>
    void SweepAtStartup();

    /// <summary>Bytes of distinct content held, for the admin portal's disk view.</summary>
    long TotalBytes { get; }

    /// <summary>Distinct content items held, including uploads in flight.</summary>
    int ContentCount { get; }

    /// <summary>Live handles across every lobby — the unit the refcount actually counts.</summary>
    int HandleCount { get; }

    /// <summary>Content items deleted since the server started. Cumulative and never reset — the
    /// <c>AuthorityMetrics</c> / <c>RelayMetrics</c> convention: a rate needs two samples and that is
    /// the reader's job.</summary>
    long Evicted { get; }
}
