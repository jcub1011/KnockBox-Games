using KnockBox.Server.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace KnockBox.Server.Hosting;

/// <summary>This server's own version, as reported to unauthenticated callers.</summary>
/// <param name="Version">The running server's version, from <see cref="KnockBoxVersion.Current"/>.</param>
public sealed record ServerVersionResponse(string Version);

/// <summary>
/// The one public, unauthenticated version endpoint, shared by the player shell and the admin
/// portal. Deliberately a single string: the version already travels inside the authenticated
/// marketplace catalog payload (<c>AdminMarketplaceResponse.AppVersion</c>), but that response
/// also carries source URLs, managed paths and the job feed, so making it public to expose one
/// field would leak operator data. The version itself is not sensitive — it is printed in the
/// page headers — so this endpoint carries nothing else and needs no session.
/// </summary>
public static class ServerVersionApi
{
    public const string Path = "/api/server-version";

    /// <summary>
    /// The current version payload, serialized through the source-generated context (Native AOT).
    /// Served <c>no-store</c>: after an image update the header label must repaint on the next load,
    /// not ride a heuristic-cached GET of the previous version.
    /// </summary>
    public static IResult Current() =>
        new NoStoreResult(
            Results.Json(
                new ServerVersionResponse(KnockBoxVersion.Current.ToString()),
                KnockBoxProtocolContext.Default.ServerVersionResponse));

    /// <summary>
    /// Sets <c>Cache-Control: no-store</c> before delegating, so both origins share the header
    /// regardless of which <c>MapGet</c> registration serves them.
    /// </summary>
    private sealed class NoStoreResult(IResult inner) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            return inner.ExecuteAsync(httpContext);
        }
    }

    /// <summary>Maps the endpoint. Call once per origin that must serve it (shell, admin).</summary>
    public static void MapServerVersion(this IEndpointRouteBuilder routes) =>
        routes.MapGet(Path, () => Current());
}
