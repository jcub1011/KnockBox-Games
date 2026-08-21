using System.Diagnostics.CodeAnalysis;

namespace KnockBox.Server.Marketplace;

/// <summary>
/// A semantic version, parsed and ordered per semver.org 2.0.0. This is the whole basis of the
/// marketplace's "is my copy current?" answer, so it implements real precedence rather than string
/// comparison — <c>"0.10.0"</c> sorts after <c>"0.9.0"</c>, and a prerelease sorts *before* the
/// release it leads to (§11.3-11.4), which naive comparison gets backwards in both cases.
/// </summary>
/// <remarks>
/// Deliberately hand-rolled rather than taken from NuGet's <c>NuGetVersion</c> or SemanticVersioning:
/// this is ~100 lines of well-specified logic, the server takes no dependency it doesn't need (see
/// the OpenAPI note in KnockBox.Server.csproj), and a new package has to clear the Native AOT gate.
///
/// Build metadata (<c>+sha</c>) is accepted and then discarded: §10 says it is ignored for
/// precedence, and neither marketplace schema permits it in a published version anyway. The parser
/// is otherwise strict — a leading <c>v</c>, a missing patch component, or a leading zero in a
/// numeric identifier is a parse failure, not a guess, because a wrong guess here silently
/// misreports whether an operator's server is up to date.
/// </remarks>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Prerelease)
    : IComparable<SemVer>
{
    /// <summary>Renders the version back to its canonical string form.</summary>
    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    /// <summary>True when this version carries a prerelease tag, i.e. it is not a stable release.</summary>
    public bool IsPrerelease => Prerelease is not null;

    /// <summary>
    /// Parses <paramref name="text"/> as <c>major.minor.patch</c> with an optional
    /// <c>-prerelease</c> and an optional, ignored <c>+build</c>. Returns false on anything else;
    /// never throws.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();

        // §10: build metadata is not part of precedence. Drop it before anything else looks at the
        // string, so "1.2.3+a" and "1.2.3+b" cannot compare as different versions.
        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            if (plus == span.Length - 1) return false; // "1.2.3+" — the metadata field must not be empty.
            span = span[..plus];
        }

        string? prerelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            var tail = span[(dash + 1)..];
            if (!IsValidPrerelease(tail)) return false;
            prerelease = tail.ToString();
            span = span[..dash];
        }

        Span<Range> parts = stackalloc Range[4];
        // 4, not 3: asking for one more than we accept is what makes "1.2.3.4" fail instead of
        // silently parsing as 1.2.3.
        var count = SplitOnDots(span, parts);
        if (count != 3) return false;

        if (!TryParseNumeric(span[parts[0]], out var major)) return false;
        if (!TryParseNumeric(span[parts[1]], out var minor)) return false;
        if (!TryParseNumeric(span[parts[2]], out var patch)) return false;

        version = new SemVer(major, minor, patch, prerelease);
        return true;
    }

    /// <summary>Parses, or returns null — the shape most callers here want.</summary>
    public static SemVer? TryParse(string? text) => TryParse(text, out var v) ? v : null;

    public int CompareTo(SemVer other)
    {
        var byNumbers = Major.CompareTo(other.Major);
        if (byNumbers != 0) return byNumbers;
        byNumbers = Minor.CompareTo(other.Minor);
        if (byNumbers != 0) return byNumbers;
        byNumbers = Patch.CompareTo(other.Patch);
        if (byNumbers != 0) return byNumbers;

        // §11.3: a version WITH a prerelease has lower precedence than the same version without one.
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        return ComparePrerelease(Prerelease.AsSpan(), other.Prerelease.AsSpan());
    }

    public static bool operator <(SemVer left, SemVer right) => left.CompareTo(right) < 0;
    public static bool operator >(SemVer left, SemVer right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemVer left, SemVer right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemVer left, SemVer right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// §11.4: compare dot-separated identifiers left to right. Numeric identifiers compare
    /// numerically and always rank below alphanumeric ones; a shorter run of otherwise-equal
    /// identifiers ranks lower ("1.0.0-alpha" &lt; "1.0.0-alpha.1").
    /// </summary>
    private static int ComparePrerelease(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        while (true)
        {
            if (left.IsEmpty) return right.IsEmpty ? 0 : -1;
            if (right.IsEmpty) return 1;

            var l = NextIdentifier(ref left);
            var r = NextIdentifier(ref right);

            var lNumeric = TryParseNumeric(l, out var lValue);
            var rNumeric = TryParseNumeric(r, out var rValue);

            int cmp;
            if (lNumeric && rNumeric) cmp = lValue.CompareTo(rValue);
            else if (lNumeric) cmp = -1;                        // numeric < alphanumeric
            else if (rNumeric) cmp = 1;
            else cmp = l.CompareTo(r, StringComparison.Ordinal); // ASCII sort order

            if (cmp != 0) return cmp;
        }
    }

    private static ReadOnlySpan<char> NextIdentifier(ref ReadOnlySpan<char> remaining)
    {
        var dot = remaining.IndexOf('.');
        if (dot < 0)
        {
            var whole = remaining;
            remaining = default;
            return whole;
        }
        var head = remaining[..dot];
        remaining = remaining[(dot + 1)..];
        return head;
    }

    private static int SplitOnDots(ReadOnlySpan<char> span, Span<Range> parts)
    {
        var count = 0;
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '.') continue;
            if (count == parts.Length) return count + 1; // more parts than we accept
            parts[count++] = new Range(start, i);
            start = i + 1;
        }
        return count;
    }

    /// <summary>
    /// A semver numeric identifier: digits only, no leading zero unless the value IS zero, and small
    /// enough to be an int. Rejecting "01" is §9, and rejecting overflow keeps a hostile catalog
    /// entry from wrapping into a negative version.
    /// </summary>
    private static bool TryParseNumeric(ReadOnlySpan<char> span, out int value)
    {
        value = 0;
        if (span.IsEmpty) return false;
        if (span.Length > 1 && span[0] == '0') return false;

        foreach (var c in span)
        {
            if (c is < '0' or > '9') return false;
            if (value > (int.MaxValue - (c - '0')) / 10) return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }

    /// <summary>§9: dot-separated identifiers of [0-9A-Za-z-], none empty, numeric ones unpadded.</summary>
    private static bool IsValidPrerelease(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty) return false;
        var remaining = span;
        while (!remaining.IsEmpty)
        {
            var identifier = NextIdentifier(ref remaining);
            if (identifier.IsEmpty) return false;

            var allDigits = true;
            foreach (var c in identifier)
            {
                var ok = c is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '-';
                if (!ok) return false;
                if (c is < '0' or > '9') allDigits = false;
            }
            if (allDigits && identifier.Length > 1 && identifier[0] == '0') return false;

            // A trailing dot leaves `remaining` empty but means an empty final identifier.
            if (!remaining.IsEmpty || span[^1] != '.') continue;
            return false;
        }
        return true;
    }
}
