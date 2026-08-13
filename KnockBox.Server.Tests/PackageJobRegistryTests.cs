using KnockBox.Server.Games;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The package job feed: the monotonic cursor the portal polls, the retention that lets an operator
/// come back to an outcome, and the cancellation boundary that refuses to create a half-swapped game.
/// </summary>
public class PackageJobRegistryTests
{
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.UnixEpoch);

    private PackageJobRegistry New(int retention = PackageJobRegistry.DefaultRetention) => new(_clock, retention);

    private PackageJob Start(PackageJobRegistry registry, string gameId = "demo") =>
        registry.Create(PackageJobKind.Install, PackageJobSource.Marketplace, gameId, gameId,
            null, "1.0.0", PackageApplyMode.Auto);

    [Fact]
    public void A_new_job_starts_queued_with_a_sequence()
    {
        var registry = New();

        var job = Start(registry);

        Assert.Equal(PackageJobStatus.Queued, job.Status);
        Assert.True(job.Sequence > 0);
        Assert.Equal(job.Sequence, registry.LastSequence);
        Assert.Null(job.EndedAt);
        Assert.False(job.IsTerminal);
    }

    [Fact]
    public void Every_mutation_bumps_the_sequence()
    {
        // The whole change feed rests on this: a poll that sees no higher sequence must be able to
        // conclude nothing moved.
        var registry = New();
        var job = Start(registry);
        var seen = new List<long> { job.Sequence };

        seen.Add(registry.SetStatus(job.JobId, PackageJobStatus.Downloading, "Downloading.")!.Sequence);
        registry.Progress(job.JobId, 50, 100);
        seen.Add(registry.LastSequence);
        seen.Add(registry.Finish(job.JobId, PackageJobStatus.Succeeded, "Installed.")!.Sequence);

        Assert.Equal(seen.OrderBy(s => s).ToList(), seen);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    [Fact]
    public void Read_returns_only_what_changed_since_the_cursor()
    {
        var registry = New();
        var a = Start(registry, "alpha");
        var cursor = registry.LastSequence;
        var b = Start(registry, "beta");

        var changed = registry.Read(cursor);

        Assert.Equal([b.JobId], changed.Select(j => j.JobId));
        Assert.Empty(registry.Read(registry.LastSequence));
        Assert.Equal(2, registry.Read(0).Count);
        Assert.Contains(a.JobId, registry.Read(0).Select(j => j.JobId));
    }

    [Fact]
    public void Read_with_a_non_positive_limit_returns_nothing()
    {
        var registry = New();
        Start(registry);

        Assert.Empty(registry.Read(0, 0));
        Assert.Empty(registry.Read(0, -1));
    }

    [Fact]
    public void Progress_is_throttled_but_the_final_byte_always_publishes()
    {
        var registry = New();
        var job = Start(registry);
        registry.SetStatus(job.JobId, PackageJobStatus.Downloading, "Downloading.");
        var before = registry.LastSequence;

        registry.Progress(job.JobId, 10, 100);
        var afterFirst = registry.LastSequence;
        registry.Progress(job.JobId, 20, 100); // immediately after: throttled away
        var afterSecond = registry.LastSequence;
        registry.Progress(job.JobId, 100, 100); // complete: published regardless

        Assert.True(afterFirst > before);
        Assert.Equal(afterFirst, afterSecond);
        Assert.True(registry.LastSequence > afterSecond);
        Assert.Equal(100, registry.Get(job.JobId)!.BytesDone);
    }

    [Fact]
    public void Progress_resumes_once_the_throttle_interval_has_passed()
    {
        var registry = New();
        var job = Start(registry);
        registry.Progress(job.JobId, 10, 1000);
        var after = registry.LastSequence;

        _clock.Advance(TimeSpan.FromSeconds(1));
        registry.Progress(job.JobId, 20, 1000);

        Assert.True(registry.LastSequence > after);
    }

    [Fact]
    public void Finishing_stamps_an_end_time_and_is_idempotent()
    {
        var registry = New();
        var job = Start(registry);
        _clock.Advance(TimeSpan.FromSeconds(30));

        var finished = registry.Finish(job.JobId, PackageJobStatus.Failed, "Failed.", "the download failed");
        var sequence = registry.LastSequence;
        var again = registry.Finish(job.JobId, PackageJobStatus.Succeeded, "Installed.");

        Assert.Equal(PackageJobStatus.Failed, finished!.Status);
        Assert.Equal("the download failed", finished.Error);
        Assert.Equal(_clock.GetUtcNow(), finished.EndedAt);
        // The first terminal transition wins: a late success must not overwrite a recorded failure.
        Assert.Equal(PackageJobStatus.Failed, again!.Status);
        Assert.Equal(sequence, registry.LastSequence);
    }

    [Fact]
    public void A_terminal_job_ignores_further_mutations()
    {
        var registry = New();
        var job = Start(registry);
        registry.Finish(job.JobId, PackageJobStatus.Succeeded, "Installed.");
        var sequence = registry.LastSequence;

        registry.SetStatus(job.JobId, PackageJobStatus.Downloading, "Downloading.");
        registry.Progress(job.JobId, 5, 10);

        Assert.Equal(sequence, registry.LastSequence);
        Assert.Equal(PackageJobStatus.Succeeded, registry.Get(job.JobId)!.Status);
    }

    [Fact]
    public void ActiveFor_finds_a_running_job_case_insensitively_and_forgets_a_finished_one()
    {
        var registry = New();
        var job = Start(registry, "demo");

        Assert.Equal(job.JobId, registry.ActiveFor("DEMO")!.JobId);
        Assert.Equal(1, registry.ActiveCount);

        registry.Finish(job.JobId, PackageJobStatus.Succeeded, "Installed.");

        // What turns a second click into a 409 while work is in flight, and lets the next one through
        // once it is not.
        Assert.Null(registry.ActiveFor("demo"));
        Assert.Equal(0, registry.ActiveCount);
    }

    [Theory]
    [InlineData(PackageJobStatus.Queued)]
    [InlineData(PackageJobStatus.Downloading)]
    [InlineData(PackageJobStatus.Verifying)]
    [InlineData(PackageJobStatus.WaitingForLobbies)]
    public void Cancel_is_honoured_before_the_files_are_touched(PackageJobStatus status)
    {
        var registry = New();
        var job = Start(registry);
        registry.SetStatus(job.JobId, status, status.ToString());
        var token = registry.TokenFor(job.JobId);

        Assert.Equal(PackageCancelOutcome.Cancelled, registry.Cancel(job.JobId));
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_is_refused_once_the_job_is_applying()
    {
        // The point of no return. A half-swapped game directory is the one outcome worth refusing to
        // create — the same reasoning AdminOperations.DeleteGame applies to a half-delete.
        var registry = New();
        var job = Start(registry);
        registry.SetStatus(job.JobId, PackageJobStatus.Applying, "Installing files.");
        var token = registry.TokenFor(job.JobId);

        Assert.Equal(PackageCancelOutcome.TooLate, registry.Cancel(job.JobId));
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_reports_an_unknown_job_and_a_finished_one_differently()
    {
        var registry = New();
        var job = Start(registry);
        registry.Finish(job.JobId, PackageJobStatus.Succeeded, "Installed.");

        Assert.Equal(PackageCancelOutcome.NotFound, registry.Cancel("nope"));
        Assert.Equal(PackageCancelOutcome.TooLate, registry.Cancel(job.JobId));
    }

    [Fact]
    public void Retention_drops_the_oldest_finished_jobs()
    {
        var registry = New(retention: 4);

        for (var i = 0; i < 8; i++)
        {
            var job = Start(registry, $"game-{i}");
            _clock.Advance(TimeSpan.FromSeconds(1));
            registry.Finish(job.JobId, PackageJobStatus.Succeeded, "Installed.");
        }

        Assert.Equal(4, registry.Count);
        var kept = registry.Snapshot().Select(j => j.GameId).ToList();
        Assert.Equal(["game-7", "game-6", "game-5", "game-4"], kept);
    }

    [Fact]
    public void Retention_never_evicts_an_active_job()
    {
        // Evicting a running job would strand it: nothing left to observe it with, and no way to cancel
        // it. Exceeding the cap is the lesser problem, so the cap is deliberately soft.
        var registry = New(retention: 4);
        var active = new List<PackageJob>();
        for (var i = 0; i < 8; i++) active.Add(Start(registry, $"game-{i}"));

        Assert.Equal(8, registry.Count);
        foreach (var job in active) Assert.NotNull(registry.Get(job.JobId));
    }

    [Fact]
    public void Concurrent_jobs_get_distinct_ids_and_sequences()
    {
        var registry = New(retention: 500);

        Parallel.For(0, 200, i => Start(registry, $"game-{i}"));

        var all = registry.Snapshot();
        Assert.Equal(200, all.Count);
        Assert.Equal(200, all.Select(j => j.JobId).Distinct().Count());
        Assert.Equal(200, all.Select(j => j.Sequence).Distinct().Count());
    }

    [Fact]
    public void An_unknown_job_reads_as_null_and_carries_no_token()
    {
        var registry = New();

        Assert.Null(registry.Get("nope"));
        Assert.Null(registry.Mutate("nope", j => j));
        Assert.Equal(CancellationToken.None, registry.TokenFor("nope"));
    }
}
