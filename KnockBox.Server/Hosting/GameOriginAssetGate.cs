using KnockBox.Server.Games;

namespace KnockBox.Server.Hosting;

/// <summary>
/// Denies the ONE game asset the game origin must never serve: a game's server-authority module
/// (design §11). It is server-side code, not a client asset — and for hidden-information games its
/// secrecy is the whole point. The game origin otherwise serves everything in a game folder
/// (ServeUnknownFileTypes), so this is an explicit exclusion, mirroring the shell origin's
/// thumbnail allowlist.
/// </summary>
public static class GameOriginAssetGate
{
    /// <summary>
    /// True for <c>/games/{id}/{path}</c> where {path} equals game {id}'s declared serverAuthority
    /// (separator-normalized, ordinal-ignore-case — game folders live on case-insensitive
    /// filesystems too), or that path plus a <c>.br</c>/<c>.gz</c> suffix — so a stale
    /// pre-compressed variant from before the exclusion existed can never leak the module either.
    /// </summary>
    public static bool IsDeniedAuthorityAsset(string path, GameCatalog catalog)
    {
        const string prefix = "/games/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var rest = path[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return false;
        var id = rest[..slash];
        var file = rest[(slash + 1)..];

        if (!catalog.TryGet(id, out var manifest) || string.IsNullOrEmpty(manifest.ServerAuthority))
            return false;

        if (file.EndsWith(".br", StringComparison.OrdinalIgnoreCase)
            || file.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            file = file[..^3];
        }

        return string.Equals(
            file.Replace('\\', '/'),
            manifest.ServerAuthority.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
}
