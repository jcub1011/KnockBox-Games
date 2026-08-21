using KnockBox.Server.Games;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The <c>.kb-package</c> freshness marker: its four-field format, and the backward compatibility that
/// stops a server upgrade from re-extracting every installed game.
/// </summary>
public class PackageMarkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kb-marker-{Guid.NewGuid():N}");

    public PackageMarkerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private string MarkerPath => Path.Combine(_dir, PackageMarker.FileName);

    [Fact]
    public void Round_trips_stamp_source_and_root()
    {
        // A platform-native absolute path, not a hard-coded Windows one: Path.GetFileName (which is
        // what Write uses) does not treat a backslash as a separator on Linux, so the literal
        // @"C:\somewhere\..." this used to pass made the ENTIRE string the "file name" on the CI runner.
        var elsewhere = Path.Combine(Path.GetTempPath(), "somewhere", "alpha-chain-v2.kbg");
        PackageMarker.Write(_dir, elsewhere, PackageMarker.ManagedRoot, (12345L, 678L));

        var marker = PackageMarker.TryRead(_dir);

        Assert.NotNull(marker);
        Assert.Equal(12345L, marker.Value.Mtime);
        Assert.Equal(678L, marker.Value.Length);
        // The NAME, never the path: the marker travels with the extracted folder, and an absolute path
        // from another machine would be meaningless in it.
        Assert.Equal("alpha-chain-v2.kbg", marker.Value.Source);
        Assert.Equal(PackageMarker.ManagedRoot, marker.Value.Root);
    }

    [Fact]
    public void Missing_marker_reads_as_null()
    {
        Assert.Null(PackageMarker.TryRead(_dir));
    }

    [Fact]
    public void Missing_directory_reads_as_null()
    {
        Assert.Null(PackageMarker.TryRead(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void A_legacy_three_field_marker_reads_as_the_games_root()
    {
        // Exactly what a pre-managed-root server wrote. Reading it as anything else would make every
        // installed game look stale after an upgrade and re-extract the entire library.
        File.WriteAllText(MarkerPath, "999\t42\tdemo.kbg\n");

        var marker = PackageMarker.TryRead(_dir);

        Assert.NotNull(marker);
        Assert.Equal(999L, marker.Value.Mtime);
        Assert.Equal(42L, marker.Value.Length);
        Assert.Equal("demo.kbg", marker.Value.Source);
        Assert.Equal(PackageMarker.GamesRoot, marker.Value.Root);
    }

    [Fact]
    public void A_legacy_marker_whose_file_name_contains_a_tab_is_not_misread_as_the_new_format()
    {
        // Three fields, but the name splits into two — so a naive field count would read "odd" as the
        // root token and silently truncate the name. Validating the token against the known vocabulary
        // is what keeps this correct.
        File.WriteAllText(MarkerPath, "5\t6\todd\tname.kbg\n");

        var marker = PackageMarker.TryRead(_dir);

        Assert.NotNull(marker);
        Assert.Equal("odd\tname.kbg", marker.Value.Source);
        Assert.Equal(PackageMarker.GamesRoot, marker.Value.Root);
    }

    [Fact]
    public void A_file_name_containing_a_tab_survives_the_new_format()
    {
        PackageMarker.Write(_dir, "odd\tname.kbg", PackageMarker.ManagedRoot, (5L, 6L));

        var marker = PackageMarker.TryRead(_dir);

        Assert.NotNull(marker);
        Assert.Equal("odd\tname.kbg", marker.Value.Source);
        Assert.Equal(PackageMarker.ManagedRoot, marker.Value.Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\n")]
    [InlineData("notanumber\t42\tgames\tdemo.kbg\n")]
    [InlineData("999\tnotanumber\tgames\tdemo.kbg\n")]
    [InlineData("999\t42\n")]
    public void Unparseable_markers_read_as_null_which_means_stale(string contents)
    {
        // Null is always safe: every caller treats it as "reinstall", never as "up to date".
        File.WriteAllText(MarkerPath, contents);

        Assert.Null(PackageMarker.TryRead(_dir));
    }
}
