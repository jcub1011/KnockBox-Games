using KnockBox.Server.Hosting;

namespace KnockBox.Server.Games;

/// <summary>
/// Resolves an installed game id back to the <c>.kbg</c> file it was installed from, across both package
/// roots.
/// </summary>
/// <remarks>
/// This exists because <c>Path.Combine(GamesRoot, id + ".kbg")</c> — which deletion, disk accounting and
/// the admin games listing each derived independently — is wrong in two ways. The installer accepts ANY
/// <c>*.kbg</c> file name and takes the id from the header inside, so a game installed from
/// <c>alpha-chain-v2.kbg</c> was reported as not package-backed and had its bytes uncounted; worse,
/// deleting it removed the unpacked copy and left the package, so the installer put the game straight
/// back. And with a second, managed package root, the assumption is wrong for every portal-installed
/// game as well.
///
/// The marker inside the extracted folder is authoritative: it records the exact file name and root that
/// produced the folder. Probing is only the fallback for a game with no readable marker.
/// </remarks>
public static class GamePackageLocations
{
    /// <param name="Path">Full path to the <c>.kbg</c> file.</param>
    /// <param name="Root">One of the <see cref="PackageMarker"/> root tokens.</param>
    public readonly record struct PackageLocation(string Path, string Root)
    {
        /// <summary>True when this package sits in the writable managed root, so the server may replace or remove it.</summary>
        public bool Managed => Root == PackageMarker.ManagedRoot;
    }

    /// <summary>The source package for <paramref name="id"/>, or null when the game came from a plain folder.</summary>
    public static PackageLocation? Find(ContentPaths.Resolved paths, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        if (PackageMarker.TryRead(Path.Combine(paths.GamesUnpackedRoot, id)) is { } marker
            && RootPath(paths, marker.Root) is { } root)
        {
            var fromMarker = Path.Combine(root, marker.Source);
            if (File.Exists(fromMarker)) return new PackageLocation(fromMarker, marker.Root);
            // The marker names a file that is gone. Fall through and probe: the operator may have replaced
            // it with a canonically-named one, and reporting nothing would understate the footprint.
        }

        // Probe order follows install precedence — games/ wins a contested id (GameCatalog searches it
        // first), so a hand-placed package is the one reported when both roots hold an <id>.kbg.
        var name = id + GamePackage.Extension;
        var hand = Path.Combine(paths.GamesRoot, name);
        if (File.Exists(hand)) return new PackageLocation(hand, PackageMarker.GamesRoot);

        var managed = Path.Combine(paths.GamesManagedRoot, name);
        return File.Exists(managed) ? new PackageLocation(managed, PackageMarker.ManagedRoot) : null;
    }

    /// <summary>Where a package that carries <paramref name="root"/> lives, or null for an unknown token.</summary>
    public static string? RootPath(ContentPaths.Resolved paths, string root) => root switch
    {
        PackageMarker.GamesRoot => paths.GamesRoot,
        PackageMarker.ManagedRoot => paths.GamesManagedRoot,
        _ => null,
    };
}
