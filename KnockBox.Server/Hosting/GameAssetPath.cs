namespace KnockBox.Server.Hosting;

/// <summary>
/// The single parser for <c>/games/{id}/{relative}</c> request paths and for the game-relative paths
/// a manifest declares (<c>entry</c>, <c>thumbnail</c>, <c>serverAuthority</c>, <c>authorityWords</c>).
///
/// Comparing a RAW url remainder against a RAW manifest string is not a sound test of "these name the
/// same file", because whatever ultimately serves the request canonicalizes first:
/// <see cref="Microsoft.Extensions.FileProviders.PhysicalFileProvider"/> resolves through
/// <see cref="Path.GetFullPath"/>, which collapses duplicate separators and <c>.</c> segments — and on
/// Windows treats <c>\</c> as a separator too. So <c>/games/wg//answers.txt</c> reaches the same file as
/// <c>/games/wg/answers.txt</c> while failing a literal string comparison, which is how a deny list can
/// be walked straight past. Anything deciding whether a path may be served must compare CANONICAL
/// forms; that is what this type exists to produce, in one place rather than the four hand-rolled
/// copies of the split this replaced.
/// </summary>
internal static class GameAssetPath
{
    /// <summary>The request-path root game assets are served under (the static options' RequestPath).</summary>
    public const string Root = "/games";
    public const string Prefix = Root + "/";

    /// <summary>
    /// The game id out of a <c>/games/{id}</c> or <c>/games/{id}/…</c> path, or null when the path
    /// isn't under <c>/games/</c> or names no id. Use this when only the id matters (per-game
    /// response headers); use <see cref="TryParse"/> when the file part matters too.
    /// </summary>
    public static string? GameId(string? path)
    {
        if (path is null || !path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/');
        var id = slash < 0 ? rest : rest[..slash];
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// Splits <c>/games/{id}/{relative}</c> and canonicalizes the file part. False when the path is
    /// not a game asset path, names no file, or its file part cannot be canonicalized — see
    /// <see cref="Canonicalize"/> for what that rules out.
    /// </summary>
    public static bool TryParse(string? path, out string id, out string relative)
    {
        id = "";
        relative = "";
        if (path is null || !path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = path[Prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0) return false; // no file part, or an empty id

        if (Canonicalize(rest[(slash + 1)..]) is not { } canonical) return false;
        id = rest[..slash];
        relative = canonical;
        return true;
    }

    /// <summary>
    /// The canonical form of a game-relative path: <c>/</c> separators, no empty segments (a doubled
    /// separator) and no <c>.</c> segments. Null when the input is empty or contains a <c>..</c>
    /// segment.
    /// </summary>
    /// <remarks>
    /// <c>..</c> is REFUSED rather than resolved. Popping a segment would produce a canonical path for
    /// something that is not a game asset at all, and every caller's correct answer for a traversal
    /// attempt is the same as for an unparseable path — don't serve it, don't match it against a
    /// manifest. The file providers block traversal independently; this just never launders it.
    /// </remarks>
    public static string? Canonicalize(string? relative)
    {
        if (string.IsNullOrEmpty(relative)) return null;

        var segments = relative.Replace('\\', '/').Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") return null;
            kept.Add(segment);
        }
        return kept.Count == 0 ? null : string.Join('/', kept);
    }
}
