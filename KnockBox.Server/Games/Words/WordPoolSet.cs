namespace KnockBox.Server.Games.Words;

/// <summary>
/// A dictionary's worth of words spanning multiple lengths — the concrete <see cref="IWordPool"/>
/// handed to a lobby's authority runtime. Words are bucketed into per-length <see cref="WordPool"/>s;
/// a prefix-sum over the per-length counts exposes a contiguous global index
/// <c>[0, TotalWordCount)</c>, so a single random draw maps uniformly to a word.
/// </summary>
/// <remarks>
/// Port of <c>KnockBox.WordService/Services/CustomWordPool.cs</c> plus the length-bucketing that
/// lived in <c>WordListService.BuildByLength</c>, folded into <see cref="Build"/>. The packed byte
/// buffers make storage roughly the size of the raw word list, and the whole set is shared across
/// every lobby engine of a game (see <c>AuthorityWordService</c>).
/// </remarks>
public sealed class WordPoolSet : IWordPool
{
    private readonly IReadOnlyDictionary<int, WordPool> _byLength;
    private readonly int[] _lengths;       // distinct word lengths, sorted ascending
    private readonly int[] _cumulative;    // _cumulative[k] == total words in _lengths[0..k]

    public int TotalWordCount { get; }
    public IReadOnlyList<int> AvailableLengths => _lengths;

    private WordPoolSet(IReadOnlyDictionary<int, WordPool> byLength)
    {
        _byLength = byLength;
        _lengths = byLength.Keys.OrderBy(static x => x).ToArray();
        _cumulative = new int[_lengths.Length];

        var running = 0;
        for (var k = 0; k < _lengths.Length; k++)
        {
            running += byLength[_lengths[k]].WordCount;
            _cumulative[k] = running;
        }
        TotalWordCount = running;
    }

    /// <summary>
    /// Builds the set from a flat word list: blanks trimmed and skipped, bucketed by length, each
    /// bucket built into a <see cref="WordPool"/> with the given <paramref name="caseInsensitive"/>
    /// flag. Length buckets that end up empty (e.g. all non-ASCII) are dropped so
    /// <see cref="AvailableLengths"/> stays honest.
    /// </summary>
    public static WordPoolSet Build(IEnumerable<string> words, bool caseInsensitive = true)
    {
        var byLength = new Dictionary<int, List<string>>();
        foreach (var raw in words)
        {
            if (raw is null) continue;
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue;
            if (!byLength.TryGetValue(trimmed.Length, out var bucket))
            {
                bucket = [];
                byLength[trimmed.Length] = bucket;
            }
            bucket.Add(trimmed);
        }

        var result = new Dictionary<int, WordPool>(byLength.Count);
        foreach (var (length, bucket) in byLength)
        {
            var pool = WordPool.Build(length, bucket, caseInsensitive);
            if (pool.WordCount > 0) result[length] = pool;
        }
        return new WordPoolSet(result);
    }

    public int GetWordCount(int length)
        => _byLength.TryGetValue(length, out var pool) ? pool.WordCount : 0;

    public ReadOnlySpan<byte> GetWord(int length, int index)
    {
        if (!_byLength.TryGetValue(length, out var pool))
            throw new ArgumentOutOfRangeException(nameof(length), $"No words of length {length} in this pool.");
        return pool.GetWord(index);
    }

    public ReadOnlySpan<byte> GetWord(int globalIndex)
    {
        if ((uint)globalIndex >= (uint)TotalWordCount)
            throw new ArgumentOutOfRangeException(nameof(globalIndex));

        // Walk the (small) length buckets to the one whose cumulative range contains globalIndex;
        // #lengths is ~the word-length span, so this is cheap.
        var k = 0;
        while (_cumulative[k] <= globalIndex) k++;
        var localIndex = globalIndex - (k == 0 ? 0 : _cumulative[k - 1]);
        return _byLength[_lengths[k]].GetWord(localIndex);
    }

    public bool Contains(ReadOnlySpan<char> word)
        => _byLength.TryGetValue(word.Length, out var pool) && pool.Contains(word);
}
