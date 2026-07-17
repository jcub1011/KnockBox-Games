namespace KnockBox.Server.Games.Words;

/// <summary>
/// A read-only, memory-efficient pool of distinct words — the per-dictionary handle a
/// server-authority module queries through <c>kb.words</c>. Words of the same length share a single
/// packed ASCII buffer, so storage is roughly the size of the raw word list and index access is O(1).
/// Returned spans are ASCII bytes that alias the pool's internal buffer; decode with
/// <see cref="System.Text.Encoding.ASCII"/>.<c>GetString</c> if a <see cref="string"/> is needed, and
/// do not store them across <c>await</c>.
/// </summary>
/// <remarks>
/// Adapted from the sibling <c>KnockBox.WordService</c> repo (<c>IWordPool</c> +
/// <c>CustomWordPool</c>), trimmed to what server-authority word games need and given a per-pool
/// case-sensitivity flag. The whole dictionary stays on the CLR side and is shared by every lobby
/// engine — only the boolean/number/string RESULT of a query ever crosses the Jint boundary.
/// </remarks>
public interface IWordPool
{
    /// <summary>Total number of distinct words across all lengths.</summary>
    int TotalWordCount { get; }

    /// <summary>The distinct word lengths present in the pool, sorted ascending.</summary>
    IReadOnlyList<int> AvailableLengths { get; }

    /// <summary>Number of words of the given <paramref name="length"/> (0 if none).</summary>
    int GetWordCount(int length);

    /// <summary>
    /// Returns the <paramref name="index"/>-th word (sorted ordinal) of the given
    /// <paramref name="length"/> as raw ASCII bytes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if there are no words of <paramref name="length"/>, or <paramref name="index"/> is
    /// outside <c>[0, GetWordCount(length))</c>.
    /// </exception>
    ReadOnlySpan<byte> GetWord(int length, int index);

    /// <summary>
    /// Returns the <paramref name="globalIndex"/>-th word across the whole pool as raw ASCII bytes.
    /// Words are addressed length-bucket by length-bucket (ascending length, ordinal within each
    /// length), so the index space is a contiguous <c>[0, TotalWordCount)</c> — one draw maps
    /// uniformly to a word.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown if <paramref name="globalIndex"/> is outside <c>[0, TotalWordCount)</c>.
    /// </exception>
    ReadOnlySpan<byte> GetWord(int globalIndex);

    /// <summary>
    /// True if <paramref name="word"/> exists in the pool. Case sensitivity is fixed per pool at
    /// build time; non-ASCII queries always return false.
    /// </summary>
    bool Contains(ReadOnlySpan<char> word);
}
