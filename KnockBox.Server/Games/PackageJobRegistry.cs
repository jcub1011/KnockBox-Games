using System.Collections.Concurrent;

namespace KnockBox.Server.Games;

/// <summary>What a job is doing to a game.</summary>
public enum PackageJobKind
{
    /// <summary>A game that was not installed is being added.</summary>
    Install,

    /// <summary>An installed game is being replaced with a different version.</summary>
    Update,

    /// <summary>An installed game is being replaced with a retained earlier version.</summary>
    Rollback,

    /// <summary>A managed game is being removed.</summary>
    Uninstall,
}

/// <summary>Where a job's bytes came from. Distinct from <see cref="PackageJobKind"/>: an upload can install OR update.</summary>
public enum PackageJobSource
{
    /// <summary>Fetched from a registered marketplace catalog, hash-checked against its entry.</summary>
    Marketplace,

    /// <summary>Streamed to the portal by the operator. No catalog hash exists to check it against.</summary>
    Upload,

    /// <summary>A retained backup already on disk.</summary>
    Backup,

    /// <summary>Nothing was fetched — an uninstall.</summary>
    None,
}

/// <summary>
/// A job's position in its lifecycle. The terminal three are the only states a job stays in.
/// </summary>
public enum PackageJobStatus
{
    Queued,
    Downloading,
    Verifying,

    /// <summary>Drain mode: the package is ready and the job is waiting for the game's last lobby to end.</summary>
    WaitingForLobbies,

    /// <summary>The point of no return — files are being swapped. Deliberately not cancellable.</summary>
    Applying,

    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>How an update is allowed to affect lobbies that are running the game right now.</summary>
public enum PackageApplyMode
{
    /// <summary>Apply only while the game has no lobbies; otherwise leave it for later.</summary>
    Auto,

    /// <summary>Block new lobbies for the game and apply once the running ones finish on their own.</summary>
    Drain,

    /// <summary>Close every lobby running the game, then apply.</summary>
    Force,
}

/// <summary>One package operation, as the portal sees it.</summary>
/// <param name="Sequence">
/// Bumped on every mutation, so a poll can ask for what changed since a cursor — the same change-feed
/// shape as <c>AdminLogBuffer</c>, and the reason the portal needs no socket and no SSE.
/// </param>
/// <param name="BytesTotal">0 when unknown. The portal renders indeterminate progress rather than a
/// confident 0 %.</param>
public sealed record PackageJob(
    string JobId,
    long Sequence,
    PackageJobKind Kind,
    PackageJobSource Source,
    string GameId,
    string? GameName,
    string? FromVersion,
    string? ToVersion,
    PackageJobStatus Status,
    string Phase,
    long BytesDone,
    long BytesTotal,
    PackageApplyMode Mode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Error,
    string? Warning,
    int LobbiesWaiting)
{
    /// <summary>True once the job has stopped moving — nothing further will change but retention.</summary>
    public bool IsTerminal => Status is PackageJobStatus.Succeeded or PackageJobStatus.Failed
        or PackageJobStatus.Cancelled;

    /// <summary>
    /// Whether Cancel would be honoured. False from <see cref="PackageJobStatus.Applying"/> onwards: a
    /// half-swapped game directory is the one outcome worth refusing to create, the same reasoning
    /// <c>AdminOperations.DeleteGame</c> applies to a half-delete.
    /// </summary>
    public bool Cancellable => Status is PackageJobStatus.Queued or PackageJobStatus.Downloading
        or PackageJobStatus.Verifying or PackageJobStatus.WaitingForLobbies;
}

/// <summary>The outcome of asking to cancel a job.</summary>
public enum PackageCancelOutcome { Cancelled, NotFound, TooLate }

/// <summary>
/// The live and recently-finished package operations, as a cursor-polled change feed.
/// </summary>
/// <remarks>
/// A download-and-extract of a large game outlives any poll interval, and a drain is open-ended, so no
/// package operation can live inside an HTTP request. Every route starts a job and answers with its id;
/// the portal follows it here. Because the state is server-side, switching tabs, reloading the page or
/// closing the browser changes nothing about the operation.
///
/// This is the third use of the house cursor-polling pattern (after the log ring buffer): a monotonic
/// sequence bumped on every mutation, and a <c>Read(after)</c> that returns only what moved. Still no
/// SSE and no second socket role.
///
/// Finished jobs are RETAINED, not dropped: an operator who spent ten minutes on the Logs tab has to be
/// able to come back and find out whether the update worked.
/// </remarks>
public sealed class PackageJobRegistry(TimeProvider clock, int retention = PackageJobRegistry.DefaultRetention)
{
    public const int DefaultRetention = 50;

    // Progress ticks are the only high-frequency mutation, and each one bumps the sequence — which is
    // what a polling client diffs on. Unthrottled, a fast local copy would churn the feed for no visible
    // benefit: nobody can read a progress bar updating faster than this.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly int _retention = Math.Max(4, retention);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, PackageJob> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastProgress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancels = new(StringComparer.Ordinal);
    private long _sequence;

    /// <summary>The highest sequence issued — the cursor a caller passes back as <c>after</c>.</summary>
    public long LastSequence { get { lock (_gate) return _sequence; } }

    /// <summary>Jobs that have not reached a terminal state.</summary>
    public int ActiveCount { get { lock (_gate) return _jobs.Values.Count(j => !j.IsTerminal); } }

    public int Count { get { lock (_gate) return _jobs.Count; } }

    /// <summary>Starts a job in <see cref="PackageJobStatus.Queued"/> and returns it.</summary>
    public PackageJob Create(
        PackageJobKind kind, PackageJobSource source, string gameId, string? gameName,
        string? fromVersion, string? toVersion, PackageApplyMode mode)
    {
        var job = new PackageJob(
            Guid.NewGuid().ToString("N"), 0, kind, source, gameId, gameName, fromVersion, toVersion,
            PackageJobStatus.Queued, "Queued.", 0, 0, mode, clock.GetUtcNow(), null, null, null, 0);

        lock (_gate)
        {
            job = job with { Sequence = ++_sequence };
            _jobs[job.JobId] = job;
            Evict();
        }
        _cancels[job.JobId] = new CancellationTokenSource();
        return job;
    }

    /// <summary>The token a job's worker should observe. Never null for a live job.</summary>
    public CancellationToken TokenFor(string jobId) =>
        _cancels.TryGetValue(jobId, out var cts) ? cts.Token : CancellationToken.None;

    public PackageJob? Get(string jobId)
    {
        lock (_gate) return _jobs.GetValueOrDefault(jobId);
    }

    /// <summary>The active job for a game, if any. What makes a second click a 409 rather than a race.</summary>
    public PackageJob? ActiveFor(string gameId)
    {
        lock (_gate)
            return _jobs.Values.FirstOrDefault(
                j => !j.IsTerminal && string.Equals(j.GameId, gameId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Applies a change and bumps the sequence. No-op for an unknown or already-terminal job.</summary>
    public PackageJob? Mutate(string jobId, Func<PackageJob, PackageJob> change)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.IsTerminal) return current;
            var next = change(current) with { Sequence = ++_sequence };
            _jobs[jobId] = next;
            return next;
        }
    }

    public PackageJob? SetStatus(string jobId, PackageJobStatus status, string phase) =>
        Mutate(jobId, j => j with { Status = status, Phase = phase });

    /// <summary>Records transferred bytes, throttled so the feed does not churn.</summary>
    public void Progress(string jobId, long bytesDone, long bytesTotal)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.IsTerminal) return;

            var now = clock.GetUtcNow();
            // Always publish the final byte, whatever the throttle says: a bar frozen at 97 % on a job
            // that has actually finished transferring reads as a stall.
            var complete = bytesTotal > 0 && bytesDone >= bytesTotal;
            if (!complete && _lastProgress.TryGetValue(jobId, out var last) && now - last < ProgressInterval)
                return;

            _lastProgress[jobId] = now;
            _jobs[jobId] = current with { BytesDone = bytesDone, BytesTotal = bytesTotal, Sequence = ++_sequence };
        }
    }

    /// <summary>
    /// Called once when a job reaches a terminal state, with the finished job.
    /// </summary>
    /// <remarks>
    /// A settable hook rather than a constructor dependency, the same shape as <c>LobbyCloser.OnClosing</c>:
    /// this class is the install engine's bookkeeping and has no business knowing that outbound webhooks
    /// exist. Set once from the composition root, before any request is served. Because
    /// <see cref="Finish"/> is idempotent, a subscriber is guaranteed exactly one call per job.
    /// </remarks>
    public Action<PackageJob>? OnFinished { get; set; }

    /// <summary>Moves a job to a terminal state. Idempotent — the first terminal transition wins.</summary>
    public PackageJob? Finish(string jobId, PackageJobStatus status, string phase, string? error = null,
        string? warning = null)
    {
        PackageJob? finished;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current) || current.IsTerminal) return current;
            finished = current with
            {
                Status = status,
                Phase = phase,
                Error = error,
                Warning = warning ?? current.Warning,
                EndedAt = clock.GetUtcNow(),
                Sequence = ++_sequence,
            };
            _jobs[jobId] = finished;
            _lastProgress.Remove(jobId);
            Evict();
        }

        if (_cancels.TryRemove(jobId, out var cts)) cts.Dispose();
        // Outside the lock, and swallowing: a notification hook must not hold the registry's gate, and a
        // subscriber that throws must not leave a job un-finished in the caller's eyes.
        if (finished is not null && OnFinished is { } hook)
        {
            try { hook(finished); } catch { /* a notification is never worth failing the job for */ }
        }
        return finished;
    }

    /// <summary>
    /// Asks a job to stop. <see cref="PackageCancelOutcome.TooLate"/> once it is applying.
    /// </summary>
    /// <remarks>
    /// This only signals; the job moves itself to <see cref="PackageJobStatus.Cancelled"/> when it
    /// notices. A worker that has already passed the point of no return therefore still completes, which
    /// is exactly the intent.
    /// </remarks>
    public PackageCancelOutcome Cancel(string jobId)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var current)) return PackageCancelOutcome.NotFound;
            if (current.IsTerminal) return PackageCancelOutcome.TooLate;
            if (!current.Cancellable) return PackageCancelOutcome.TooLate;
        }

        if (_cancels.TryGetValue(jobId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { /* finished in the meantime */ }
        }
        return PackageCancelOutcome.Cancelled;
    }

    /// <summary>Jobs whose sequence is above <paramref name="after"/>, oldest change first.</summary>
    public IReadOnlyList<PackageJob> Read(long after = 0, int limit = 200)
    {
        if (limit <= 0) return [];
        lock (_gate)
        {
            return [.. _jobs.Values
                .Where(j => j.Sequence > after)
                .OrderBy(j => j.Sequence)
                .TakeLast(limit)];
        }
    }

    /// <summary>Every retained job, newest first — what a client with no cursor starts from.</summary>
    public IReadOnlyList<PackageJob> Snapshot()
    {
        lock (_gate) return [.. _jobs.Values.OrderByDescending(j => j.StartedAt).ThenByDescending(j => j.Sequence)];
    }

    // Retention: only FINISHED jobs are evictable, oldest first. Dropping an active job would strand a
    // running operation with no way to observe or cancel it, so the cap is deliberately soft — a burst
    // of concurrent work is allowed to exceed it rather than lose track of itself.
    private void Evict()
    {
        if (_jobs.Count <= _retention) return;

        foreach (var stale in _jobs.Values
                     .Where(j => j.IsTerminal)
                     .OrderBy(j => j.EndedAt ?? j.StartedAt)
                     .Take(_jobs.Count - _retention)
                     .ToList())
        {
            _jobs.Remove(stale.JobId);
            _lastProgress.Remove(stale.JobId);
        }
    }
}
