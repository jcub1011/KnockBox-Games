namespace KnockBox.Server.Games;

/// <summary>
/// The per-game freshness record <see cref="GamePackageInstaller"/> writes INSIDE each extracted game
/// folder, recording which package file produced it and in what state that file was.
/// </summary>
/// <remarks>
/// Keeping it inside the extracted folder (rather than in one central index) means it is deleted along
/// with the folder, so the index can never disagree with what is on disk. Dot-prefixed so
/// <c>PhysicalFileProvider</c>'s default exclusion filters never serve it.
///
/// It lives in its own type because three subsystems now need to read it: the installer (freshness and
/// uninstall liveness), <see cref="GamePackageLocations"/> (resolving a game id back to its source
/// package), and the tests.
/// </remarks>
public static class PackageMarker
{
    public const string FileName = ".kb-package";

    /// <summary>The read-only <c>games/</c> mount, where an operator hand-drops a package.</summary>
    public const string GamesRoot = "games";

    /// <summary>The writable managed root, where the admin portal installs packages it fetched or was given.</summary>
    public const string ManagedRoot = "managed";

    private static bool IsKnownRoot(string token) => token is GamesRoot or ManagedRoot;

    /// <param name="Source">The package file NAME (not path) the extracted folder came from.</param>
    /// <param name="Root">Which package root <paramref name="Source"/> sits in — see the constants above.</param>
    public readonly record struct Marker(long Mtime, long Length, string Source, string Root);

    // Format: "<mtimeTicks>\t<length>\t<rootToken>\t<source file name>".
    //
    // The file name stays LAST so a tab inside it cannot corrupt any other field — the same convention as
    // the pre-compress index, and the reason the root token was inserted BEFORE the name rather than
    // appended after it. The token is drawn from a fixed two-value vocabulary, so it can never contain a
    // tab itself.
    public static void Write(string directory, string packagePath, string root, (long Mtime, long Length) stamp) =>
        File.WriteAllText(Path.Combine(directory, FileName),
            $"{stamp.Mtime}\t{stamp.Length}\t{root}\t{Path.GetFileName(packagePath)}\n");

    /// <summary>
    /// Reads a marker, or null when there is none or it is unreadable.
    /// </summary>
    /// <remarks>
    /// A null answer always means "looks stale", which reinstalls — safe in every caller, which is why an
    /// IO failure here is swallowed rather than propagated.
    ///
    /// Markers written before the managed root existed have three fields and no token. They are read as
    /// <see cref="GamesRoot"/>, which is exactly what they meant when they were written, so upgrading a
    /// deployment re-extracts nothing.
    /// </remarks>
    public static Marker? TryRead(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var line = File.ReadLines(path).FirstOrDefault();
            if (line is null) return null;

            var parts = line.Split('\t', 4);
            if (parts.Length < 3) return null;
            if (!long.TryParse(parts[0], out var mtime)) return null;
            if (!long.TryParse(parts[1], out var length)) return null;

            // Four fields AND a token we recognise is the current format. Anything else is legacy — which
            // includes the pathological case of a THREE-field marker whose file name contains a tab, since
            // that also splits into four. Validating the token rather than counting fields is what keeps
            // that case reading correctly instead of losing the first tab-separated chunk of the name.
            return parts.Length == 4 && IsKnownRoot(parts[2])
                ? new Marker(mtime, length, parts[3], parts[2])
                : new Marker(mtime, length, string.Join('\t', parts[2..]), GamesRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
