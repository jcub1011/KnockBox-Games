using KnockBox.Server.Games;

namespace KnockBox.Server.Hosting;

/// <summary>
/// Denies the game assets the game origin must never serve: a game's server-authority module and its
/// declared <c>authorityWords</c> dictionaries (design §11). These are server-side code/data, not
/// client assets — and for hidden-information games (a secret answer list) their secrecy is the whole
/// point. The game origin otherwise serves everything in a game folder (ServeUnknownFileTypes), so
/// this is an explicit exclusion, mirroring the shell origin's thumbnail allowlist.
/// </summary>
public static class GameOriginAssetGate
{
    /// <summary>
    /// True for <c>/games/{id}/{path}</c> where {path} names game {id}'s declared serverAuthority OR
    /// any of its authorityWords files, or that path plus a <c>.br</c>/<c>.gz</c> suffix — so a stale
    /// pre-compressed variant from before the exclusion existed can never leak the file either.
    /// </summary>
    /// <remarks>
    /// BOTH sides are canonicalized through <see cref="GameAssetPath"/> before they are compared, and
    /// that is the whole correctness argument. A literal comparison denies <c>/games/wg/answers.txt</c>
    /// while waving through <c>/games/wg//answers.txt</c>, which the static-file provider then resolves
    /// to the very same secret file — the deny list has to canonicalize exactly like the thing that
    /// eventually opens the file. The manifest side needs it too: a game may legitimately declare
    /// <c>"./authority.js"</c>, which the catalog accepts (it resolves the path) and a raw comparison
    /// would never match. Comparison stays ordinal-ignore-case because game folders live on
    /// case-insensitive filesystems too.
    /// </remarks>
    public static bool IsDeniedAuthorityAsset(string path, GameCatalog catalog)
    {
        if (!GameAssetPath.TryParse(path, out var id, out var file)) return false;
        if (!catalog.TryGet(id, out var manifest)) return false;

        if (file.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^3];
        }

        if (NamesSameFile(file, manifest.ServerAuthority)) return true;

        if (manifest.AuthorityWords is { } words)
        {
            foreach (var decl in words.Values)
            {
                if (NamesSameFile(file, decl?.File)) return true;
            }
        }

        return false;
    }

    private static bool NamesSameFile(string requested, string? declared) =>
        !string.IsNullOrEmpty(declared)
        && GameAssetPath.Canonicalize(declared) is { } canonical
        && string.Equals(requested, canonical, StringComparison.OrdinalIgnoreCase);
}
