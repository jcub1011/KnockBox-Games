namespace KnockBox.Server.Lobbies;

/// <summary>
/// The operator's blocklist for generated room codes (spec §2.4): codes players would rather not be
/// handed, and shoulder-surfing hazards like a code that spells something.
/// </summary>
/// <remarks>
/// <para>Two kinds of entry, because they answer different questions:</para>
/// <list type="bullet">
/// <item><b>Words</b> match as a <em>substring</em> anywhere in the code, so <c>XQ</c> blocks both
/// <c>XQ4B</c> and <c>7XQ2</c>. Anything shorter than the code length would be nearly useless as a
/// whole-code match.</item>
/// <item><b>Patterns</b> match the <em>whole</em> code, with <c>?</c> for one character and <c>*</c> for
/// any run — enough to express "nothing starting FU" without a second syntax to learn.</item>
/// </list>
/// <para><b>Deliberately not regular expressions.</b> This runs on the lobby-create path, on a string an
/// operator typed into a web form. A regex there is a denial-of-service lever pointed at the thing every
/// player needs, and the expressiveness buys nothing over a glob on four characters.</para>
/// <para>Matching is case-insensitive, and codes are compared upper-cased, because the lobby dictionary is
/// <c>OrdinalIgnoreCase</c> and the shell upper-cases what a player types. A blocklist that was
/// case-sensitive here would be bypassable by typing the code in lower case on the join side.</para>
/// </remarks>
public sealed class RoomCodeFilter
{
    /// <summary>Blocks nothing — a deployment that has never configured this, and what the tests use.</summary>
    public static readonly RoomCodeFilter Empty = new([], []);

    /// <summary>
    /// Cap on the total number of entries. It bounds the per-draw cost on the lobby-create path and the
    /// exhaustive count below; it is also far more than a real blocklist needs, since one pattern covers a
    /// whole family.
    /// </summary>
    public const int MaxEntries = 32;

    /// <summary>Longest entry worth storing: nothing longer can occur inside a code.</summary>
    public const int MaxEntryLength = LobbyManager.CodeLength;

    private readonly string[] _words;
    private readonly string[] _patterns;

    private RoomCodeFilter(string[] words, string[] patterns)
    {
        _words = words;
        _patterns = patterns;
    }

    /// <summary>The blocked substrings, upper-cased and de-duplicated.</summary>
    public IReadOnlyList<string> Words => _words;

    /// <summary>The blocked whole-code masks, upper-cased and de-duplicated.</summary>
    public IReadOnlyList<string> Patterns => _patterns;

    /// <summary>True when nothing is blocked, so the generator can skip the check entirely.</summary>
    public bool IsEmpty => _words.Length == 0 && _patterns.Length == 0;

    /// <summary>Total entries, for the cap.</summary>
    public int Count => _words.Length + _patterns.Length;

    /// <summary>
    /// Compiles a blocklist, dropping entries that aren't usable rather than rejecting the whole list —
    /// the same discipline the settings file's other rows get, since this may have been hand-edited.
    /// </summary>
    public static RoomCodeFilter Compile(IEnumerable<string>? words, IEnumerable<string>? patterns)
    {
        var cleanWords = new List<string>();
        var cleanPatterns = new List<string>();

        foreach (var raw in words ?? [])
        {
            var entry = Normalize(raw);
            if (entry is null || !IsWord(entry) || cleanWords.Contains(entry)) continue;
            cleanWords.Add(entry);
        }
        foreach (var raw in patterns ?? [])
        {
            var entry = Normalize(raw);
            if (entry is null || !IsPattern(entry) || cleanPatterns.Contains(entry)) continue;
            cleanPatterns.Add(entry);
        }

        // Trim to the cap from the end, so an over-long hand-edited list keeps its first entries rather
        // than silently keeping an arbitrary subset.
        while (cleanWords.Count + cleanPatterns.Count > MaxEntries)
        {
            if (cleanPatterns.Count > 0) cleanPatterns.RemoveAt(cleanPatterns.Count - 1);
            else cleanWords.RemoveAt(cleanWords.Count - 1);
        }

        return cleanWords.Count == 0 && cleanPatterns.Count == 0
            ? Empty
            : new RoomCodeFilter([.. cleanWords], [.. cleanPatterns]);
    }

    /// <summary>Whether this code must not be handed out. Case-insensitive.</summary>
    public bool IsBlocked(string? code)
    {
        if (IsEmpty || string.IsNullOrEmpty(code)) return false;

        Span<char> upper = stackalloc char[code.Length];
        for (var i = 0; i < code.Length; i++) upper[i] = char.ToUpperInvariant(code[i]);
        return IsBlockedUpper(upper);
    }

    /// <summary>
    /// The same question for a code that is already upper-case — which every generated one is, since the
    /// alphabet has no lower-case letters. Lets the generator test a stack buffer without allocating a
    /// string per draw.
    /// </summary>
    public bool IsBlockedUpper(ReadOnlySpan<char> code)
    {
        if (IsEmpty) return false;
        foreach (var word in _words)
            if (code.Contains(word, StringComparison.Ordinal)) return true;
        foreach (var pattern in _patterns)
            if (MatchesPattern(code, pattern)) return true;
        return false;
    }

    /// <summary>
    /// Why this entry can't be used, or null when it can. Reported by the admin API so an operator learns
    /// it at the form rather than by watching a blocklist quietly do nothing.
    /// </summary>
    public static string? ValidateEntry(string? raw, bool pattern)
    {
        var entry = Normalize(raw);
        if (entry is null) return "Enter something to block.";
        if (entry.Length > MaxEntryLength)
            return $"Codes are {LobbyManager.CodeLength} characters, so nothing longer than " +
                   $"{MaxEntryLength} can ever appear in one.";
        if (pattern && !IsPattern(entry))
            return "A pattern may contain only the code alphabet plus ? (one character) and * (any run).";
        if (!pattern && !IsWord(entry))
            return $"Use only characters from the code alphabet ({LobbyManager.CodeAlphabet}).";
        return null;
    }

    /// <summary>
    /// True when the entry is legal but can never be generated, because it uses letters the code alphabet
    /// leaves out (there is no <c>O</c>, <c>0</c>, <c>I</c> or <c>1</c> — they are too easily misread).
    /// </summary>
    /// <remarks>
    /// Not an error: such an entry is harmless, and an operator blocking a word list wholesale shouldn't
    /// have to know the alphabet. But it is worth SAYING, because otherwise the entry looks like it is
    /// doing something and isn't.
    /// </remarks>
    public static bool IsUnreachable(string? raw)
    {
        var entry = Normalize(raw);
        if (entry is null) return false;
        foreach (var c in entry)
        {
            if (c is '?' or '*') continue;
            if (!LobbyManager.CodeAlphabet.Contains(c)) return true;
        }
        return false;
    }

    /// <summary>
    /// How many of the possible codes this blocklist removes, counted exactly.
    /// </summary>
    /// <remarks>
    /// A deliberate walk of the whole code space — 32⁴ = 1,048,576 codes, with an early exit on the first
    /// matching entry and at most <see cref="MaxEntries"/> entries to test. It runs when the blocklist is
    /// read or saved, never on the lobby-create path, and it is what lets the portal say "this removes 3%
    /// of the code space" and lets the API refuse a list that would remove most of it. Combinatorics over
    /// overlapping globs would be faster and much easier to get subtly wrong.
    /// </remarks>
    public int CountBlocked()
    {
        if (IsEmpty) return 0;

        var alphabet = LobbyManager.CodeAlphabet;
        var blocked = 0;
        Span<char> code = stackalloc char[LobbyManager.CodeLength];
        // Four nested loops rather than recursion, because the code length is a constant of the protocol.
        foreach (var a in alphabet)
        {
            code[0] = a;
            foreach (var b in alphabet)
            {
                code[1] = b;
                foreach (var c in alphabet)
                {
                    code[2] = c;
                    foreach (var d in alphabet)
                    {
                        code[3] = d;
                        if (IsBlockedUpper(code)) blocked++;
                    }
                }
            }
        }
        return blocked;
    }

    /// <summary>Total codes the generator can produce, so a count can be shown as a share.</summary>
    public static int CodeSpaceSize()
    {
        var size = 1;
        for (var i = 0; i < LobbyManager.CodeLength; i++) size *= LobbyManager.CodeAlphabet.Length;
        return size;
    }

    private static string? Normalize(string? raw)
    {
        var trimmed = raw?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private static bool IsWord(string entry)
    {
        if (entry.Length is 0 or > MaxEntryLength) return false;
        foreach (var c in entry)
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        return true;
    }

    private static bool IsPattern(string entry)
    {
        if (entry.Length is 0 or > MaxEntryLength + 2) return false; // room for the wildcards themselves
        foreach (var c in entry)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('?' or '*')) return false;
        return true;
    }

    /// <summary>
    /// Glob match over the whole code: <c>?</c> is one character, <c>*</c> is any run including none.
    /// Iterative with a backtrack point, so it allocates nothing and can't recurse.
    /// </summary>
    private static bool MatchesPattern(ReadOnlySpan<char> code, string pattern)
    {
        int c = 0, p = 0, starP = -1, starC = 0;
        while (c < code.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == code[c]))
            {
                c++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starC = c;
            }
            else if (starP >= 0)
            {
                // Backtrack: let the last '*' swallow one more character.
                p = starP + 1;
                c = ++starC;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
