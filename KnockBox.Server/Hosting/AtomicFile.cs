namespace KnockBox.Server.Hosting;

/// <summary>
/// The atomic-publish primitive: <c>File.Move(source, destination, overwrite: true)</c> with a short,
/// tightly bounded retry on a transient IO failure.
///
/// Every place this server makes new bytes live does it with one overwriting rename — the only publish
/// that leaves no instant at which the destination is missing, not even across a crash. On Windows that
/// maps to <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>, which needs delete access to the destination
/// and so fails outright while <em>anything</em> holds a handle to it. A virus scanner or the search
/// indexer opening the file microseconds after it was written is enough — and was: a marketplace
/// download failed with <see cref="UnauthorizedAccessException"/> once in ~50 full test runs, discarding
/// a fully verified package that the very next attempt would have placed.
/// </summary>
/// <remarks>
/// The rename is retried, <b>never split into delete-then-move</b>. Deleting first would reintroduce the
/// exact window the single rename exists to avoid, trading a rare transient failure for a rare permanent
/// one — a strictly worse bargain, since the transient case recovers in milliseconds and the permanent
/// case leaves a game with no package at all.
///
/// The budget is deliberately tiny (4 attempts, ~150ms worst case). This is for a scanner that is about
/// to let go, not for a file someone has genuinely open: <see cref="Admin.AdminSettingsStore"/> calls it
/// under its write lock and <see cref="Games.PackageManager"/> while holding an install slot, so the
/// bound is what stops one stuck rename from stalling every other admin write or queued install. A real
/// ACL denial or a read-only mount still fails, with the original exception and its original stack,
/// just ~150ms later.
///
/// <see cref="Games.GameAssetPrecompressor"/>'s two per-<em>file</em> moves deliberately do NOT use this:
/// they are already caught per file, omitted from the index, and retried by the next reconcile pass,
/// which is a finer granularity than a retry here would add.
/// </remarks>
internal static class AtomicFile
{
    /// <summary>Four tries, so three waits — ~150ms before a transient failure becomes a real one.</summary>
    public const int DefaultAttempts = 4;

    public const int DefaultDelayMs = 50;

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="destination"/>, replacing it, retrying a
    /// transient IO failure a few times. Throws the last failure unchanged once the attempts run out.
    /// </summary>
    public static void MoveWithRetry(
        string source, string destination,
        int attempts = DefaultAttempts, int delayMs = DefaultDelayMs) =>
        Retry(() => File.Move(source, destination, overwrite: true), attempts, delayMs);

    /// <summary>
    /// As <see cref="MoveWithRetry"/>, but waits between attempts without blocking the thread.
    /// </summary>
    /// <remarks>
    /// Pass the token that should cancel the <em>publish</em>, which is not always the one that governed
    /// producing the bytes: <see cref="Marketplace.MarketplaceClient"/> hands over the caller's token
    /// rather than its download deadline, because by the time it renames, the transfer is complete and
    /// hash-verified and that deadline has done its job.
    /// </remarks>
    public static Task MoveWithRetryAsync(
        string source, string destination,
        int attempts = DefaultAttempts, int delayMs = DefaultDelayMs,
        CancellationToken cancellationToken = default) =>
        RetryAsync(() => File.Move(source, destination, overwrite: true), attempts, delayMs, cancellationToken);

    /// <summary>
    /// Moves a whole directory, retrying a transient IO failure exactly as <see cref="MoveWithRetry"/>
    /// does for one file.
    /// </summary>
    /// <remarks>
    /// A directory is MORE exposed to this than a single file, not less: Windows fails a directory
    /// rename with <c>ERROR_ACCESS_DENIED</c> while any file anywhere beneath it is open without
    /// share-delete, so one scanner handle on one freshly written asset sinks the whole swap.
    /// <see cref="Games.GamePackageInstaller"/> renames a directory it finished writing microseconds
    /// earlier, which is precisely the window a real-time scanner occupies — and did: the swap failed
    /// on roughly one full test run in two, on a different package each time.
    ///
    /// Unlike the file form this cannot replace an existing destination — <see cref="Directory.Move"/>
    /// requires it to be absent, which is why the installer moves the live folder aside first rather
    /// than passing an overwrite flag that does not exist.
    /// </remarks>
    public static void MoveDirectoryWithRetry(
        string source, string destination,
        int attempts = DefaultAttempts, int delayMs = DefaultDelayMs) =>
        Retry(() => Directory.Move(source, destination), attempts, delayMs);

    /// <summary>
    /// Runs <paramref name="operation"/>, retrying while it fails transiently and attempts remain.
    /// </summary>
    /// <remarks>
    /// Exposed as the seam the tests drive. A real sharing violation only happens on Windows — on Linux
    /// <c>rename(2)</c> succeeds regardless of open handles, and .NET maps <c>FileShare.None</c> to an
    /// advisory <c>flock</c> that <c>rename</c> never consults — so on the CI runners a handle-holding
    /// test would prove nothing at all. Injecting the operation makes the loop itself testable
    /// everywhere, and the Windows-only tests then use this same entry point with a real
    /// <see cref="File.Move(string, string, bool)"/> inside to prove a genuine sharing violation is in
    /// the retried set.
    /// </remarks>
    public static void Retry(Action operation, int attempts, int delayMs)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            // On the last attempt the filter stops matching, so the original exception propagates with
            // its own stack rather than being caught and rethrown as something else.
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                if (delayMs > 0) Thread.Sleep(delayMs);
            }
        }
    }

    /// <summary>
    /// <see cref="Retry"/>, waiting asynchronously. Takes an <see cref="Action"/> rather than a
    /// <c>Func&lt;Task&gt;</c> because the operation is synchronous either way — only the wait differs.
    /// </summary>
    public static async Task RetryAsync(
        Action operation, int attempts, int delayMs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsTransient(ex))
            {
                // Checked even when there is no delay to wait through, so a cancelled token always
                // stops the loop rather than depending on how the caller configured the backoff.
                cancellationToken.ThrowIfCancellationRequested();
                if (delayMs > 0) await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Worth another try, or not. The same filter the callers' own catch clauses use.
    /// </summary>
    /// <remarks>
    /// Both arms are required: <see cref="UnauthorizedAccessException"/> is not an
    /// <see cref="IOException"/> (it derives from <c>SystemException</c>), and it is the one that was
    /// actually observed. <see cref="FileNotFoundException"/> and
    /// <see cref="DirectoryNotFoundException"/> <em>are</em> <see cref="IOException"/> subclasses and so
    /// get retried pointlessly; that is left alone rather than special-cased, because the only cost is
    /// that a permanent failure surfaces ~150ms later.
    /// </remarks>
    private static bool IsTransient(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
