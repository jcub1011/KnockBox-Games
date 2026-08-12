using KnockBox.Server.Admin;
using Serilog;
using Serilog.Events;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Pins the in-memory log sink the portal's live view reads from: the ring's eviction, the cursor that
/// makes polling a stream, the filters, and the literal message rendering that keeps it agreeing with the
/// rolling log file.
/// </summary>
public class AdminLogBufferTests
{
    // Drives the REAL Serilog pipeline into the sink, so the test exercises the same rendering and
    // property enrichment production does rather than hand-built LogEvents.
    private static (AdminLogBuffer Buffer, ILogger Logger) Rig(int capacity = AdminLogBuffer.DefaultCapacity)
    {
        var buffer = new AdminLogBuffer(capacity);
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(buffer)
            .CreateLogger();
        return (buffer, logger);
    }

    [Fact]
    public void An_empty_buffer_reads_as_empty_with_no_cursor()
    {
        var (buffer, _) = Rig();
        Assert.Equal(0, buffer.LastSequence);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.TotalWritten);
        Assert.Empty(buffer.Read());
    }

    [Fact]
    public void Entries_are_captured_with_level_category_and_message()
    {
        var (buffer, logger) = Rig();
        logger.ForContext("SourceContext", "KnockBox.GameLog").Warning("Something odd happened");

        var entry = Assert.Single(buffer.Read());
        Assert.Equal(1, entry.Sequence);
        Assert.Equal(LogEventLevel.Warning, entry.Level);
        Assert.Equal("KnockBox.GameLog", entry.Category); // unquoted — Serilog renders string props with quotes
        Assert.Equal("Something odd happened", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void String_properties_render_literally_so_the_view_matches_the_log_file()
    {
        var (buffer, logger) = Rig();
        logger.Information("Skipping game {GameId}: no entry file", "dice-simulator");

        // The file sink uses "{Message:lj}". Serilog's own RenderMessage() would produce
        // 'Skipping game "dice-simulator"', so the portal and the log file would disagree about the same
        // event — which is exactly the kind of difference that wastes an operator's afternoon.
        Assert.Equal("Skipping game dice-simulator: no entry file", Assert.Single(buffer.Read()).Message);
    }

    [Fact]
    public void Sequences_are_monotonic_and_the_cursor_returns_only_newer_entries()
    {
        var (buffer, logger) = Rig();
        for (var i = 1; i <= 5; i++) logger.Information("event {N}", i);

        Assert.Equal(5, buffer.LastSequence);
        var tail = buffer.Read(afterSequence: 3);
        Assert.Equal([4L, 5L], tail.Select(e => e.Sequence));
    }

    [Fact]
    public void The_ring_evicts_the_oldest_entries_and_still_reports_the_true_total()
    {
        var (buffer, logger) = Rig(capacity: 16); // 16 is the floor the buffer clamps to
        for (var i = 1; i <= 40; i++) logger.Information("event {N}", i);

        Assert.Equal(16, buffer.Count);
        Assert.Equal(40, buffer.TotalWritten); // so the portal can say "the last 16 of 40", not imply it has all
        var all = buffer.Read(limit: 100);
        Assert.Equal(16, all.Count);
        Assert.Equal(25, all[0].Sequence); // 40 - 16 + 1: the oldest survivor
        Assert.Equal(40, all[^1].Sequence);
        // Oldest-first, with no visible wrap point despite the circular storage.
        Assert.Equal(all.Select(e => e.Sequence).Order(), all.Select(e => e.Sequence));
    }

    [Fact]
    public void Filtering_by_level_returns_that_level_and_everything_more_severe()
    {
        var (buffer, logger) = Rig();
        logger.Debug("debug");
        logger.Information("info");
        logger.Warning("warn");
        logger.Error("error");

        var atLeastWarning = buffer.Read(minLevel: LogEventLevel.Warning);
        Assert.Equal(["warn", "error"], atLeastWarning.Select(e => e.Message));
    }

    [Fact]
    public void Filtering_by_category_matches_a_substring_case_insensitively()
    {
        var (buffer, logger) = Rig();
        logger.ForContext("SourceContext", "KnockBox.GameLog").Information("from a game");
        logger.ForContext("SourceContext", "KnockBox.Server.Games.GameCatalog").Information("from the catalog");

        // The operator types "gamelog", not the fully-qualified category.
        Assert.Equal("from a game", Assert.Single(buffer.Read(category: "gamelog")).Message);
    }

    [Fact]
    public void Searching_matches_the_message_or_the_exception_text()
    {
        var (buffer, logger) = Rig();
        logger.Information("nothing to see");
        logger.Error(new InvalidOperationException("disk is full"), "write failed");

        Assert.Equal("write failed", Assert.Single(buffer.Read(search: "WRITE")).Message);
        // The interesting words are often only in the exception, so it is searched too.
        Assert.Single(buffer.Read(search: "disk is full"));
    }

    [Fact]
    public void An_exception_is_captured_alongside_its_message()
    {
        var (buffer, logger) = Rig();
        logger.Error(new InvalidOperationException("boom"), "it broke");

        var entry = Assert.Single(buffer.Read());
        Assert.NotNull(entry.Exception);
        Assert.Contains("boom", entry.Exception);
    }

    [Fact]
    public void When_more_entries_match_than_the_limit_the_newest_win()
    {
        var (buffer, logger) = Rig();
        for (var i = 1; i <= 10; i++) logger.Information("event {N}", i);

        var page = buffer.Read(limit: 3);
        // A tail shows the end, not the beginning: a viewer that has been away must not be pinned to the
        // oldest three events forever.
        Assert.Equal([8L, 9L, 10L], page.Select(e => e.Sequence));
    }

    [Fact]
    public void A_zero_or_negative_limit_returns_nothing_rather_than_everything()
    {
        var (buffer, logger) = Rig();
        logger.Information("something");
        Assert.Empty(buffer.Read(limit: 0));
        Assert.Empty(buffer.Read(limit: -5));
    }

    [Fact]
    public void An_enormous_message_is_truncated_so_one_line_cannot_hold_the_ring()
    {
        var (buffer, logger) = Rig();
        logger.Information("{Blob}", new string('x', 50_000));

        var entry = Assert.Single(buffer.Read());
        Assert.True(entry.Message.Length < 5_000, $"message was {entry.Message.Length} chars");
        Assert.EndsWith("…", entry.Message);
    }

    [Fact]
    public void Concurrent_writers_all_land_with_distinct_sequences()
    {
        var (buffer, logger) = Rig(capacity: 4096);
        // Serilog calls the sink on whichever thread logged, including several at once under load.
        Parallel.For(0, 500, i => logger.Information("event {N}", i));

        Assert.Equal(500, buffer.TotalWritten);
        var sequences = buffer.Read(limit: 1000).Select(e => e.Sequence).ToList();
        Assert.Equal(500, sequences.Count);
        Assert.Equal(500, sequences.Distinct().Count());
    }
}
