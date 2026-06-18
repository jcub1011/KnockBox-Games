namespace KnockBox.Server.Hosting;

/// <summary>
/// Replaces the shell home page with <see cref="DeploymentWarningPage"/> when
/// <see cref="DeploymentDiagnostics"/> reports a BLOCKING file-access problem — an unreadable games
/// mount, a missing shell — so a broken/empty deployment is obvious during setup instead of silently
/// serving nothing. Non-blocking warnings (e.g. an unwritable cache) don't blank a working site on
/// their own, but are listed on the page alongside any blocking ones. Served 200 (not 5xx) so it stays
/// visible and never trips a host health check into a restart loop. Only the home document is
/// intercepted; the page is self-contained (inline CSS) so it renders even when the web root is the
/// problem. Registered BEFORE UseDefaultFiles/UseStaticFiles so it wins over a broken index.html.
/// </summary>
internal sealed class DeploymentWarningMiddleware(RequestDelegate next, DeploymentDiagnostics diagnostics)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value;
        var isHome = string.IsNullOrEmpty(path) || path == "/"
            || string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase);
        // Snapshot the diagnostics once (it re-invokes the live games-access probe each call) and reuse
        // it for both the blocking check and the render, so a home request resolves the state exactly once.
        if (isHome && HttpMethods.IsGet(ctx.Request.Method))
        {
            var issues = diagnostics.Current();
            if (issues.Any(i => i.Blocking))
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-store";
                await ctx.Response.WriteAsync(DeploymentWarningPage.Render(issues), ctx.RequestAborted);
                return;
            }
        }
        await next(ctx);
    }
}
