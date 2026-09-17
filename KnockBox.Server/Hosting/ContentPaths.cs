namespace KnockBox.Server.Hosting;

/// <summary>
/// Resolves where the server's content lives (<c>web/</c>, <c>games/</c>, <c>logs/</c>, the two
/// derived caches, the managed package root and the blob store) across the three ways it runs:
/// <c>dotnet run</c> from the repo, a published desktop folder, and a container image. Pure (no
/// <c>IHostEnvironment</c>) so the precedence rules are unit-testable.
///
/// Per root, precedence:
/// <list type="number">
/// <item>Explicit config (<c>KnockBox:WebRoot</c> / <c>GamesRoot</c> / <c>LogsRoot</c>); a relative
/// value resolves against the content root.</item>
/// <item>Repo discovery: walk up from the content root (then the app base directory) looking for
/// the solution file — the dev layout, where web/ and games/ sit at the repo top level.</item>
/// <item>The app base directory (published exe folder, <c>/app</c> in the container), where publish
/// bakes <c>web/</c> in and <c>games/</c> sits alongside. The marker file is deliberately never
/// shipped, so published deployments always land here.</item>
/// </list>
/// </summary>
public static class ContentPaths
{
    private const string RepoMarker = "KnockBox-Games.slnx";

    // Four of these are written by the server, so all four must stay OUTSIDE the read-only games/
    // mount and therefore get their own roots rather than living under GamesRoot:
    //   • GamesCompressedRoot — pre-compressed .br/.gz siblings of each game asset.
    //     Default sibling "games-compressed". Regenerable: losing it costs rebuild time.
    //   • GamesUnpackedRoot — game folders extracted from .kbg packages.
    //     Default sibling "games-unpacked". Regenerable: re-extracted from the packages.
    //   • GamesManagedRoot — the .kbg packages the ADMIN PORTAL installed (from a marketplace, or
    //     uploaded), plus their rollback backups. Default sibling "games-managed". NOT regenerable: a
    //     marketplace package can be fetched again, but an uploaded one exists nowhere else. It holds
    //     packages, never extracted games — those still land in GamesUnpackedRoot, so the catalog's
    //     root list is unchanged.
    //   • BlobsRoot — media a game uploaded for its session to share, content-addressed by SHA-256.
    //     Default sibling "blobs". Regenerable, and more completely than either cache: every blob is
    //     anchored to a lobby, lobbies are in-memory and die with the process, and the ticket secret is
    //     regenerated per process — so after a restart every blob on disk is orphaned by definition and
    //     BlobStore.SweepAtStartup deletes the lot. A client re-uploads what its session still needs.
    public sealed record Resolved(
        string WebRoot, string GamesRoot, string LogsRoot, string GamesCompressedRoot, string GamesUnpackedRoot,
        string GamesManagedRoot)
    {
        // A NAMED init member rather than a seventh positional parameter, and `required` rather than
        // defaulted, both deliberately. Every member of this record is a string, so a seventh positional
        // one would bind silently wherever a caller's argument order drifted from the declaration's --
        // which is not hypothetical: GamePackageExporterTests passed the six in the wrong order for as
        // long as they were all positional, swapping LogsRoot and GamesUnpackedRoot, and compiled
        // cleanly the whole time. `required` breaks every construction site instead, so each one is
        // read once by a human, and that is how the pre-existing swap was found.
        public required string BlobsRoot { get; init; }
    }

    public static Resolved Resolve(
        string? webRootConfig, string? gamesRootConfig, string? logsRootConfig,
        string? gamesCompressedRootConfig, string? gamesUnpackedRootConfig, string? gamesManagedRootConfig,
        string? blobsRootConfig,
        string contentRoot, string baseDirectory)
    {
        var anchor = FindRepoRoot(contentRoot) ?? FindRepoRoot(baseDirectory) ?? baseDirectory;
        return new(
            ResolveOne(webRootConfig, "web", contentRoot, anchor),
            ResolveOne(gamesRootConfig, "games", contentRoot, anchor),
            ResolveOne(logsRootConfig, "logs", contentRoot, anchor),
            ResolveOne(gamesCompressedRootConfig, "games-compressed", contentRoot, anchor),
            ResolveOne(gamesUnpackedRootConfig, "games-unpacked", contentRoot, anchor),
            ResolveOne(gamesManagedRootConfig, "games-managed", contentRoot, anchor))
        {
            BlobsRoot = ResolveOne(blobsRootConfig, "blobs", contentRoot, anchor),
        };
    }

    private static string ResolveOne(string? configured, string name, string contentRoot, string anchor) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(anchor, name)
            // Path.Combine returns the second path unchanged when it is already absolute.
            : Path.GetFullPath(Path.Combine(contentRoot, configured));

    /// <summary>Walks up from <paramref name="start"/> looking for the repo marker; null if absent.</summary>
    public static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, RepoMarker)))
                return dir.FullName;
        return null;
    }
}
