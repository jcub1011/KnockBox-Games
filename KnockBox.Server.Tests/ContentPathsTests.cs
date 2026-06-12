using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

public class ContentPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-paths-" + Guid.NewGuid().ToString("N"));

    public ContentPathsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    [Fact]
    public void Explicit_config_wins_and_relative_paths_anchor_to_the_content_root()
    {
        var paths = ContentPaths.Resolve(
            webRootConfig: Path.Combine(_root, "custom-web"),
            gamesRootConfig: "my-games",
            logsRootConfig: null,
            contentRoot: _root,
            baseDirectory: Path.Combine(_root, "app"));

        Assert.Equal(Path.Combine(_root, "custom-web"), paths.WebRoot);
        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "my-games")), paths.GamesRoot);
        // Unconfigured root falls through to the base-directory fallback (no repo marker here).
        Assert.Equal(Path.Combine(_root, "app", "logs"), paths.LogsRoot);
    }

    [Fact]
    public void Repo_discovery_walks_up_from_the_content_root_to_the_marker()
    {
        File.WriteAllText(Path.Combine(_root, "KnockBox-Games.slnx"), "");
        var nested = Path.Combine(_root, "KnockBox.Server", "bin", "Debug");
        Directory.CreateDirectory(nested);

        var paths = ContentPaths.Resolve(null, null, null, contentRoot: nested, baseDirectory: nested);

        Assert.Equal(Path.Combine(_root, "web"), paths.WebRoot);
        Assert.Equal(Path.Combine(_root, "games"), paths.GamesRoot);
        Assert.Equal(Path.Combine(_root, "logs"), paths.LogsRoot);
    }

    [Fact]
    public void Published_layout_falls_back_to_the_base_directory()
    {
        // No marker anywhere under the temp root → published exe / container layout.
        var appDir = Path.Combine(_root, "publish");
        Directory.CreateDirectory(appDir);

        var paths = ContentPaths.Resolve(null, null, null, contentRoot: _root, baseDirectory: appDir);

        Assert.Equal(Path.Combine(appDir, "web"), paths.WebRoot);
        Assert.Equal(Path.Combine(appDir, "games"), paths.GamesRoot);
        Assert.Equal(Path.Combine(appDir, "logs"), paths.LogsRoot);
    }

    [Fact]
    public void FindRepoRoot_returns_null_when_the_marker_is_absent()
    {
        Assert.Null(ContentPaths.FindRepoRoot(_root));
    }
}
