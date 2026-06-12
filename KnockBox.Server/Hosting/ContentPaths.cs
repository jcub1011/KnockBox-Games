namespace KnockBox.Server.Hosting;

/// <summary>
/// Resolves where the server's content lives (<c>web/</c>, <c>games/</c>, <c>logs/</c>) across the
/// three ways it runs: <c>dotnet run</c> from the repo, a published desktop folder, and a container
/// image. Pure (no <c>IHostEnvironment</c>) so the precedence rules are unit-testable.
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

    public sealed record Resolved(string WebRoot, string GamesRoot, string LogsRoot);

    public static Resolved Resolve(
        string? webRootConfig, string? gamesRootConfig, string? logsRootConfig,
        string contentRoot, string baseDirectory)
    {
        var anchor = FindRepoRoot(contentRoot) ?? FindRepoRoot(baseDirectory) ?? baseDirectory;
        return new(
            ResolveOne(webRootConfig, "web", contentRoot, anchor),
            ResolveOne(gamesRootConfig, "games", contentRoot, anchor),
            ResolveOne(logsRootConfig, "logs", contentRoot, anchor));
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
