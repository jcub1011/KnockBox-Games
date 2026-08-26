using System.Text.RegularExpressions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Guards the invariant that decides whether updating the image keeps or destroys an operator's data:
/// every directory the server writes non-regenerable state into must be a MOUNT, in every file that
/// tells someone how to deploy this.
///
/// The failure this exists to prevent has happened, twice, in two different ways. This project's
/// predecessor lost admin settings and installed games on every TrueNAS Custom App image update, because
/// the state directory was not mounted and a <c>VOLUME</c> directive was mistaken for persistence. And
/// in this repo, <c>docker-compose.yml</c> was correct while every published COPY of it —
/// <c>README.md</c>'s quick start, the Cloudflare Tunnel guide — omitted <c>/app/data</c> and
/// <c>/app/games-managed</c>. The canonical file being right is not the property that matters; operators
/// paste the copies. So the copies are what these tests check.
///
/// Like <see cref="OriginPortBindingTests"/>, these assertions are about the deployment FILES rather
/// than a running host: nothing observable in-process distinguishes "this directory is mounted" from
/// "this directory happens to exist", and the container-level proof belongs in the docker CI job (which
/// recreates the container and checks the state survived).
/// </summary>
public class DockerPersistenceTests
{
    /// <summary>
    /// State that cannot be rebuilt from anything else, so losing the directory loses the data outright.
    /// Deliberately NOT the full list of writable roots: <c>games-compressed</c> and
    /// <c>games-unpacked</c> are derived caches and <c>logs</c> is disposable, and requiring a mount for
    /// all six would make this test fire over things that cost only rebuild time.
    /// </summary>
    private static readonly (string Path, string Holds)[] NonRegenerable =
    [
        ("/app/data", "the admin password hash and every persisted operator policy decision"),
        ("/app/games-managed", "the game packages the admin portal installed, including uploaded ones that exist nowhere else"),
    ];

    /// <summary>Every directory the image creates for the server to write into.</summary>
    private static readonly string[] WritableContainerDirs =
    [
        "/app/data", "/app/games-managed", "/app/games-compressed", "/app/games-unpacked", "/app/logs",
    ];

    // ── The image ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every writable directory must be created AND chowned to the app user while the build is still
    /// root. This is not hypothetical bookkeeping: the predecessor's Dockerfile had exactly this line,
    /// and a later memory optimization that switched to a chiseled base (which has no shell) deleted it.
    /// Docker then created each missing mount point as <c>root:root</c>, the UID-1654 process could not
    /// write it, and the admin portal accepted settings that were never saved.
    /// </summary>
    [Theory]
    [InlineData("/app/data")]
    [InlineData("/app/games-managed")]
    [InlineData("/app/games-compressed")]
    [InlineData("/app/games-unpacked")]
    [InlineData("/app/logs")]
    public void Every_writable_container_directory_is_created_and_chowned(string dir)
    {
        var dockerfile = ReadRepoFile(Path.Combine("KnockBox.Server", "Dockerfile"));
        if (dockerfile is null) return; // not run from a repo checkout — nothing to check

        Assert.True(
            Regex.IsMatch(dockerfile, @"mkdir[^\n]*" + Regex.Escape(dir) + @"(\s|\\|$)"),
            $"The Dockerfile never creates '{dir}'. Docker will create a missing mount point as " +
            "root:root, which the non-root app user cannot write.");

        Assert.True(
            Regex.IsMatch(dockerfile, @"chown \$APP_UID[^\n]*" + Regex.Escape(dir) + @"(\s|\\|$)"),
            $"'{dir}' is writable state but is not chowned to $APP_UID in the Dockerfile, so the " +
            "container's non-root user cannot write it. Writes then fail at runtime while the portal " +
            "still reports success — the exact shape of the predecessor's silent data loss.");
    }

    /// <summary>
    /// The ownership fix only works while the build is still root, so <c>USER</c> must come after it. A
    /// <c>USER</c> line moved above the chown would leave the whole thing inert and nothing else would
    /// notice.
    /// </summary>
    [Fact]
    public void The_app_user_is_switched_to_after_the_directories_are_chowned()
    {
        var dockerfile = ReadRepoFile(Path.Combine("KnockBox.Server", "Dockerfile"));
        if (dockerfile is null) return;

        var chown = dockerfile.IndexOf("chown $APP_UID", StringComparison.Ordinal);
        var user = Regex.Match(dockerfile, @"^USER \$APP_UID", RegexOptions.Multiline);

        Assert.True(chown >= 0, "The Dockerfile no longer chowns anything to $APP_UID.");
        Assert.True(user.Success, "The Dockerfile no longer switches to $APP_UID.");
        Assert.True(chown < user.Index,
            "USER $APP_UID appears BEFORE the chown, so the chown runs as the unprivileged user and " +
            "cannot change ownership. The directories stay root-owned and every write fails.");
    }

    /// <summary>
    /// No <c>VOLUME</c> directive, and this absence is a decision rather than an omission. <c>VOLUME</c>
    /// only declares a mount point: with nothing mounted, Docker attaches an ANONYMOUS volume, which
    /// Compose usually carries across a recreate — so it looks like persistence — while
    /// <c>docker run</c> makes a fresh one per container and Kubernetes ignores the directive entirely.
    /// The predecessor shipped that belief and lost state on every TrueNAS update for weeks. The server
    /// warns at startup instead (see <c>StatePersistence</c>), which works on every platform; a
    /// directive that converts a loud failure into a quiet one is worse than none.
    /// </summary>
    [Fact]
    public void The_image_declares_no_VOLUME()
    {
        var dockerfile = ReadRepoFile(Path.Combine("KnockBox.Server", "Dockerfile"));
        if (dockerfile is null) return;

        Assert.False(
            Regex.IsMatch(dockerfile, @"^\s*VOLUME\b", RegexOptions.Multiline),
            "The Dockerfile declares a VOLUME. That is not persistence — an unmounted VOLUME becomes an " +
            "anonymous volume that `docker run` replaces per container and Kubernetes ignores — and it " +
            "hides the startup warning's failure case behind something that works just often enough to " +
            "be trusted. See StatePersistence for the full reasoning.");
    }

    // ── The compose file ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/app/data")]
    [InlineData("/app/games-managed")]
    [InlineData("/app/games-compressed")]
    [InlineData("/app/games-unpacked")]
    public void The_compose_file_mounts_every_writable_root(string target)
    {
        var compose = ReadRepoFile("docker-compose.yml");
        if (compose is null) return;

        Assert.True(MountsTarget(compose, target),
            $"docker-compose.yml has no mount at '{target}', so it lives inside the container and is " +
            "destroyed on the next image update.");
    }

    /// <summary>
    /// The project name must be pinned. Without it Compose derives the project from the DIRECTORY the
    /// file sits in and prefixes every named volume with it — so unzipping a newer release bundle into a
    /// new directory silently creates a new, EMPTY set of volumes and the operator's settings and
    /// installed games are simply gone.
    /// </summary>
    [Fact]
    public void The_compose_project_name_is_pinned()
    {
        var compose = ReadRepoFile("docker-compose.yml");
        if (compose is null) return;

        Assert.True(
            Regex.IsMatch(compose, @"^name:\s*\S+", RegexOptions.Multiline),
            "docker-compose.yml declares no top-level `name:`, so its named volumes are prefixed with " +
            "the directory name. Upgrading by unzipping a release bundle elsewhere would then start " +
            "against empty volumes.");
    }

    /// <summary>
    /// And each volume names itself, for the same reason one layer down: what a volume is CALLED must
    /// not depend on where the compose file was unzipped.
    /// </summary>
    [Fact]
    public void Every_declared_volume_pins_its_own_name()
    {
        var compose = ReadRepoFile("docker-compose.yml");
        if (compose is null) return;

        // The top-level `volumes:` block is the last one in the file; its entries are the declarations.
        var block = compose[compose.LastIndexOf("\nvolumes:", StringComparison.Ordinal)..];
        var declared = Regex.Matches(block, @"^  (knockbox-[a-z0-9-]+):\s*$", RegexOptions.Multiline);

        Assert.NotEmpty(declared);
        foreach (var volume in declared.Select(m => m.Groups[1].Value))
            Assert.True(
                Regex.IsMatch(block, @"^  " + Regex.Escape(volume) + @":\s*\r?\n\s+name:\s*" + Regex.Escape(volume),
                    RegexOptions.Multiline),
                $"Volume '{volume}' does not pin `name: {volume}`, so Compose prefixes it with the " +
                "project name and its identity depends on the directory this file is in.");
    }

    // ── The copies operators actually paste ────────────────────────────────────────────────────────

    /// <summary>
    /// This is the assertion that matters most, because it covers the files that were actually wrong.
    /// Any fenced block in the docs that is complete enough to mount the games directory is a block
    /// someone will paste and run, so it must also mount the state that cannot be rebuilt. A block that
    /// mounts nothing (an <c>environment:</c> fragment, a shell snippet) is not a deployment and is
    /// skipped.
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/HOSTING.md")]
    [InlineData("docs/ADMIN.md")]
    [InlineData("docs/INFRASTRUCTURE.md")]
    [InlineData("docs/MARKETPLACE.md")]
    public void Every_documented_compose_block_mounts_the_non_regenerable_state(string relativePath)
    {
        var markdown = ReadRepoFile(relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (markdown is null) return;

        var blocks = FencedBlocks(markdown).Where(b => MountsTarget(b.Text, "/games")).ToList();
        foreach (var (line, text) in blocks)
            foreach (var (path, holds) in NonRegenerable)
                Assert.True(MountsTarget(text, path),
                    $"The compose block at {relativePath}:{line} mounts /games but not '{path}', which " +
                    $"holds {holds}. Anyone who pastes this block loses that on their next image " +
                    "update — the canonical docker-compose.yml being correct does not help them.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a compose fragment mounts something at <paramref name="target"/>, in either syntax:
    /// short (<c>- /host/path:/app/data</c>, possibly with a <c>${VAR:-default}</c> source) or long
    /// (<c>target: /app/data</c>). Commented lines don't count — a mount an operator has to uncomment
    /// is a mount they will forget.
    /// </summary>
    private static bool MountsTarget(string text, string target)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#')) continue;

            // Long form: `target: /app/data`.
            if (line.StartsWith("target:", StringComparison.Ordinal)
                && line["target:".Length..].Trim() == target)
                return true;

            // Short form: the target follows a ':' and ends at end-of-line or the option suffix (':ro').
            var at = line.IndexOf(':' + target, StringComparison.Ordinal);
            if (at < 0) continue;
            var after = at + 1 + target.Length;
            if (after >= line.Length || line[after] == ':' || char.IsWhiteSpace(line[after])) return true;
        }
        return false;
    }

    /// <summary>Fenced code blocks as (1-based opening-fence line, contents).</summary>
    private static List<(int Line, string Text)> FencedBlocks(string markdown)
    {
        var blocks = new List<(int, string)>();
        var lines = markdown.Split('\n');
        var open = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            if (open < 0) open = i;
            else
            {
                blocks.Add((open + 1, string.Join('\n', lines[(open + 1)..i])));
                open = -1;
            }
        }
        return blocks;
    }

    // Shared with OriginPortBindingTests and AddonManifestTests, the other tests that assert repo files
    // agree with each other.
    private static string? ReadRepoFile(string relativePath) => RepoFile.Read(relativePath);
}
