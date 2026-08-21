using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The bounded retry around the atomic-publish rename. The bug it pins: a marketplace download failed
/// with <c>UnauthorizedAccessException</c> out of <c>File.Move(..., overwrite: true)</c> once in ~50 full
/// test runs on Windows + Defender, discarding a fully verified package that the next attempt would have
/// placed.
/// </summary>
/// <remarks>
/// Split deliberately into two halves. The portable half drives
/// <see cref="AtomicFile.Retry(Action, int, int)"/> with an injected operation, because a real sharing
/// violation cannot be produced on the CI runners: they are Linux, where <c>rename(2)</c> succeeds
/// regardless of open handles, so a handle-holding test would pass without ever entering the retry loop
/// (and its negative twin would fail outright). The Windows-only half then feeds a real
/// <c>File.Move</c> through that same entry point, which is what proves a genuine sharing violation is
/// inside the retried exception set rather than merely assumed to be.
///
/// Neither half uses a background thread. The handle is released from INSIDE the operation, so the
/// ordering is guaranteed rather than raced — the same reasoning that made
/// <c>RecordingLogger.OnLog</c> a callback instead of a <c>Barrier</c>.
/// </remarks>
public class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kb-atomicfile-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    /// <summary>A source holding "new" and a destination holding "old", so a move is observable.</summary>
    private (string Source, string Destination) Pair()
    {
        var source = Path.Combine(_dir, $"src-{Guid.NewGuid():N}");
        var destination = Path.Combine(_dir, $"dst-{Guid.NewGuid():N}");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "old");
        return (source, destination);
    }

    // ── The retry loop itself (portable: this is what CI enforces) ────────────────────────────────

    [Fact]
    public void A_transient_failure_is_retried_until_it_succeeds()
    {
        var tries = 0;

        AtomicFile.Retry(() => { if (++tries < 3) throw new UnauthorizedAccessException(); }, attempts: 4, delayMs: 0);

        // UnauthorizedAccessException specifically, because that is the one that was observed — and it is
        // NOT an IOException, so a filter that only caught IOException would have missed the real bug.
        Assert.Equal(3, tries);
    }

    [Fact]
    public void An_io_failure_is_retried_too()
    {
        var tries = 0;

        AtomicFile.Retry(() => { if (++tries < 2) throw new IOException("being used by another process"); }, attempts: 4, delayMs: 0);

        Assert.Equal(2, tries);
    }

    [Fact]
    public void Exhausting_the_attempts_rethrows_the_last_failure_unchanged()
    {
        // Assert.Same, not just the type: a genuine ACL denial or a read-only mount has to reach the
        // caller as itself, so the operator-facing message and the stack stay the ones that were thrown.
        var boom = new UnauthorizedAccessException("Access to the path is denied.");
        var tries = 0;

        var thrown = Assert.Throws<UnauthorizedAccessException>(() =>
            AtomicFile.Retry(() => { tries++; throw boom; }, attempts: 4, delayMs: 0));

        Assert.Same(boom, thrown);
        Assert.Equal(4, tries);
    }

    [Fact]
    public void A_failure_that_is_not_transient_is_not_retried()
    {
        var tries = 0;

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Retry(() => { tries++; throw new InvalidOperationException(); }, attempts: 4, delayMs: 0));

        Assert.Equal(1, tries);
    }

    [Fact]
    public void One_attempt_means_one_attempt()
    {
        var tries = 0;

        Assert.Throws<IOException>(() =>
            AtomicFile.Retry(() => { tries++; throw new IOException(); }, attempts: 1, delayMs: 0));

        Assert.Equal(1, tries);
    }

    [Fact]
    public void Fewer_than_one_attempt_is_a_programming_error()
    {
        // Not "never run it": a caller asking for zero attempts has miscomputed something.
        Assert.Throws<ArgumentOutOfRangeException>(() => AtomicFile.Retry(() => { }, attempts: 0, delayMs: 0));
    }

    [Fact]
    public async Task The_async_form_retries_the_same_way()
    {
        var tries = 0;

        await AtomicFile.RetryAsync(
            () => { if (++tries < 3) throw new UnauthorizedAccessException(); }, attempts: 4, delayMs: 0,
            CancellationToken.None);

        Assert.Equal(3, tries);
    }

    [Fact]
    public async Task The_async_form_also_rethrows_the_last_failure_unchanged()
    {
        var boom = new IOException("The process cannot access the file.");
        var tries = 0;

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            AtomicFile.RetryAsync(() => { tries++; throw boom; }, attempts: 3, delayMs: 0, CancellationToken.None));

        Assert.Same(boom, thrown);
        Assert.Equal(3, tries);
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_loop_even_with_no_delay_to_wait_through()
    {
        // The cancellation check is deliberately not inside the `if (delayMs > 0)`, so whether a token is
        // honoured doesn't depend on how the caller configured the backoff.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var tries = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicFile.RetryAsync(() => { tries++; throw new IOException(); }, attempts: 4, delayMs: 0, cts.Token));

        Assert.Equal(1, tries);
    }

    // ── The public wrappers actually move (portable) ──────────────────────────────────────────────

    [Fact]
    public void A_move_replaces_an_existing_destination_and_consumes_the_source()
    {
        // Proves overwrite: true is baked in — every caller relies on it, and File.Move defaults to false.
        var (source, destination) = Pair();

        AtomicFile.MoveWithRetry(source, destination);

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task The_async_move_replaces_an_existing_destination_too()
    {
        var (source, destination) = Pair();

        await AtomicFile.MoveWithRetryAsync(source, destination);

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    // ── A REAL sharing violation is in the retried set (Windows only) ─────────────────────────────

    [Fact]
    public void A_destination_freed_between_attempts_is_moved_successfully()
    {
        if (!OperatingSystem.IsWindows()) return; // rename(2) ignores open handles; nothing to retry

        var (source, destination) = Pair();
        using var holder = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
        if (!LockDeniesReaders(destination)) return;

        var tries = 0;
        AtomicFile.Retry(
            () =>
            {
                // Released from inside the operation, so "the handle went away between attempts" is a
                // fact about this thread's ordering rather than a race with a sleeping one.
                if (++tries == 2) holder.Dispose();
                File.Move(source, destination, overwrite: true);
            },
            attempts: 4, delayMs: 1);

        Assert.Equal(2, tries);
        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void A_destination_held_past_the_budget_still_surfaces_the_original_error()
    {
        if (!OperatingSystem.IsWindows()) return; // rename(2) ignores open handles; nothing to retry

        var (source, destination) = Pair();
        using var holder = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None);
        if (!LockDeniesReaders(destination)) return;

        var tries = 0;
        var thrown = Assert.ThrowsAny<Exception>(() => AtomicFile.Retry(
            () => { tries++; File.Move(source, destination, overwrite: true); }, attempts: 3, delayMs: 1));

        // The type check is what proves a real Windows sharing violation lands in IsTransient, and the
        // attempt count is what proves it was retried rather than failing straight out.
        Assert.True(thrown is IOException or UnauthorizedAccessException, thrown.GetType().Name);
        Assert.Equal(3, tries);

        // Nothing consumed and nothing half-published: the old destination is still the old destination.
        // Released first — the whole point of this handle is that it denies readers, this one included.
        holder.Dispose();
        Assert.True(File.Exists(source));
        Assert.Equal("old", File.ReadAllText(destination));
    }

    /// <summary>
    /// Whether the exclusive handle is actually enforced here, so a test that depends on it can bow out
    /// rather than pass for the wrong reason. Follows the guard in <c>GamePackageInstallerTests</c>.
    /// </summary>
    private static bool LockDeniesReaders(string path)
    {
        try
        {
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException) { return true; }
    }
}
