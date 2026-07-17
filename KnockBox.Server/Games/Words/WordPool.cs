using System.Text;

namespace KnockBox.Server.Games.Words;

/// <summary>
/// Read-only set of fixed-length ASCII words backed by a packed byte buffer. Words live at
/// <c>_buffer[i*WordLength .. (i+1)*WordLength)</c>, sorted ordinal. <see cref="Contains"/> is
/// O(log N) and allocation-free (it folds and compares the query span in place, so there is no
/// per-query buffer and no word-length limit); <see cref="GetWord"/> is O(1).
/// </summary>
/// <remarks>
/// Adapted from <c>KnockBox.WordService/Services/WordPool.cs</c> with a per-pool
/// <c>caseInsensitive</c> flag. AOT-clean: only <see cref="Encoding.ASCII"/>, <c>stackalloc</c>, and
/// <c>byte[]</c> — no reflection — so it publishes clean under the <c>aot</c> CI gate.
/// </remarks>
public sealed class WordPool
{
    public int WordLength { get; }
    private readonly byte[] _buffer;
    private readonly bool _caseInsensitive;

    public int WordCount => _buffer.Length / WordLength;

    private WordPool(int wordLength, byte[] buffer, bool caseInsensitive)
    {
        WordLength = wordLength;
        _buffer = buffer;
        _caseInsensitive = caseInsensitive;
    }

    /// <summary>
    /// Builds a pool from <paramref name="words"/>: trimmed, filtered to exactly
    /// <paramref name="wordLength"/> ASCII characters, deduped, sorted ordinal. When
    /// <paramref name="caseInsensitive"/> is true the words are lowercased on build and queries fold
    /// <c>A–Z</c>; when false the original ASCII case is kept and queries match ordinally. Non-ASCII
    /// source words are skipped (ASCII encoding would corrupt them, and <see cref="Contains"/> rejects
    /// non-ASCII queries anyway).
    /// </summary>
    public static WordPool Build(int wordLength, IEnumerable<string> words, bool caseInsensitive = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(wordLength);

        var sorted = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in words)
        {
            if (raw is null) continue;
            var trimmed = raw.Trim();
            if (trimmed.Length != wordLength) continue;
            if (!IsAscii(trimmed)) continue;
            sorted.Add(caseInsensitive ? trimmed.ToLowerInvariant() : trimmed);
        }

        var buffer = new byte[sorted.Count * wordLength];
        var pos = 0;
        foreach (var w in sorted)
        {
            Encoding.ASCII.GetBytes(w, 0, wordLength, buffer, pos);
            pos += wordLength;
        }
        return new WordPool(wordLength, buffer, caseInsensitive);
    }

    public bool Contains(ReadOnlySpan<char> query)
    {
        if (query.Length != WordLength) return false;
        foreach (var c in query)
            if (c > 127) return false; // non-ASCII can never be stored, so it can never match

        // Fold + compare the query against each packed entry in place — no needle buffer, so words
        // of any length work (there is no stackalloc to bound).
        int lo = 0, hi = WordCount - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            ReadOnlySpan<byte> entry = _buffer.AsSpan(mid * WordLength, WordLength);
            var cmp = CompareEntryToQuery(entry, query, _caseInsensitive);
            if (cmp == 0) return true;
            if (cmp < 0) lo = mid + 1;
            else hi = mid - 1;
        }
        return false;
    }

    // Ordinal comparison of a stored entry against a query, sign-compatible with SequenceCompareTo
    // (negative when entry sorts before query). Entries are already lowercased at build when the pool
    // is case-insensitive; the query is folded on the fly here. Callers guarantee equal length and an
    // all-ASCII query.
    private static int CompareEntryToQuery(ReadOnlySpan<byte> entry, ReadOnlySpan<char> query, bool caseInsensitive)
    {
        for (var i = 0; i < entry.Length; i++)
        {
            int q = query[i];
            if (caseInsensitive && q is >= 'A' and <= 'Z') q += 32;
            var d = entry[i] - q;
            if (d != 0) return d;
        }
        return 0;
    }

    public ReadOnlySpan<byte> GetWord(int index)
    {
        if ((uint)index >= (uint)WordCount) throw new ArgumentOutOfRangeException(nameof(index));
        return _buffer.AsSpan(index * WordLength, WordLength);
    }

    private static bool IsAscii(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (c > 127) return false;
        return true;
    }
}
