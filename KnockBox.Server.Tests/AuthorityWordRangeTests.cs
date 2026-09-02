using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// <c>kb.words.rangeOfPrefix</c> / <c>pickRange</c> — the two primitives that move a word game's hot
/// loop to the side of the sandbox boundary that holds the bytes.
///
/// <para>Every word game needs the same two things: the bounds of "words of length L starting with P",
/// and then a lot of words out of that range. With only <c>pickOfLength</c> to reach the data, both are
/// written in JavaScript — a binary search whose every probe is an interpreted iteration plus a
/// marshalled string, then a loop that crosses the boundary once per candidate. On a shipped module
/// resolving all 26 starting letters across 14 lengths, that cost 3,298 crossings; through
/// <c>rangeOfPrefix</c> it is 364.</para>
/// </summary>
public class AuthorityWordRangeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-wordrange-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsAuthorityRuntime> _runtimes = [];
    private readonly AuthorityModuleCache _modules = new(TimeProvider.System);

    public AuthorityWordRangeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var r in _runtimes) r.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly string[] Words =
    [
        // length 3: two 'c' words, one 'a', one 'z'
        "cat", "cow", "ant", "zip",
        // length 4: a deliberate run of 'ca' inside a wider run of 'c'
        "cake", "calm", "cart", "chip", "clip", "able", "zone",
    ];

    private static IReadOnlyDictionary<string, IWordPool> Pool(bool caseInsensitive = true) =>
        new Dictionary<string, IWordPool> { ["en"] = WordPoolSet.Build(Words, caseInsensitive) };

    private JsAuthorityRuntime Load(string source, AuthorityOptions? options = null,
        IReadOnlyDictionary<string, IWordPool>? words = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, source);
        var runtime = new JsAuthorityRuntime(
            path, _modules, options ?? AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs()),
            TimeProvider.System, words ?? Pool(), "g");
        _runtimes.Add(runtime);
        runtime.Initialize("""[{"id":"p1","displayName":"Ann"}]""");
        return runtime;
    }

    /// <summary>Runs one expression against kb.words and returns the JSON it produced.</summary>
    private string Eval(string expression, AuthorityOptions? options = null,
        IReadOnlyDictionary<string, IWordPool>? words = null) =>
        Load($$"""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { return { v: {{expression}} }; },
                snapshot() { return {}; },
              };
            }
            """, options, words).Invoke("applyIntent", "\"p1\"", "{}");

    // ── The pool itself ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_range_brackets_exactly_the_words_carrying_the_prefix()
    {
        var pool = WordPoolSet.Build(Words);

        // Length 4, sorted ordinal: able, cake, calm, cart, chip, clip, zone
        Assert.Equal((1, 6), pool.RangeOfPrefix(4, "c"));    // cake..clip
        Assert.Equal((1, 4), pool.RangeOfPrefix(4, "ca"));   // cake, calm, cart — the run inside the run
        Assert.Equal((1, 2), pool.RangeOfPrefix(4, "cake")); // a full word is just a prefix of length 4
        Assert.Equal((0, 1), pool.RangeOfPrefix(4, "a"));
        Assert.Equal((6, 7), pool.RangeOfPrefix(4, "z"));    // the last run, where an off-by-one would show
    }

    [Fact]
    public void An_empty_prefix_is_the_whole_bucket_and_a_missing_one_is_empty()
    {
        var pool = WordPoolSet.Build(Words);

        Assert.Equal((0, 7), pool.RangeOfPrefix(4, ""));     // every word has the empty prefix

        // "Empty" is start == end, NOT (0, 0): a prefix that sorts between two runs lands at its
        // insertion point, so `q` answers (6, 6) — after clip, before zone. Asserting the position would
        // pin an implementation detail; asserting emptiness is the contract a caller can rely on, and it
        // is what makes `for (let i = start; i < end; i++)` correct without a special case.
        foreach (var (len, prefix) in new[]
                 {
                     (4, "q"),      // no q words, but a well-defined place where they would go
                     (9, "c"),      // no words of that length at all
                     (4, "cakes"),  // longer than the bucket: names nothing storable
                     (4, "é"),      // non-ASCII can never be stored
                 })
        {
            var (start, end) = pool.RangeOfPrefix(len, prefix);
            Assert.Equal(start, end);
            Assert.InRange(start, 0, pool.GetWordCount(len));
        }
    }

    [Fact]
    public void The_range_folds_case_exactly_as_Contains_does()
    {
        // A case-insensitive pool stores lower-case and folds the query; a case-sensitive one must not,
        // or a range would silently span the wrong words rather than returning none.
        Assert.Equal((1, 4), WordPoolSet.Build(Words, caseInsensitive: true).RangeOfPrefix(4, "CA"));
        Assert.Equal((0, 0), WordPoolSet.Build(Words, caseInsensitive: false).RangeOfPrefix(4, "CA"));
    }

    [Fact]
    public void The_range_agrees_with_walking_the_bucket_by_hand()
    {
        // The property that matters: the bounds are exactly the words a linear scan would have accepted.
        // Cheap to state, and it is what a subtle binary-search bug would break.
        var pool = WordPoolSet.Build(Words);
        foreach (var len in new[] { 3, 4 })
        foreach (var prefix in new[] { "a", "c", "ca", "ch", "z", "q", "" })
        {
            var (start, end) = pool.RangeOfPrefix(len, prefix);
            var expected = Enumerable.Range(0, pool.GetWordCount(len))
                .Select(i => System.Text.Encoding.ASCII.GetString(pool.GetWord(len, i)))
                .Where(w => w.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();

            Assert.Equal(expected.Count, end - start);
            for (var i = 0; i < expected.Count; i++)
                Assert.Equal(expected[i], System.Text.Encoding.ASCII.GetString(pool.GetWord(len, start + i)));
        }
    }

    // ── The capability as a module sees it ───────────────────────────────────────────

    [Fact]
    public void A_module_gets_bounds_it_can_feed_straight_back_to_pickOfLength()
    {
        // The contract that makes the primitive useful: the indices address the same space as
        // pickOfLength, so a range is directly usable as draw bounds.
        Assert.Equal("""{"v":["cake","calm","cart"]}""", Eval("""
            (function () {
              const [start, end] = kb.words.rangeOfPrefix('en', 4, 'ca');
              const out = [];
              for (let i = start; i < end; i++) out.push(kb.words.pickOfLength('en', 4, i));
              return out;
            })()
            """));
    }

    [Fact]
    public void PickRange_returns_the_same_slice_in_one_call()
    {
        Assert.Equal("""{"v":["cake","calm","cart"]}""", Eval("kb.words.pickRange('en', 4, 1, 3)"));
    }

    [Fact]
    public void PickRange_clamps_to_the_bucket_rather_than_returning_holes()
    {
        // Asking past the end yields what exists, not nulls: a module iterating the result must not have
        // to filter it, and the array's own length is how it learns it got fewer than it asked for.
        Assert.Equal("""{"v":["chip","clip","zone"]}""", Eval("kb.words.pickRange('en', 4, 4, 99)"));
        Assert.Equal("""{"v":[]}""", Eval("kb.words.pickRange('en', 4, 99, 5)"));
        Assert.Equal("""{"v":[]}""", Eval("kb.words.pickRange('en', 4, 0, 0)"));
        Assert.Equal("""{"v":[]}""", Eval("kb.words.pickRange('en', 9, 0, 5)"));  // no such length
    }

    [Fact]
    public void PickRange_is_capped_so_one_call_cannot_conjure_the_whole_dictionary()
    {
        // Bounds both the JS array a module can build in a single crossing and the strings the host
        // allocates to fill it. Without it, `pickRange(dict, len, 0, 400000)` is one call.
        var options = AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityMaxWordsPerCall", "2")));
        Assert.Equal("""{"v":["able","cake"]}""", Eval("kb.words.pickRange('en', 4, 0, 99)", options));
    }

    [Fact]
    public void Both_primitives_are_guarded_like_the_rest_of_the_surface()
    {
        // Unknown dictionary / bad argument shapes return null rather than throwing: a CLR throw out of a
        // ClrFunction would surface as a fatal module failure, which is the design's §7 rule.
        Assert.Equal("""{"v":null}""", Eval("kb.words.rangeOfPrefix('nope', 4, 'c')"));
        Assert.Equal("""{"v":null}""", Eval("kb.words.rangeOfPrefix('en', 4, 42)"));
        Assert.Equal("""{"v":null}""", Eval("kb.words.rangeOfPrefix('en')"));
        Assert.Equal("""{"v":null}""", Eval("kb.words.pickRange('nope', 4, 0, 1)"));
        Assert.Equal("""{"v":null}""", Eval("kb.words.pickRange('en', 4, 0)"));
    }

    [Fact]
    public void The_new_members_are_frozen_with_the_rest_of_kb_words()
    {
        Assert.Equal("""{"v":true}""", Eval("""
            (function () {
              try { kb.words.rangeOfPrefix = () => [0, 999999]; return false; } catch (e) { return true; }
            })()
            """));
    }
}
