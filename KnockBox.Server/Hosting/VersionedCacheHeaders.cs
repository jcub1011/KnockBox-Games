namespace KnockBox.Server.Hosting;

/// <summary>
/// Decides the <c>Cache-Control</c> for platform files served under a content-hash token
/// (see <see cref="ContentHashProvider"/>): the carrier pages (<c>index.html</c> and friends)
/// and the versioned URLs they point at.
///
/// The rule is deliberately asymmetric, because the two failure modes cost different things:
/// <list type="bullet">
/// <item>A versioned URL carrying the CURRENT token is immutable — the token names the bytes, so a
/// cached copy can never be stale. This is the one place a year-long cache is a fact rather than a
/// bet, and it is what keeps platform JavaScript cheap at scale.</item>
/// <item>The same path with a MISSING or STALE token revalidates (<c>no-cache,
/// must-revalidate</c>). The static middleware ignores the query string and always serves current
/// bytes, so marking a stale token immutable would pin wrong bytes under a URL nobody asks for
/// again — while answering revalidate heals any client holding a stale carrier (e.g. behind a
/// non-compliant proxy) on its next request.</item>
/// </list>
/// Files that are never versioned in any URL — transitive ES imports (<c>kb-core.js</c>,
/// <c>kb-protocol.js</c>, <c>admin-core.js</c>), the game SDK, favicons — always take the
/// revalidate branch: their freshness rests on ETag revalidation, which is also how the admin
/// portal has always worked. Rewriting JavaScript bodies per request to inject versions would break
/// the static middleware's ETags for no additional correctness.
/// </summary>
internal static class VersionedCacheHeaders
{
    /// <summary>For a versioned URL carrying the current token: the token names the bytes.</summary>
    public const string Immutable = "public, max-age=31536000, immutable";

    /// <summary>For everything else: revalidate every time, serve 304 when unchanged.</summary>
    public const string Revalidate = "no-cache, must-revalidate";

    /// <summary>
    /// The <c>Cache-Control</c> value for one request. <paramref name="versionedPaths"/> are the
    /// request paths versioned by the carrier page on this origin (e.g. <c>/shell.js</c>,
    /// <c>/home.css</c>); <paramref name="versionQuery"/> is the request's <c>?v=</c> value (empty
    /// when absent); <paramref name="currentToken"/> is the provider's current token.
    /// </summary>
    public static string CacheControlFor(
        string? path,
        string? versionQuery,
        string currentToken,
        ISet<string> versionedPaths)
    {
        if (path is not null
            && versionedPaths.Contains(path)
            && versionQuery is not null
            && versionQuery.Equals(currentToken, StringComparison.Ordinal))
        {
            return Immutable;
        }
        return Revalidate;
    }

    /// <summary>
    /// Whether a request path names a carrier page that must be rendered with the current token
    /// rather than served statically. Matches exact paths only (any query string is the client's
    /// business — <c>?game=</c>/<c>?join=</c> are read client-side).
    /// </summary>
    public static bool IsCarrierPage(string? path, string[] carriers)
    {
        if (path is null) return false;
        foreach (var carrier in carriers)
            if (path.Equals(carrier, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
