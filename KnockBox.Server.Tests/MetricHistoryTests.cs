using System.Diagnostics;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The dashboard's time series (spec §5.2): a bounded ring read through a cursor, so an open portal streams
/// one sample per tick and a portal just opened gets the whole retained hour.
/// </summary>
public class MetricHistoryTests
{
    private static MetricHistory.Sample Sample(int minute, double cpuSeconds = 0) => new(
        Sequence: 0, // stamped by Add
        At: new DateTimeOffset(2026, 8, 13, 12, minute, 0, TimeSpan.Zero),
        CpuSeconds: cpuSeconds,
        WorkingSetMb: 100 + minute,
        ManagedHeapMb: 20,
        Lobbies: minute,
        Players: minute * 2,
        GameSockets: minute,
        AuthorityLobbies: 0,
        Games: []);

    [Fact]
    public void A_fresh_history_holds_nothing_and_has_no_sequence()
    {
        var history = new MetricHistory(16);
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.LastSequence);
        Assert.Empty(history.Read());
    }

    [Fact]
    public void Samples_are_stamped_with_a_monotonic_sequence_and_read_oldest_first()
    {
        var history = new MetricHistory(16);
        for (var i = 0; i < 3; i++) history.Add(Sample(i));

        var all = history.Read();
        Assert.Equal([1L, 2L, 3L], all.Select(s => s.Sequence));
        Assert.Equal(3, history.LastSequence);
    }

    [Fact]
    public void A_cursor_returns_only_what_the_client_has_not_seen()
    {
        var history = new MetricHistory(16);
        for (var i = 0; i < 5; i++) history.Add(Sample(i));

        // What an open dashboard does on every poll: one sample crosses the wire, not the whole hour.
        var fresh = history.Read(afterSequence: 4);
        Assert.Single(fresh);
        Assert.Equal(5, fresh[0].Sequence);
        // And 0 — what a dashboard just opened sends — returns everything retained.
        Assert.Equal(5, history.Read(0).Count);
    }

    [Fact]
    public void The_ring_evicts_the_oldest_and_never_grows()
    {
        var history = new MetricHistory(8);
        for (var i = 0; i < 30; i++) history.Add(Sample(i));

        // The one new long-lived structure in this feature, so its bound is asserted rather than assumed.
        Assert.Equal(8, history.Count);
        var held = history.Read();
        Assert.Equal(8, held.Count);
        Assert.Equal([23L, 24L, 25L, 26L, 27L, 28L, 29L, 30L], held.Select(s => s.Sequence));
    }

    [Fact]
    public void A_cursor_older_than_everything_retained_just_returns_what_is_left()
    {
        var history = new MetricHistory(8);
        for (var i = 0; i < 30; i++) history.Add(Sample(i));

        // A dashboard that was closed for an hour asks for sequence 2; those samples are long gone. It gets
        // the retained window rather than an error or an empty answer.
        var result = history.Read(afterSequence: 2);
        Assert.Equal(8, result.Count);
    }

    [Fact]
    public void The_capacity_has_a_floor_so_a_misconfigured_zero_is_not_an_empty_ring()
    {
        Assert.True(new MetricHistory(0).Capacity >= 8);
        Assert.True(new MetricHistory(-5).Capacity >= 8);
    }
}

/// <summary>
/// Per-game authority cost — the only real per-game CPU this server has, because everything else runs in the
/// player's browser.
/// </summary>
public class AuthorityMetricsTests
{
    [Fact]
    public void A_game_that_never_ran_a_module_has_no_row_at_all()
    {
        var metrics = new AuthorityMetrics();
        // Not zero-with-a-row: a plain HTML5 game costs this process nothing, and inventing a 0.00s row for
        // every game in the catalog is how a measurement turns into noise.
        Assert.Null(metrics.For("tictactoe"));
        Assert.Empty(metrics.Snapshot());
    }

    [Fact]
    public void Calls_time_and_errors_accumulate_per_game()
    {
        var metrics = new AuthorityMetrics();
        var oneSecond = Stopwatch.Frequency;

        metrics.RecordCall("word-rush", oneSecond / 100);            // 10ms
        metrics.RecordCall("word-rush", oneSecond / 50);             // 20ms
        metrics.RecordCall("word-rush", oneSecond / 200, failed: true); // 5ms, threw

        var row = metrics.For("word-rush");
        Assert.NotNull(row);
        Assert.Equal(3, row.Value.Calls);
        Assert.Equal(1, row.Value.Errors);
        Assert.Equal(0.035, row.Value.CpuSeconds, precision: 3);
        Assert.Equal(11.667, row.Value.AverageCallMs, precision: 2);
        // The slowest single call, which an average over thousands of cheap ticks hides completely.
        Assert.Equal(20, row.Value.MaxCallMs, precision: 1);
    }

    [Fact]
    public void A_failed_call_still_counts_its_time()
    {
        var metrics = new AuthorityMetrics();
        metrics.RecordCall("word-rush", Stopwatch.Frequency / 10, failed: true);

        // It ran to the point of throwing — often longer than a success. Excluding it would understate a
        // module that mostly fails, which is exactly the module worth noticing.
        Assert.Equal(0.1, metrics.For("word-rush")!.Value.CpuSeconds, precision: 2);
    }

    [Fact]
    public void The_snapshot_is_busiest_first()
    {
        var metrics = new AuthorityMetrics();
        metrics.RecordCall("cheap", Stopwatch.Frequency / 1000);
        metrics.RecordCall("expensive", Stopwatch.Frequency);

        Assert.Equal(["expensive", "cheap"], metrics.Snapshot().Select(r => r.GameId));
    }

    [Fact]
    public void Game_ids_are_matched_case_insensitively_like_the_catalog()
    {
        var metrics = new AuthorityMetrics();
        metrics.RecordCall("Word-Rush", 100);
        Assert.NotNull(metrics.For("word-rush"));
    }

    [Fact]
    public void Pruning_drops_games_that_no_longer_exist()
    {
        var metrics = new AuthorityMetrics();
        metrics.RecordCall("kept", 100);
        metrics.RecordCall("uninstalled", 100);

        metrics.Prune(["kept"]);

        // An uninstalled game must not hold a dashboard row forever — the same rule RelayMetrics.Prune keeps.
        Assert.NotNull(metrics.For("kept"));
        Assert.Null(metrics.For("uninstalled"));
    }
}
