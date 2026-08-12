using KnockBox.Server.Networking;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Pins the per-game relay counters. They exist because a game running in the player's browser is NOT
/// free server-side: every socket holds a bounded outbound channel plus a writer task, and a broadcast
/// serializes once then sends once per recipient.
/// </summary>
public class RelayMetricsTests
{
    [Fact]
    public void A_game_with_no_traffic_has_no_row()
    {
        Assert.Empty(new RelayMetrics().Snapshot());
    }

    [Fact]
    public void A_relayed_frame_counts_once_in_and_once_per_recipient_out()
    {
        var metrics = new RelayMetrics();
        metrics.RecordRelay("ttt", recipients: 4, bytes: 100);

        var game = Assert.Single(metrics.Snapshot());
        Assert.Equal("ttt", game.GameId);
        Assert.Equal(1, game.FramesIn);
        Assert.Equal(4, game.FramesOut);
        Assert.Equal(100, game.BytesIn);
        // The point of the pair: one 100-byte broadcast to four players costs 400 bytes of egress.
        Assert.Equal(400, game.BytesOut);
        Assert.Equal(4.0, game.FanOut);
    }

    [Fact]
    public void A_frame_with_no_recipients_still_counts_as_received()
    {
        var metrics = new RelayMetrics();
        metrics.RecordRelay("ttt", recipients: 0, bytes: 50);

        var game = Assert.Single(metrics.Snapshot());
        // A frame nobody was there to receive still cost a parse and a serialize, so it is not invisible —
        // but it produced no egress.
        Assert.Equal(1, game.FramesIn);
        Assert.Equal(0, game.FramesOut);
        Assert.Equal(50, game.BytesIn);
        Assert.Equal(0, game.BytesOut);
        Assert.Equal(0, game.FanOut);
    }

    [Fact]
    public void Counters_accumulate_and_are_kept_per_game()
    {
        var metrics = new RelayMetrics();
        metrics.RecordRelay("ttt", 2, 10);
        metrics.RecordRelay("ttt", 3, 10);
        metrics.RecordRelay("other", 1, 10);
        metrics.RecordDropped("ttt");

        var snapshot = metrics.Snapshot();
        Assert.Equal(2, snapshot.Count);
        var ttt = snapshot.Single(g => g.GameId == "ttt");
        Assert.Equal(2, ttt.FramesIn);
        Assert.Equal(5, ttt.FramesOut);
        Assert.Equal(1, ttt.FramesDropped);
        Assert.Equal(2.5, ttt.FanOut);
        Assert.Equal(1, snapshot.Single(g => g.GameId == "other").FramesOut);
    }

    [Fact]
    public void Game_ids_are_pooled_case_insensitively_like_the_catalog()
    {
        var metrics = new RelayMetrics();
        metrics.RecordRelay("TicTacToe", 1, 10);
        metrics.RecordRelay("tictactoe", 1, 10);

        // Otherwise one game would occupy two rows whose numbers each tell half the story.
        Assert.Equal(2, Assert.Single(metrics.Snapshot()).FramesIn);
    }

    [Fact]
    public void The_snapshot_is_ordered_busiest_first()
    {
        var metrics = new RelayMetrics();
        metrics.RecordRelay("quiet", 1, 10);
        metrics.RecordRelay("busy", 50, 10);
        metrics.RecordRelay("middling", 5, 10);

        Assert.Equal(["busy", "middling", "quiet"], metrics.Snapshot().Select(g => g.GameId));
    }

    [Fact]
    public void Pruning_drops_counters_for_games_that_no_longer_exist()
    {
        var metrics = new RelayMetrics();
        metrics.RecordRelay("kept", 1, 10);
        metrics.RecordRelay("uninstalled", 1, 10);

        metrics.Prune(["KEPT"]); // case-insensitive, matching GameCatalog's own id comparison

        Assert.Equal("kept", Assert.Single(metrics.Snapshot()).GameId);
    }

    [Fact]
    public void Concurrent_relays_are_all_counted()
    {
        var metrics = new RelayMetrics();
        // The relay records from whichever socket thread handled the frame, several at once under load.
        Parallel.For(0, 1000, _ => metrics.RecordRelay("ttt", 2, 10));

        var game = Assert.Single(metrics.Snapshot());
        Assert.Equal(1000, game.FramesIn);
        Assert.Equal(2000, game.FramesOut);
        Assert.Equal(20_000, game.BytesOut);
    }
}
