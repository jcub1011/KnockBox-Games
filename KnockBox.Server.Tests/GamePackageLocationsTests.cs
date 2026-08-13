using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Resolving an installed game id back to its source <c>.kbg</c>. The bug this replaces was deriving
/// <c>GamesRoot/&lt;id&gt;.kbg</c>: the installer accepts any file name and takes the id from the header
/// inside, so a game installed from <c>alpha-chain-v2.kbg</c> was reported as having no package at all —
/// and deleting it removed the unpacked copy while leaving the package, so the game reinstalled itself.
/// </summary>
public class GamePackageLocationsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"kb-locations-{Guid.NewGuid():N}");
    private readonly ContentPaths.Resolved _paths;

    public GamePackageLocationsTests()
    {
        _paths = new ContentPaths.Resolved(
            Path.Combine(_root, "web"),
            Path.Combine(_root, "games"),
            Path.Combine(_root, "logs"),
            Path.Combine(_root, "games-compressed"),
            Path.Combine(_root, "games-unpacked"),
            Path.Combine(_root, "games-managed"));
        foreach (var dir in new[] { _paths.GamesRoot, _paths.GamesUnpackedRoot, _paths.GamesManagedRoot })
            Directory.CreateDirectory(dir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private string WritePackage(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        File.WriteAllText(path, "not really a zip");
        return path;
    }

    private void WriteMarker(string id, string fileName, string rootToken)
    {
        var dir = Path.Combine(_paths.GamesUnpackedRoot, id);
        Directory.CreateDirectory(dir);
        PackageMarker.Write(dir, fileName, rootToken, (1L, 2L));
    }

    [Fact]
    public void The_marker_resolves_a_package_whose_file_name_is_not_the_game_id()
    {
        var path = WritePackage(_paths.GamesRoot, "alpha-chain-v2.kbg");
        WriteMarker("alpha", "alpha-chain-v2.kbg", PackageMarker.GamesRoot);

        var found = GamePackageLocations.Find(_paths, "alpha");

        Assert.NotNull(found);
        Assert.Equal(path, found.Value.Path);
        Assert.Equal(PackageMarker.GamesRoot, found.Value.Root);
        Assert.False(found.Value.Managed);
    }

    [Fact]
    public void The_marker_resolves_a_package_in_the_managed_root()
    {
        var path = WritePackage(_paths.GamesManagedRoot, "beta.kbg");
        WriteMarker("beta", "beta.kbg", PackageMarker.ManagedRoot);

        var found = GamePackageLocations.Find(_paths, "beta");

        Assert.NotNull(found);
        Assert.Equal(path, found.Value.Path);
        // Managed is what tells the portal an update, rollback or removal is possible: that root is
        // writable by design, unlike the read-only games mount.
        Assert.True(found.Value.Managed);
    }

    [Fact]
    public void A_game_with_no_package_resolves_to_null()
    {
        // The plain-folder case: a game hand-placed in games/ with no archive behind it.
        Directory.CreateDirectory(Path.Combine(_paths.GamesRoot, "folder-game"));

        Assert.Null(GamePackageLocations.Find(_paths, "folder-game"));
    }

    [Fact]
    public void Without_a_marker_the_canonical_name_is_probed()
    {
        var path = WritePackage(_paths.GamesManagedRoot, "gamma.kbg");

        var found = GamePackageLocations.Find(_paths, "gamma");

        Assert.NotNull(found);
        Assert.Equal(path, found.Value.Path);
        Assert.Equal(PackageMarker.ManagedRoot, found.Value.Root);
    }

    [Fact]
    public void Probing_follows_install_precedence_so_the_hand_placed_package_wins()
    {
        // Both roots hold delta.kbg and there is no marker to disambiguate. GameCatalog searches games/
        // first, so that is the copy actually serving players and the one to report.
        var hand = WritePackage(_paths.GamesRoot, "delta.kbg");
        WritePackage(_paths.GamesManagedRoot, "delta.kbg");

        var found = GamePackageLocations.Find(_paths, "delta");

        Assert.NotNull(found);
        Assert.Equal(hand, found.Value.Path);
    }

    [Fact]
    public void A_marker_naming_a_file_that_is_gone_falls_back_to_probing()
    {
        // An operator replaced an oddly-named package with a canonically-named one. Reporting nothing
        // would understate the footprint and disable Delete for no reason.
        WriteMarker("epsilon", "epsilon-old.kbg", PackageMarker.GamesRoot);
        var path = WritePackage(_paths.GamesRoot, "epsilon.kbg");

        var found = GamePackageLocations.Find(_paths, "epsilon");

        Assert.NotNull(found);
        Assert.Equal(path, found.Value.Path);
    }

    [Fact]
    public void A_legacy_marker_still_resolves_into_the_games_root()
    {
        var path = WritePackage(_paths.GamesRoot, "zeta-1.kbg");
        var dir = Path.Combine(_paths.GamesUnpackedRoot, "zeta");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PackageMarker.FileName), "1\t2\tzeta-1.kbg\n");

        var found = GamePackageLocations.Find(_paths, "zeta");

        Assert.NotNull(found);
        Assert.Equal(path, found.Value.Path);
        Assert.Equal(PackageMarker.GamesRoot, found.Value.Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_id_resolves_to_null(string id)
    {
        Assert.Null(GamePackageLocations.Find(_paths, id));
    }
}
