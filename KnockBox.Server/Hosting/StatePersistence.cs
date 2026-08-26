namespace KnockBox.Server.Hosting;

/// <summary>
/// Answers one question: will this directory survive the next image update?
///
/// Inside a container, everything not covered by a mount lives in the container's own writable layer
/// and is destroyed when the container is replaced — which is exactly what updating an image does. The
/// server keeps working perfectly against the empty replacement, so nothing surfaces until an operator
/// notices the admin portal asking to be claimed again and their disabled games serving players. This
/// class exists so that outcome is announced at startup instead of discovered months later.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately NOT solved with a <c>VOLUME</c> directive in the Dockerfile, which is the
/// intuitive fix and the wrong one. <c>VOLUME</c> only declares a mount point: when the operator mounts
/// nothing, Docker attaches an <i>anonymous</i> volume, which Compose usually carries across a recreate
/// — so it looks like persistence — while <c>docker run</c> makes a fresh one per container,
/// <c>--rm</c> discards it immediately, and Kubernetes ignores the directive outright. This project's
/// predecessor shipped that exact belief and lost operator state on every TrueNAS Custom App update,
/// because that platform recreates the container each time. A declaration that turns a loud failure
/// into a quiet one is worse than no declaration; a warning that works on every platform beats a
/// directive that works on one.
/// </para>
/// <para>
/// Pure, like <see cref="ContentPaths"/> and <see cref="GameAssetPath"/>: the parsing takes text rather
/// than reading <c>/proc</c> itself, so the precedence rules are unit-testable. String operations only
/// — no new package, so the <c>aot</c> gate stays green.
/// </para>
/// </remarks>
public static class StatePersistence
{
    /// <summary>
    /// Mount points from the contents of <c>/proc/self/mountinfo</c>, longest first, so the first match
    /// while walking the list is the most specific one.
    /// </summary>
    /// <remarks>
    /// Field 5 (1-based) is the mount point. Optional fields of unknown count follow field 6 and are
    /// terminated by a lone <c>-</c>, but field 5 sits before all of that, so a straight split on spaces
    /// is enough and no separator hunting is needed. Paths are octal-escaped by the kernel (a space in a
    /// mount point arrives as <c>\040</c>); those escapes are decoded rather than ignored, or a mount
    /// under a directory whose name contains a space would never match.
    /// </remarks>
    public static IReadOnlyList<string> MountPoints(string mountInfo)
    {
        var points = new List<string>();
        foreach (var line in mountInfo.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) continue; // blank or truncated line
            points.Add(Unescape(fields[4]));
        }

        // Longest first: /app/data must win over /app, and /app over /.
        points.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        return points;
    }

    /// <summary>
    /// The most specific mount point containing <paramref name="path"/>, or null when none does — which
    /// cannot happen for a real Linux path, since <c>/</c> is always mounted, but can when the caller
    /// passes a hand-built list.
    /// </summary>
    public static string? NearestMount(IEnumerable<string> mountPoints, string path)
    {
        var normalized = Normalize(path);
        string? best = null;
        foreach (var point in mountPoints)
        {
            var candidate = Normalize(point);
            if (!Contains(candidate, normalized)) continue;
            // Not assuming the caller sorted: the longest match wins regardless of input order.
            if (best is null || candidate.Length > best.Length) best = candidate;
        }
        return best;
    }

    /// <summary>
    /// True when <paramref name="path"/> lives in the container's own writable layer — i.e. the nearest
    /// mount containing it is the root filesystem, so nothing outside the container holds it and it dies
    /// with the container.
    /// </summary>
    /// <remarks>
    /// Only meaningful in a container (see <see cref="InContainer"/>). On an ordinary host the nearest
    /// mount for almost everything is <c>/</c> and that is entirely fine, which is why callers must gate
    /// on the container check rather than on this answer alone.
    /// </remarks>
    public static bool IsEphemeral(IEnumerable<string> mountPoints, string path) =>
        NearestMount(mountPoints, path) is null or "/";

    /// <summary>
    /// Whether this process is running in a container. <c>DOTNET_RUNNING_IN_CONTAINER</c> is set by the
    /// official .NET base images, including the <c>runtime-deps</c> one this server ships on;
    /// <c>/.dockerenv</c> covers an image built from some other base that still runs under Docker.
    /// </summary>
    public static bool InContainer() =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true",
            StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");

    /// <summary>
    /// The mount points of the running process, or null when they can't be read — the normal answer on
    /// Windows and macOS, and the reason every caller treats null as "say nothing". Guessing from an
    /// unreadable <c>/proc</c> would mean warning about every directory on a desktop install.
    /// </summary>
    public static IReadOnlyList<string>? CurrentMountPoints()
    {
        try
        {
            return File.Exists("/proc/self/mountinfo")
                ? MountPoints(File.ReadAllText("/proc/self/mountinfo"))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Trailing separators removed, so "/app/data/" and "/app/data" compare equal.</summary>
    private static string Normalize(string path)
    {
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    /// <summary>
    /// Whether <paramref name="parent"/> contains <paramref name="child"/>, or is it. Compared on
    /// segment boundaries: "/app" must not count as containing "/application", which a plain
    /// StartsWith gets wrong — the same trap <see cref="GameAssetPath"/> exists to avoid.
    /// </summary>
    private static bool Contains(string parent, string child)
    {
        if (parent == "/") return child.StartsWith('/');
        if (!child.StartsWith(parent, StringComparison.Ordinal)) return false;
        return child.Length == parent.Length || child[parent.Length] == '/';
    }

    /// <summary>
    /// Decodes the octal escapes the kernel writes into mountinfo paths — space, tab, newline and
    /// backslash are the only four it escapes.
    /// </summary>
    private static string Unescape(string field)
    {
        if (!field.Contains('\\')) return field; // the overwhelmingly common case

        var builder = new System.Text.StringBuilder(field.Length);
        for (var i = 0; i < field.Length; i++)
        {
            if (field[i] == '\\' && i + 3 < field.Length
                && TryOctal(field[i + 1], field[i + 2], field[i + 3], out var decoded))
            {
                builder.Append(decoded);
                i += 3;
            }
            else builder.Append(field[i]);
        }
        return builder.ToString();
    }

    private static bool TryOctal(char a, char b, char c, out char decoded)
    {
        decoded = '\0';
        if (a is < '0' or > '7' || b is < '0' or > '7' || c is < '0' or > '7') return false;
        decoded = (char)(((a - '0') << 6) | ((b - '0') << 3) | (c - '0'));
        return true;
    }
}
