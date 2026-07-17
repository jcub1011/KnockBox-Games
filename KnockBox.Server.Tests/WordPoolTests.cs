using System.Text;
using KnockBox.Server.Games.Words;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The packed-buffer word structures adapted from the sibling KnockBox.WordService repo, plus the
/// per-pool case-sensitivity flag and the WordPoolSet global-index scheme kb.words.pick relies on.
/// </summary>
public class WordPoolTests
{
    private static string Word(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);

    [Fact]
    public void Build_dedupes_trims_and_lowercases_when_case_insensitive()
    {
        var pool = WordPool.Build(5, ["Apple", " APPLE ", "brave", "crane", ""]);

        Assert.Equal(5, pool.WordLength);
        Assert.Equal(3, pool.WordCount);
        Assert.True(pool.Contains("apple"));
        Assert.True(pool.Contains("APPLE"));
        Assert.True(pool.Contains("brave"));
    }

    [Fact]
    public void Build_skips_words_of_the_wrong_length()
    {
        var pool = WordPool.Build(4, ["tree", "hello", "blue", "x"]);
        Assert.Equal(2, pool.WordCount);
        Assert.True(pool.Contains("tree"));
        Assert.True(pool.Contains("blue"));
        Assert.False(pool.Contains("hello"));
    }

    [Fact]
    public void Build_skips_non_ascii_source_words()
    {
        // "café" is length 4 but non-ASCII; it must not be stored (ASCII encoding would corrupt it)
        // and must not be findable.
        var pool = WordPool.Build(4, ["café", "tree"]);
        Assert.Equal(1, pool.WordCount);
        Assert.True(pool.Contains("tree"));
        Assert.False(pool.Contains("café"));
    }

    [Fact]
    public void Contains_is_case_insensitive_by_default()
    {
        var pool = WordPool.Build(5, ["apple"]);
        Assert.True(pool.Contains("APPLE"));
        Assert.True(pool.Contains("Apple"));
        Assert.True(pool.Contains("aPpLe"));
    }

    [Fact]
    public void Case_sensitive_pool_keeps_original_case_and_matches_exactly()
    {
        var pool = WordPool.Build(5, ["Apple", "apple"], caseInsensitive: false);
        Assert.Equal(2, pool.WordCount);
        Assert.True(pool.Contains("Apple"));
        Assert.True(pool.Contains("apple"));
        Assert.False(pool.Contains("APPLE"));
        // Ordinal sort: 'A' (65) precedes 'a' (97).
        Assert.Equal("Apple", Word(pool.GetWord(0)));
        Assert.Equal("apple", Word(pool.GetWord(1)));
    }

    [Fact]
    public void Contains_is_false_for_missing_wrong_length_empty_and_non_ascii_queries()
    {
        var pool = WordPool.Build(5, ["apple", "brave"]);
        Assert.False(pool.Contains("crane"));
        Assert.False(pool.Contains("app"));
        Assert.False(pool.Contains("apples"));
        Assert.False(pool.Contains([]));
        Assert.False(pool.Contains("applé"));
    }

    [Fact]
    public void GetWord_returns_words_in_sorted_order_and_throws_out_of_range()
    {
        var pool = WordPool.Build(5, ["crane", "apple", "brave"]);
        Assert.Equal("apple", Word(pool.GetWord(0)));
        Assert.Equal("brave", Word(pool.GetWord(1)));
        Assert.Equal("crane", Word(pool.GetWord(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = pool.GetWord(-1); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = pool.GetWord(3); });
    }

    // ── WordPoolSet (multi-length, global index) ──────────────────────────────

    [Fact]
    public void Set_global_index_walks_length_buckets_ascending_then_ordinal()
    {
        var set = WordPoolSet.Build(["dog", "be", "cat", "ax"]);

        Assert.Equal(4, set.TotalWordCount);
        Assert.Equal([2, 3], set.AvailableLengths);
        // length 2 bucket (ax, be) then length 3 bucket (cat, dog).
        Assert.Equal("ax", Word(set.GetWord(0)));
        Assert.Equal("be", Word(set.GetWord(1)));
        Assert.Equal("cat", Word(set.GetWord(2)));
        Assert.Equal("dog", Word(set.GetWord(3)));
    }

    [Fact]
    public void Set_per_length_count_and_pick_and_membership()
    {
        var set = WordPoolSet.Build(["dog", "be", "cat", "ax"]);
        Assert.Equal(2, set.GetWordCount(2));
        Assert.Equal(2, set.GetWordCount(3));
        Assert.Equal(0, set.GetWordCount(4));
        Assert.Equal("cat", Word(set.GetWord(3, 0)));
        Assert.True(set.Contains("ax"));
        Assert.False(set.Contains("zzz"));
    }

    [Fact]
    public void Shared_fixture_pick_sequence_matches_the_local_emulation()
    {
        // SHARED FIXTURE — must stay byte-identical to the JS parity test in
        // clients/phaser/__tests__/knockbox-local-words.test.js. Includes a case dupe (Dog/dog), a
        // fold (CAT), and a non-ASCII word (café, skipped).
        string[] fixture = ["Dog", "be", "CAT", "ax", "dog", "eel", "café"];
        string[] expected = ["ax", "be", "cat", "dog", "eel"]; // length asc, ordinal within

        var set = WordPoolSet.Build(fixture); // caseInsensitive default
        Assert.Equal(expected.Length, set.TotalWordCount);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], Word(set.GetWord(i)));
    }

    [Fact]
    public void Set_global_index_throws_out_of_range()
    {
        var set = WordPoolSet.Build(["ax", "be"]);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = set.GetWord(2); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = set.GetWord(-1); });
    }
}
