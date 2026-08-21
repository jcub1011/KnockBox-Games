using KnockBox.Server.Marketplace;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// <see cref="SemVer"/> parsing and precedence. This is the arithmetic the whole "is my copy
/// current?" answer rests on, so the cases below are the ones where string comparison — the obvious
/// wrong implementation — gives a different (and wrong) answer.
/// </summary>
public class PluginVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0, null)]
    [InlineData("0.1.0", 0, 1, 0, null)]
    [InlineData("10.20.30", 10, 20, 30, null)]
    [InlineData("1.0.0-alpha", 1, 0, 0, "alpha")]
    [InlineData("1.0.0-alpha.1", 1, 0, 0, "alpha.1")]
    [InlineData("1.0.0-0.3.7", 1, 0, 0, "0.3.7")]
    [InlineData("1.0.0-x-y-z.-", 1, 0, 0, "x-y-z.-")]
    [InlineData("  1.2.3  ", 1, 2, 3, null)]
    public void Parses_valid_versions(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        Assert.Equal(new SemVer(major, minor, patch, prerelease), version);
    }

    [Theory]
    // Build metadata is not part of precedence (§10), so it is accepted and dropped.
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("1.2.3-rc.1+sha.abc", "1.2.3-rc.1")]
    public void Discards_build_metadata(string text, string expected)
    {
        Assert.True(SemVer.TryParse(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]          // a leading v is a tag convention, not a version
    [InlineData("1.2.3-")]          // empty prerelease
    [InlineData("1.2.3+")]          // empty build metadata
    [InlineData("01.2.3")]          // §9: no leading zeros
    [InlineData("1.2.3-alpha..1")]  // empty identifier
    [InlineData("1.2.3-alpha.01")]  // §9: no leading zeros in a numeric identifier
    [InlineData("1.2.3-alpha.")]    // trailing dot
    [InlineData("1.2.3-al pha")]
    [InlineData("a.b.c")]
    [InlineData("99999999999.0.0")] // overflows int rather than wrapping negative
    public void Rejects_invalid_versions(string? text)
    {
        Assert.False(SemVer.TryParse(text, out _));
        Assert.Null(SemVer.TryParse(text));
    }

    [Theory]
    // Numeric ordering, which string comparison gets wrong.
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("2.0.0", "10.0.0")]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.0.9", "1.1.0")]
    // §11.3: a prerelease precedes the release it leads to — the other case string comparison
    // inverts, since "1.0.0-alpha" sorts after "1.0.0" lexically.
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    // §11.4, the full worked example from the specification.
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta", "1.0.0-beta.2")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]   // numeric identifiers compare numerically
    [InlineData("1.0.0-beta.11", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]          // numeric ranks below alphanumeric
    public void Orders_lower_before_higher(string lower, string higher)
    {
        var a = SemVer.TryParse(lower) ?? throw new InvalidOperationException(lower);
        var b = SemVer.TryParse(higher) ?? throw new InvalidOperationException(higher);

        Assert.True(a < b, $"expected {lower} < {higher}");
        Assert.True(b > a);
        Assert.True(a <= b);
        Assert.True(b >= a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1.2.3-rc.1")]
    public void Equal_versions_compare_equal(string text)
    {
        var a = SemVer.TryParse(text)!.Value;
        var b = SemVer.TryParse(text)!.Value;

        Assert.Equal(0, a.CompareTo(b));
        Assert.Equal(a, b);
        Assert.True(a <= b);
        Assert.True(a >= b);
    }

    [Fact]
    public void Build_metadata_does_not_affect_equality()
    {
        // §10 again, stated as the property that matters: two builds of the same version are the
        // same version, so a rebuild must not read as an available update.
        Assert.Equal(SemVer.TryParse("1.2.3+a"), SemVer.TryParse("1.2.3+b"));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void Round_trips_through_ToString(string text, string expected) =>
        Assert.Equal(expected, SemVer.TryParse(text)!.Value.ToString());

    [Fact]
    public void Reports_prerelease_status()
    {
        Assert.True(SemVer.TryParse("1.0.0-rc.1")!.Value.IsPrerelease);
        Assert.False(SemVer.TryParse("1.0.0")!.Value.IsPrerelease);
    }

    [Fact]
    public void Sorts_a_realistic_release_history()
    {
        string[] published = ["1.0.0", "0.9.0", "1.0.0-rc.1", "0.10.0", "1.0.1", "1.0.0-alpha"];
        var ordered = published.Select(v => SemVer.TryParse(v)!.Value).Order().Select(v => v.ToString());

        Assert.Equal(["0.9.0", "0.10.0", "1.0.0-alpha", "1.0.0-rc.1", "1.0.0", "1.0.1"], ordered);
    }
}
