using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KnockBox.Server.Hosting;

namespace KnockBox.Server.Games.Blobs;

/// <summary>
/// Content-addressed, lobby-scoped blob storage. See <see cref="IBlobStore"/> for what it is for; this
/// is how.
/// </summary>
/// <remarks>
/// <para><b>Three maps, and the middle one is the point.</b> Handles are what a game names and what the
/// refcount counts; content is what sits on disk, keyed by hash so identical bytes are stored once
/// however many sessions register them; lobby charges are the quota accounting. The structure is
/// <see cref="Words.AuthorityWordService"/>'s, which already solves the same indirection.</para>
/// <para><b>Constant memory is the design driver, not a nice property.</b> Ingest streams through one
/// pooled 80 KB buffer straight to a file, hashing as it goes, so peak managed memory per upload is
/// that buffer no matter whether the blob is 4 KB or 100 MB. Nothing in this class ever holds a blob's
/// bytes, and serving hands the file to <c>StaticFileMiddleware</c>, which sendfiles it.</para>
/// <para><b>One lock, and it is not a performance compromise.</b> <c>_gate</c> guards every structural
/// change to <c>_content</c>, every refcount transition, every lobby charge and every deletion —
/// because the invariant they share is compound, and no amount of <c>ConcurrentDictionary</c> makes a
/// compound invariant atomic. The specific bug it exists to prevent: a refcount reaching zero and a
/// delete starting, while another lobby registers the same hash and takes the count back to one, ending
/// with a live handle pointing at a file that has been unlinked. Registration is a lobby-lifecycle
/// operation and an image add, never a per-frame one, so a single lock costs nothing measurable — the
/// same argument <see cref="AuthorityModuleCache.Get"/> makes for its parse lock. Reads
/// (<see cref="Has"/>, <see cref="TryResolveToken"/>) stay lock-free, and the expensive part of an
/// upload — the streaming — is outside the lock entirely.</para>
/// <para><b>No per-key upload lock, and content-addressing is why.</b> Two uploaders of identical bytes
/// each write their own GUID-named <c>.part</c> and both rename onto the same final name; the loser's
/// bytes and the winner's bytes are the same bytes, so a lost race is a no-op by construction. Do not
/// justify this by analogy to <see cref="PackageManager"/>: its GUID staging and overwriting rename are
/// documented purely as crash-atomicity, and its actual answer to per-key contention is
/// <see cref="PackageJobRegistry"/>, chosen over a semaphore <em>because a dictionary entry can be
/// inspected and reported back to a second caller</em>. This store has no such registry and needs none,
/// because its keys are hashes.</para>
/// <para><b>Eviction diverges from <see cref="AuthorityModuleCache"/> deliberately, in both
/// directions.</b> Copied: the mutable entry class (a record would allocate a replacement on every
/// touch), the value-comparing <c>TryRemove</c>, and the "non-positive window means keep" convention.
/// <em>Not</em> copied: the idle clock and its refresh-don't-skip rule. That rule exists because a
/// module cache has no idea whether anyone still wants a module, so it guesses from a timestamp. Here
/// the refcount is exact — zero references means nothing can want it — so an idle window would only
/// delay a deletion that is already correct, and refreshing a clock nothing reads would be
/// cargo-culting. The grace window is the only time-based protection, and it covers exactly one thing:
/// the gap between bytes landing and the <em>first</em> handle referencing them — see
/// <c>BlobEntry.EverReferenced</c> for why "first" is load-bearing rather than pedantic.</para>
/// <para>The other divergence is the important one. Both existing caches drop a dictionary entry and let
/// the GC decide; <b>this one deletes files</b>, so the refcount is load-bearing rather than advisory
/// and a deletion is ordered strictly after the count reaches zero.</para>
/// </remarks>
public sealed class BlobStore : IBlobStore
{
    /// <summary>The repo's universal streaming chunk, matched to the <c>FileStream</c> buffer and the
    /// pool rent so all three agree.</summary>
    private const int ChunkSize = 81920;

    /// <summary>Bytes of HMAC kept in a read token. 128 bits is far past what an unguessable URL needs,
    /// and truncating a MAC is the standard, safe way to shorten one.</summary>
    private const int TagBytes = 16;

    private const int TagHexLength = TagBytes * 2;

    /// <summary>Longest logical id a game may register under. Generous — it is a dictionary key, never a
    /// path — but bounded, because it is attacker-controlled and lives in a map until the lobby closes.</summary>
    public const int MaxLogicalIdLength = 128;

    // One entry per registration, and THE unit of accounting. Keyed the way AuthorityWordService keys
    // its handles: a tuple of the owner and the owner's own name for the thing. Only mutated under
    // _gate; ConcurrentDictionary for safe lock-free enumeration, not for the atomicity (see the class
    // remarks — the invariant is compound).
    private readonly ConcurrentDictionary<(string LobbyId, string LogicalId), BlobHandle> _handles = new();

    // The dedup point, keyed purely on CONTENT. Ordinal: hashes are lowercase hex by construction and
    // the deployment target is Linux, where the paths derived from them are case-sensitive.
    private readonly ConcurrentDictionary<string, BlobEntry> _content = new(StringComparer.Ordinal);

    // Quota accounting, one per lobby that has ever uploaded or registered. OrdinalIgnoreCase to match
    // LobbyManager, which keys lobbies that way.
    private readonly ConcurrentDictionary<string, LobbyCharge> _lobbies = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private readonly BlobOptionsProvider _options;
    private readonly TimeProvider _time;
    private readonly ILogger<BlobStore> _logger;

    // Per process, and non-configurable, for the same reason TokenService's is: it signs nothing that
    // has to survive a restart. Blobs certainly do not — SweepAtStartup deletes every one of them — so a
    // token minted before a restart was already dead, and a fresh secret costs nothing while removing
    // an operator's opportunity to leak one. Its OWN secret rather than TokenService's, so a key is not
    // reused across two purposes.
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);

    private long _totalBytes;
    private long _evicted;
    private ITimer? _sweepTimer;

    public BlobStore(BlobOptionsProvider options, TimeProvider time, ILogger<BlobStore> logger)
    {
        _options = options;
        _time = time;
        _logger = logger;
    }

    // ── State ────────────────────────────────────────────────────────────────────────────────────────

    private sealed record BlobHandle(string Sha256, long RegisteredTicks);

    /// <summary>
    /// One piece of content. A class rather than a record because several fields are mutated in place:
    /// a record would allocate a replacement on every upload and every touch, which is the reason
    /// <see cref="AuthorityModuleCache"/> gives for the same choice.
    /// </summary>
    private sealed class BlobEntry
    {
        public required string Sha256 { get; init; }

        /// <summary>Absolute path the content lives at once published. Set at claim time, so it is
        /// available before the bytes are.</summary>
        public required string Path { get; init; }

        /// <summary>Written before <see cref="Published"/> is set, and read only after a reader has seen
        /// <see cref="Published"/>. The interlocked write to that field is the release barrier that makes
        /// these two visible.</summary>
        public long Length;

        /// <summary>
        /// Normalized at upload and again at register, because dedup means two callers can declare
        /// different types for identical bytes. Last writer wins, and that is harmless: both values came
        /// out of <see cref="BlobContentTypes.Normalize"/>, so both are types the serving path is happy
        /// to hand out.
        /// </summary>
        public string ContentType = BlobContentTypes.Default;

        /// <summary>Live handles referencing this content, across every lobby. Guarded by <c>_gate</c>.</summary>
        public int RefCount;

        /// <summary>
        /// Whether a handle has <em>ever</em> referenced this content, which is what takes it out of the
        /// grace window's scope for good. Guarded by <c>_gate</c>.
        /// </summary>
        /// <remarks>
        /// The grace window protects exactly one gap: bytes that have landed but that nothing references
        /// yet, because a sweep in that moment would delete what a client is one round trip away from
        /// claiming. Once a handle has existed, that gap is behind us — a subsequent release is a game
        /// stating it is done with the blob, and honouring the original window would keep a deliberately
        /// released file on disk for another five minutes for no reason at all. Without this flag,
        /// upload-register-release inside one window leaves the bytes behind and the last lobby to close
        /// does not actually free anything, which is R4 failing quietly.
        /// </remarks>
        public bool EverReferenced;

        /// <summary>
        /// Volatile because ingest extends it without holding <c>_gate</c> and the sweep reads it while
        /// holding it. The only race is two writers stamping near-identical deadlines, and either is
        /// correct.
        /// </summary>
        private long _graceUntilTicks;

        public long GraceUntilTicks
        {
            get => Volatile.Read(ref _graceUntilTicks);
            set => Volatile.Write(ref _graceUntilTicks, value);
        }

        /// <summary>
        /// 0 while the entry is a claim with no bytes behind it, 1 once the staging rename has landed.
        /// Interlocked, and that is what makes the accounting exact: two concurrent uploads of the same
        /// new content both rename successfully, and exactly one of them wins the compare-and-swap, so
        /// the bytes are added to the server total once rather than twice.
        /// </summary>
        public int Published;
    }

    /// <summary>
    /// What one lobby is charged, and how many uploads it has in flight.
    /// </summary>
    /// <remarks>
    /// <see cref="Handles"/> counts this lobby's handles <em>per hash</em>, which is what makes the
    /// charge correct under R6: a lobby registering one 500 MB file under two logical ids is charged
    /// 500 MB, not 1 GB, because the quota is about disk and the file on disk is one file. Charging
    /// twice would penalise a game for a dedup it is not supposed to be able to see.
    /// </remarks>
    private sealed class LobbyCharge
    {
        /// <summary>hash → handles this lobby holds on it. Guarded by <c>_gate</c>.</summary>
        public readonly Dictionary<string, int> Handles = new(StringComparer.Ordinal);

        /// <summary>Sum of the lengths of the distinct hashes above. Guarded by <c>_gate</c> for writes;
        /// read with <see cref="Interlocked.Read(ref long)"/> from the streaming path, which does not
        /// hold it.</summary>
        public long Bytes;

        /// <summary>Uploads currently streaming. Interlocked, deliberately outside <c>_gate</c> — an
        /// upload holds this for its whole duration, and holding the gate that long would serialise
        /// every registration on the server behind one slow client.</summary>
        public int InFlight;
    }

    // ── Reporting ────────────────────────────────────────────────────────────────────────────────────

    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public int ContentCount => _content.Count;
    public int HandleCount => _handles.Count;
    public long Evicted => Interlocked.Read(ref _evicted);

    public string? WriteBlockedReason()
    {
        var options = _options.Current;
        if (!options.Enabled)
            return "Blob sharing is disabled on this server (KnockBox:BlobsEnabled=false).";
        if (!Directory.Exists(options.Root))
            return $"The blob folder '{options.Root}' does not exist.";
        return null;
    }

    // Deliberately NOT a DirectoryProbe.WhyNotWritable check, unlike
    // PackageManager.InstallBlockedReason. That probe writes and deletes a file, and this method runs on
    // every upload and every registration rather than on an admin click — so it would turn a per-request
    // path into two extra filesystem operations to answer a question the actual write answers for free.
    // A root that goes read-only after startup surfaces as WriteFailed with the OS's own message, which
    // is more informative anyway, and Program.cs's bootstrap writability probe is what tells the
    // operator at the moment they can still do something about it.

    // ── Read tokens ──────────────────────────────────────────────────────────────────────────────────

    public string TokenFor(string sha256) => $"{sha256}.{Tag(sha256)}";

    public bool Has(string? sha256) =>
        BlobLayout.IsValidHash(sha256)
        && _content.TryGetValue(sha256!, out var entry)
        && Volatile.Read(ref entry.Published) == 1;

    public bool Touch(string? sha256)
    {
        if (!BlobLayout.IsValidHash(sha256)) return false;
        if (!_content.TryGetValue(sha256!, out var entry) || Volatile.Read(ref entry.Published) == 0)
            return false;

        lock (_gate)
        {
            if (!_content.TryGetValue(sha256!, out entry) || Volatile.Read(ref entry.Published) == 0)
                return false;

            entry.GraceUntilTicks = Now() + _options.Current.Grace.Ticks;
            entry.EverReferenced = false;
            return true;
        }
    }

    public bool TryResolveToken(string? token, out BlobReadTarget target)
    {
        target = default;
        if (token is null || token.Length != BlobLayout.HashLength + 1 + TagHexLength) return false;
        if (token[BlobLayout.HashLength] != '.') return false;

        var hash = token[..BlobLayout.HashLength];
        if (!BlobLayout.IsValidHash(hash)) return false;

        Span<byte> provided = stackalloc byte[TagBytes];
        if (Convert.FromHexString(token.AsSpan(BlobLayout.HashLength + 1), provided, out _, out var written)
                != System.Buffers.OperationStatus.Done
            || written != TagBytes)
            return false;

        Span<byte> expected = stackalloc byte[TagBytes];
        Mac(hash, expected);
        // Fixed-time, the same discipline TokenService applies before it will even deserialize a
        // payload. The length and hex-shape checks above are not constant-time and do not need to be:
        // they leak only that the token was the wrong shape, which its sender already knows.
        if (!CryptographicOperations.FixedTimeEquals(provided, expected)) return false;

        if (!_content.TryGetValue(hash, out var entry)) return false;
        // An entry with no bytes behind it yet must 404 rather than resolve to a path that does not
        // exist. This is the second half of claiming the grace window before the first byte: the claim
        // reserves the name, it does not assert the content.
        if (Volatile.Read(ref entry.Published) == 0) return false;

        target = new BlobReadTarget(
            hash, BlobLayout.RelativePath(hash), entry.ContentType, entry.Length);
        return true;
    }

    private string Tag(string hash)
    {
        Span<byte> mac = stackalloc byte[TagBytes];
        Mac(hash, mac);
        return Convert.ToHexStringLower(mac);
    }

    private void Mac(string hash, Span<byte> destination)
    {
        Span<byte> full = stackalloc byte[32];
        Span<byte> key = stackalloc byte[BlobLayout.HashLength];
        // ASCII by construction — IsValidHash has already restricted the string to [0-9a-f].
        Encoding.ASCII.GetBytes(hash, key);
        HMACSHA256.HashData(_secret, key, full);
        full[..TagBytes].CopyTo(destination);
    }

    // ── Ingest ───────────────────────────────────────────────────────────────────────────────────────

    public async Task<BlobIngestResult> ReceiveAsync(
        string lobbyId, string gameId, string sha256, string? contentType, Stream body,
        CancellationToken cancellationToken = default)
    {
        if (WriteBlockedReason() is { } blocked)
            return new BlobIngestResult(BlobIngestOutcome.Disabled, 0, blocked);
        if (!BlobLayout.IsValidHash(sha256))
            return new BlobIngestResult(BlobIngestOutcome.HashRejected, 0,
                "A blob id must be a SHA-256 as 64 lowercase hex characters.");

        var options = _options.Current;
        var charge = _lobbies.GetOrAdd(lobbyId, static _ => new LobbyCharge());

        // Claimed for the whole upload, so a client cannot open a hundred PUTs and leave them open. The
        // decrement is in the finally below, which also runs on a cancelled request.
        var inFlight = Interlocked.Increment(ref charge.InFlight);
        try
        {
            if (options.MaxUploadsPerLobby > 0 && inFlight > options.MaxUploadsPerLobby)
                return new BlobIngestResult(BlobIngestOutcome.TooManyUploads, 0,
                    $"This session already has {options.MaxUploadsPerLobby} uploads in flight " +
                    "(KnockBox:BlobMaxUploadsPerLobby). Finish one before starting another.");

            // Claiming the entry and answering "already present" are ONE step under the gate. Split
            // apart they race with deletion: a sweep can unlink the file between a caller seeing
            // Published and a caller acting on it, and the caller then reports success for content that
            // is no longer there.
            BlobEntry entry;
            lock (_gate)
            {
                if (_content.TryGetValue(sha256, out var existing)
                    && Volatile.Read(ref existing.Published) == 1)
                {
                    // Extend the grace window: a client that just learned we have these bytes is about
                    // to register them, and the register must not race a sweep or an unregister by an
                    // existing holder.
                    existing.GraceUntilTicks = Now() + options.Grace.Ticks;
                    existing.EverReferenced = false;
                    return new BlobIngestResult(BlobIngestOutcome.AlreadyPresent, existing.Length);
                }

                entry = _content.GetOrAdd(sha256, hash => new BlobEntry
                {
                    Sha256 = hash,
                    Path = BlobLayout.ContentPath(options.Root, hash),
                });
                // Claimed BEFORE the first byte is written, so a sweep that starts mid-upload cannot
                // delete what this call is still producing. GameAssetPrecompressor's _seeded set is the
                // same move: reserve the outcome, then produce it.
                entry.GraceUntilTicks = Now() + options.Grace.Ticks;
            }

            return await StreamToDiskAsync(
                entry, charge, options, gameId, contentType, body, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref charge.InFlight);
        }
    }

    private async Task<BlobIngestResult> StreamToDiskAsync(
        BlobEntry entry, LobbyCharge charge, BlobOptions options, string gameId, string? contentType,
        Stream body, CancellationToken cancellationToken)
    {
        var stagingDir = BlobLayout.StagingDir(options.Root);
        var staging = BlobLayout.StagingPath(options.Root, Guid.NewGuid());
        var lobbyQuota = options.LobbyQuotaFor(gameId);

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var published = false;
        long total = 0;
        try
        {
            Directory.CreateDirectory(stagingDir);

            string actual;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var file = new FileStream(
                    staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize,
                    useAsync: true))
                {
                    int read;
                    while ((read = await body
                        .ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken)
                        .ConfigureAwait(false)) > 0)
                    {
                        total += read;

                        // Every cap is checked against BYTES WRITTEN, never Content-Length: that header
                        // is the client's claim, and a chunked request has none at all. The `> 0` guards
                        // are the house convention that a non-positive cap disables that individual
                        // check — omit one and a documented "no limit" becomes "refuse everything", which
                        // is exactly how MaxPackageBytes=0 broke twice.
                        if (options.MaxBlobBytes > 0 && total > options.MaxBlobBytes)
                            return new BlobIngestResult(BlobIngestOutcome.TooLarge, total,
                                $"The blob exceeds the {options.MaxBlobBytes:N0}-byte limit " +
                                "(KnockBox:BlobMaxBytes).");

                        // Both quotas are checked optimistically: the reads are current but unsynchronised
                        // against other uploads in flight, so N concurrent uploads can each pass and
                        // overshoot by up to N-1 blobs. That is deliberate. Reserving precisely would mean
                        // holding a lock for the duration of an upload, which is minutes on a slow client
                        // and would serialise every other session behind it — a far worse property than a
                        // bounded overshoot on a quota an operator sized with headroom anyway.
                        if (lobbyQuota > 0 && Interlocked.Read(ref charge.Bytes) + total > lobbyQuota)
                            return new BlobIngestResult(BlobIngestOutcome.LobbyQuotaExceeded, total,
                                $"This session's {lobbyQuota:N0}-byte blob quota is full " +
                                "(KnockBox:BlobLobbyQuotaBytes). Release a blob it no longer needs.");

                        if (options.TotalQuotaBytes > 0
                            && Interlocked.Read(ref _totalBytes) + total > options.TotalQuotaBytes)
                            return new BlobIngestResult(BlobIngestOutcome.TotalQuotaExceeded, total,
                                $"The server's {options.TotalQuotaBytes:N0}-byte blob quota is full " +
                                "(KnockBox:BlobTotalQuotaBytes).");

                        hasher.AppendData(buffer, 0, read);
                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }

            // VERIFY, never trust. The URL names the hash the content will be stored under, so without
            // this check a client could publish arbitrary bytes at a well-known hash — poisoning a map
            // every other session on the server would then be served. It is the one real attack this
            // design admits, and one comparison closes it.
            if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
                return new BlobIngestResult(BlobIngestOutcome.HashMismatch, total,
                    "The uploaded bytes do not hash to the id they were sent under.");

            Directory.CreateDirectory(BlobLayout.ShardDir(options.Root, entry.Sha256));
            // One overwriting rename, retried briefly — never delete-then-move, and never a copy, which
            // is why staging sits inside the blob root rather than in the system temp directory. An
            // overwrite here is harmless by construction: the only bytes that can already be at this
            // name hash to the same value, so they ARE these bytes.
            await AtomicFile.MoveWithRetryAsync(staging, entry.Path, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            published = true;

            entry.Length = total;
            entry.ContentType = BlobContentTypes.Normalize(contentType);
            entry.GraceUntilTicks = Now() + options.Grace.Ticks;
            // The interlocked transition is both the release barrier for the two writes above and the
            // arbiter of who accounts for the bytes when two uploads of the same new content race.
            if (Interlocked.CompareExchange(ref entry.Published, 1, 0) == 0)
                Interlocked.Add(ref _totalBytes, total);

            return new BlobIngestResult(BlobIngestOutcome.Stored, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Covers both ends: a disk that refused the write, and a client socket that died mid-body.
            // Neither is a server fault worth a 500 and a stack trace, and the message an operator or a
            // game developer needs is the OS's own.
            _logger.LogWarning(ex, "A blob upload of {Bytes} bytes did not complete.", total);
            return new BlobIngestResult(BlobIngestOutcome.WriteFailed, total,
                $"The upload did not complete ({ex.Message}).");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            // Anything that did not reach the rename leaves a .part behind: a refusal, a hash mismatch, a
            // cancelled request, a dead socket. The FileStream is already disposed by here — the `await
            // using` scope closes on every exit path, including the early returns above — so the handle
            // is free. Best effort, and SweepAtStartup is the backstop for a process that was killed.
            if (!published)
            {
                try { File.Delete(staging); } catch { /* best effort */ }
            }
        }
    }

    // ── Handles ──────────────────────────────────────────────────────────────────────────────────────

    public BlobRegisterResult Register(
        string lobbyId, string gameId, string logicalId, string sha256, string? contentType = null)
    {
        if (WriteBlockedReason() is { } blocked)
            return new BlobRegisterResult(BlobRegisterOutcome.Disabled, null, blocked);
        if (!BlobLayout.IsValidHash(sha256))
            return new BlobRegisterResult(BlobRegisterOutcome.HashRejected, null,
                "A blob id must be a SHA-256 as 64 lowercase hex characters.");
        if (!IsValidLogicalId(logicalId))
            return new BlobRegisterResult(BlobRegisterOutcome.LogicalIdRejected, null,
                $"A blob name must be 1 to {MaxLogicalIdLength} characters and carry no control characters.");

        var options = _options.Current;
        var quota = options.LobbyQuotaFor(gameId);
        var charge = _lobbies.GetOrAdd(lobbyId, static _ => new LobbyCharge());
        var key = (lobbyId, logicalId);

        lock (_gate)
        {
            if (!_content.TryGetValue(sha256, out var entry)
                || Volatile.Read(ref entry.Published) == 0)
                return new BlobRegisterResult(BlobRegisterOutcome.UnknownHash, null,
                    "No blob with that id is stored. Upload it first.");

            _handles.TryGetValue(key, out var existing);

            // Idempotent for the same (lobby, name, hash). Re-registering must NOT take a second
            // reference: a client that retries after a dropped response would otherwise leak one, and
            // the leak is invisible until the file outlives its lobby.
            if (existing is not null && string.Equals(existing.Sha256, sha256, StringComparison.Ordinal))
                return new BlobRegisterResult(BlobRegisterOutcome.AlreadyRegistered, TokenFor(sha256));

            // Quota is checked BEFORE anything mutates, on the charge this operation would produce — not
            // after releasing the old reference. Checking afterwards would need a rollback, and the
            // rollback is not possible: releasing the old handle can delete its file, and re-acquiring a
            // reference to bytes that have just been unlinked would hand the game a live handle onto
            // nothing.
            if (quota > 0 && Projected(charge, existing?.Sha256, entry) > quota)
                return new BlobRegisterResult(BlobRegisterOutcome.LobbyQuotaExceeded, null,
                    $"This session's {quota:N0}-byte blob quota is full. Release a blob it no longer needs.");

            if (existing is not null)
            {
                // Rehoming a name onto different content: release then acquire, in that order, as one
                // operation. The order matters only for the quota, which the projection above already
                // accounted for.
                _handles.TryRemove(key, out _);
                ReleaseLocked(charge, existing.Sha256);
            }

            AcquireLocked(charge, entry);
            _handles[key] = new BlobHandle(sha256, Now());
            entry.ContentType = BlobContentTypes.Normalize(contentType ?? entry.ContentType);
        }

        return new BlobRegisterResult(BlobRegisterOutcome.Registered, TokenFor(sha256));
    }

    public bool Unregister(string lobbyId, string logicalId)
    {
        if (!_lobbies.TryGetValue(lobbyId, out var charge)) return false;

        lock (_gate)
        {
            if (!_handles.TryRemove((lobbyId, logicalId), out var handle)) return false;
            ReleaseLocked(charge, handle.Sha256);
            return true;
        }
    }

    public string? TokenForHandle(string lobbyId, string logicalId) =>
        _handles.TryGetValue((lobbyId, logicalId), out var handle) ? TokenFor(handle.Sha256) : null;

    public void ReleaseLobby(string lobbyId)
    {
        // A lobby with no charge never registered and never uploaded, so there is nothing to release and
        // nothing in _handles keyed to it — both Register and ReceiveAsync create the charge before they
        // create anything else.
        if (!_lobbies.TryRemove(lobbyId, out var charge)) return;

        var released = 0;
        lock (_gate)
        {
            // A scan of every handle on the server, because _handles is keyed by the pair rather than
            // nested per lobby. Deliberate: handle counts are in the tens per lobby and the low
            // thousands in total, this runs once per lobby teardown, and the flat tuple key is what
            // makes every other operation a single lookup. Nesting would trade four one-line methods
            // for a two-level structure to save a scan nobody can measure.
            foreach (var key in _handles.Keys)
            {
                if (!string.Equals(key.LobbyId, lobbyId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_handles.TryRemove(key, out var handle)) continue;
                ReleaseLocked(charge, handle.Sha256);
                released++;
            }
        }

        if (released > 0 && _logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug("Released {Count} blob handle(s) held by lobby {LobbyId}.", released, lobbyId);
    }

    /// <summary>
    /// What <paramref name="charge"/> would sum to after dropping a handle on
    /// <paramref name="releasing"/> (when that was its last) and taking one on
    /// <paramref name="acquiring"/> (when it holds none yet).
    /// </summary>
    private long Projected(LobbyCharge charge, string? releasing, BlobEntry acquiring)
    {
        var projected = charge.Bytes;

        if (releasing is not null
            && charge.Handles.TryGetValue(releasing, out var held) && held == 1
            && _content.TryGetValue(releasing, out var old))
            projected -= old.Length;

        if (!charge.Handles.ContainsKey(acquiring.Sha256))
            projected += acquiring.Length;

        return projected;
    }

    private static void AcquireLocked(LobbyCharge charge, BlobEntry entry)
    {
        entry.RefCount++;
        entry.EverReferenced = true;

        var held = charge.Handles.GetValueOrDefault(entry.Sha256);
        charge.Handles[entry.Sha256] = held + 1;
        // Charged on the FIRST handle only. This is R6's accounting consequence: the second logical id
        // in a lobby is a real, independently releasable handle, and it costs no disk.
        if (held == 0) charge.Bytes += entry.Length;
    }

    private void ReleaseLocked(LobbyCharge charge, string sha256)
    {
        if (charge.Handles.TryGetValue(sha256, out var held))
        {
            if (held <= 1)
            {
                charge.Handles.Remove(sha256);
                if (_content.TryGetValue(sha256, out var charged)) charge.Bytes -= charged.Length;
            }
            else
            {
                charge.Handles[sha256] = held - 1;
            }
        }

        if (!_content.TryGetValue(sha256, out var entry)) return;
        if (--entry.RefCount > 0) return;

        // Zero references. The file goes now unless it is still inside its grace window, in which case
        // the sweep collects it — an upload that has just landed but not yet registered looks exactly
        // like this, and deleting it would delete what a client is one round trip away from claiming.
        TryEvictLocked(entry);
    }

    // ── Eviction ─────────────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<string> Sweep()
    {
        var now = Now();
        List<string>? dropped = null;

        lock (_gate)
        {
            foreach (var (hash, entry) in _content)
            {
                if (entry.RefCount > 0) continue;
                if (!entry.EverReferenced && entry.GraceUntilTicks > now) continue;
                if (TryEvictLocked(entry)) (dropped ??= []).Add(hash);
            }
        }

        return dropped is null ? [] : dropped;
    }

    /// <summary>
    /// Deletes an unreferenced entry's file and drops it from the map. Call under <c>_gate</c>, with
    /// <c>RefCount</c> already at zero.
    /// </summary>
    /// <remarks>
    /// <b>The file goes first, and the map entry only if that succeeded.</b> Dropping the entry on a
    /// failed delete would orphan the bytes permanently: nothing would reference them and nothing would
    /// know they existed, so no later sweep could find them. Leaving the entry means the next sweep
    /// retries, which is what makes a delete that lost to an open read handle — the normal case on
    /// Windows, where unlinking an open file throws rather than deferring — a delay rather than a leak.
    ///
    /// The delete happens under <c>_gate</c>, which is a deliberate choice to hold a lock across
    /// filesystem I/O. A sweep drops only entries that are already unreferenced and out of grace, so the
    /// count is small and the alternative — collect under the lock, delete outside, remove under it
    /// again — reopens the exact register-versus-delete race the single gate exists to close.
    /// </remarks>
    private bool TryEvictLocked(BlobEntry entry)
    {
        if (!entry.EverReferenced && entry.GraceUntilTicks > Now()) return false;
        if (!TryDeleteContent(entry)) return false;

        // Value-comparing, the AuthorityModuleCache rule: if a concurrent claim replaced this entry we
        // must not drop the replacement. It cannot happen while _gate is held, and asserting the
        // invariant costs one comparison.
        if (!_content.TryRemove(new KeyValuePair<string, BlobEntry>(entry.Sha256, entry))) return false;

        if (Interlocked.Exchange(ref entry.Published, 0) == 1)
            Interlocked.Add(ref _totalBytes, -entry.Length);
        Interlocked.Increment(ref _evicted);
        return true;
    }

    private bool TryDeleteContent(BlobEntry entry)
    {
        try
        {
            // A provisional entry has no file at all — an abandoned PUT, or one that never got past its
            // first cap check. Nothing to delete is a successful delete.
            File.Delete(entry.Path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete blob {Path}; the next sweep will retry.", entry.Path);
            return false;
        }
    }

    public void SweepAtStartup()
    {
        // Configured, not Current: this runs before any persisted operator policy has been applied, and
        // the root is startup-only anyway.
        var root = _options.Configured.Root;
        if (!Directory.Exists(root)) return;

        var removed = 0;
        // Materialised before deleting: enumerating a directory while removing entries from it is not
        // something either OS promises to survive.
        foreach (var dir in BlobLayout.ShardDirs(root).ToList())
        {
            // Per-directory, so one shard the server cannot remove does not abandon the other 255.
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not clear the blob shard {Dir} at startup.", dir);
            }
        }

        var staging = BlobLayout.StagingDir(root);
        if (Directory.Exists(staging))
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not clear the blob staging folder {Dir} at startup.", staging);
            }
        }

        if (removed > 0)
            _logger.LogInformation(
                "Cleared {Count} blob shard(s) from {Root} at startup. Every blob is anchored to a " +
                "lobby, and lobbies do not survive a restart, so all of them were orphaned.",
                removed, root);
    }

    /// <summary>
    /// Arms the backstop sweep. Called once during bootstrap.
    /// </summary>
    /// <remarks>
    /// <para>A <see cref="TimeProvider"/> timer owned by this class rather than a raw
    /// <c>System.Threading.Timer</c> in <c>Program.cs</c>, for the reason
    /// <c>ServerAuthorityManager.StartModuleCacheSweep</c> gives: the pass itself
    /// (<see cref="Sweep"/>) is public and clock-free so a test can drive it, and what is left here is
    /// two lines of timer with nothing to assert. There is no <c>IHostedService</c>,
    /// <c>BackgroundService</c> or <c>PeriodicTimer</c> anywhere in this server, and this does not
    /// introduce the first.</para>
    /// <para><b>The cadence is fixed for the process while the grace window is read live.</b> Deriving
    /// the interval from the window is the trap that forced <c>DisconnectGraceSeconds</c> to stay
    /// startup-only. <c>KnockBox:BlobSweepSeconds</c> is therefore startup-only and the portal reports it
    /// read-only, whereas <c>BlobGraceMinutes</c> is editable and takes effect on the next tick.</para>
    /// </remarks>
    public void StartSweep(CancellationToken stopping)
    {
        var interval = _options.Configured.SweepInterval;
        if (interval <= TimeSpan.Zero)
        {
            _logger.LogInformation(
                "The blob backstop sweep is off (KnockBox:BlobSweepSeconds=0). Blobs are still released " +
                "when their handles are, so this only disables the retry for a delete that failed.");
            return;
        }

        _sweepTimer = _time.CreateTimer(_ =>
        {
            // Nothing may escape a timer callback: an unhandled exception here takes the process down.
            try
            {
                var dropped = Sweep();
                if (dropped.Count > 0 && _logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Deleted {Count} unreferenced blob(s), freeing {Bytes} bytes of quota.",
                        dropped.Count, TotalBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The blob sweep failed.");
            }
        }, null, interval, interval);

        stopping.Register(() => _sweepTimer?.Dispose());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private long Now() => _time.GetUtcNow().UtcTicks;

    /// <summary>
    /// Whether a game may register under this name. Bounded and control-character-free, and nothing
    /// more: a logical id is a dictionary key and never reaches the filesystem — the path comes from the
    /// hash — so there is no traversal to defend against and no reason to restrict the character set a
    /// game names its own assets with.
    /// </summary>
    internal static bool IsValidLogicalId(string? logicalId)
    {
        if (string.IsNullOrEmpty(logicalId) || logicalId.Length > MaxLogicalIdLength) return false;
        foreach (var c in logicalId)
            if (char.IsControl(c))
                return false;
        return true;
    }
}
