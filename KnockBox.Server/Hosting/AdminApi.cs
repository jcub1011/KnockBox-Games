using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Hosting;

/// <summary>
/// The operator dashboard's HTTP API, served at the admin origin's root under <c>/admin/api/*</c>.
///
/// It lives here rather than inline in the composition root because it is an API, not wiring: every
/// endpoint needs a method check, a content type, a serializer call and (for the protected ones) a
/// session check, and hand-writing those per endpoint in a middleware if-chain is how the fifth one
/// comes to differ from the first four. One <see cref="WriteJson"/> helper, one
/// <see cref="RequireSession"/> wrapper and a route table make each handler the part that's actually
/// specific to it.
///
/// The two password endpoints are throttled ahead of the route table — see <see cref="MapAdminApi"/>.
/// </summary>
internal static class AdminApi
{
    /// <summary>Everything the admin API needs, resolved once while the pipeline is built.</summary>
    /// <param name="CookieAlwaysSecure">
    /// Force <c>Secure</c> on the session cookie even when the request looks like plain HTTP. True when
    /// the portal's configured origin is https, because behind a TLS-terminating proxy that has not been
    /// granted <c>KnockBox:ForwardedHeaders</c> the request Kestrel sees IS plain HTTP — so deriving the
    /// flag from the request alone hands out a non-Secure session token in precisely the deployment the
    /// hosting docs recommend.
    /// </param>
    public sealed record Options(
        AdminAuthService Auth,
        LobbyManager Lobbies,
        GameCatalog Catalog,
        TimeProvider Time,
        ILogger Logger,
        int LoginAttemptsPerMinutePerIp,
        int LoginAttemptsPerMinuteGlobal,
        bool CookieAlwaysSecure);

    /// <summary>Registers the throttle, then the <c>/admin/api/*</c> routes. Anything unmatched falls
    /// through to the caller's next middleware (the portal's static files).</summary>
    public static void MapAdminApi(this IApplicationBuilder admin, Options options)
    {
        // Process start can't change and Process.GetCurrentProcess() allocates a handle per call, so read
        // it once here rather than on every 5-second dashboard poll.
        DateTime processStartedUtc;
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            processStartedUtc = self.StartTime.ToUniversalTime();

        admin.Use(PasswordAttemptThrottle(options));

        admin.UseRouting();
        admin.UseEndpoints(routes =>
        {
            routes.MapGet("/admin/api/auth/status", ctx => AuthStatus(ctx, options));
            routes.MapPost("/admin/api/auth/setup", ctx => Setup(ctx, options));
            routes.MapPost("/admin/api/auth/login", ctx => Login(ctx, options));
            routes.MapPost("/admin/api/auth/logout", ctx => Logout(ctx, options));
            routes.MapGet("/admin/api/system/status",
                RequireSession(options, ctx => SystemStatus(ctx, options, processStartedUtc)));
        });
    }

    // ── Throttle ──────────────────────────────────────────────────────────────
    // Checked BEFORE any hashing, so a refused attempt is free (~7 ms instead of ~420 ms). The policy —
    // and why it takes two buckets — lives in AdminLoginThrottle.
    private static Func<HttpContext, RequestDelegate, Task> PasswordAttemptThrottle(Options options)
    {
        var throttle = new AdminLoginThrottle(
            options.LoginAttemptsPerMinutePerIp, options.LoginAttemptsPerMinuteGlobal, options.Time);

        return async (ctx, next) =>
        {
            if (!IsPasswordAttempt(ctx))
            {
                await next(ctx);
                return;
            }

            if (throttle.Refuse(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown") is { } scope)
            {
                options.Logger.LogWarning(
                    "Throttled an admin password attempt from {Ip} ({Path}): {Scope} limit reached.",
                    ctx.Connection.RemoteIpAddress, ctx.Request.Path, scope);
                ctx.Response.Headers.RetryAfter = "60";
                await Refuse(ctx, StatusCodes.Status429TooManyRequests, "Too many attempts. Wait a minute and try again.");
                return;
            }

            await next(ctx);
        };
    }

    private static bool IsPasswordAttempt(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method)) return false;
        var path = ctx.Request.Path.Value ?? "";
        return path.Equals("/admin/api/auth/login", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/admin/api/auth/setup", StringComparison.OrdinalIgnoreCase);
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    private static Task AuthStatus(HttpContext ctx, Options options)
    {
        var authenticated = ctx.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var session)
            && options.Auth.ValidateSessionToken(session);
        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminAuthStatusResponse,
            new AdminAuthStatusResponse(options.Auth.IsConfigured, authenticated));
    }

    private static async Task Setup(HttpContext ctx, Options options)
    {
        if (await ReadPassword(ctx) is not { } password)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, "Password required.");
            return;
        }

        // Distinct messages per outcome: "already configured" is an expected race, a too-short password is
        // the operator's to fix, but an unwritable secret file is a DEPLOYMENT fault (wrong owner on the
        // mount) that previously hid behind the same "already configured or invalid" text — the single
        // most confusing way this could fail.
        var setup = options.Auth.SetupPassword(password);
        if (setup != AdminAuthService.SetupOutcome.Success)
        {
            var (status, error) = setup switch
            {
                AdminAuthService.SetupOutcome.AlreadyConfigured =>
                    (StatusCodes.Status409Conflict,
                     "An admin password is already set. Delete the secret file on the server to reset it."),
                AdminAuthService.SetupOutcome.PasswordTooWeak =>
                    (StatusCodes.Status400BadRequest,
                     $"Password must be at least {AdminAuthService.MinPasswordLength} characters."),
                _ => (StatusCodes.Status500InternalServerError,
                     $"Could not save the password to '{options.Auth.SecretFilePath}'. Check that the path exists " +
                     "and is writable by the server (in Docker, the container runs as UID 1654)."),
            };
            await Refuse(ctx, status, error);
            return;
        }

        StartSession(ctx, options);
        await Accept(ctx);
    }

    private static async Task Login(HttpContext ctx, Options options)
    {
        if (await ReadPassword(ctx) is not { } password || !options.Auth.VerifyPassword(password))
        {
            await Refuse(ctx, StatusCodes.Status401Unauthorized, "Invalid admin password.");
            return;
        }

        StartSession(ctx, options);
        await Accept(ctx);
    }

    private static Task Logout(HttpContext ctx, Options options)
    {
        // Deleting a cookie is really a Set-Cookie with an expiry, and the browser only replaces the
        // existing one when the attributes match how it was issued — hence the shared options builder
        // rather than two hand-kept copies.
        ctx.Response.Cookies.Delete(AdminAuthService.SessionCookieName, SessionCookie(ctx, options));
        return Accept(ctx);
    }

    private static Task SystemStatus(HttpContext ctx, Options options, DateTime processStartedUtc)
    {
        var uptime = options.Time.GetUtcNow().UtcDateTime - processStartedUtc;
        var status = new AdminSystemStatusResponse(
            $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s",
            options.Lobbies.Count,
            options.Catalog.Count,
            Environment.WorkingSet / (1024 * 1024),
            GC.GetTotalMemory(false) / (1024 * 1024),
            options.Time.GetUtcNow().UtcDateTime.ToString("O"));

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminSystemStatusResponse, status);
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>Wraps a handler so it only runs for a request carrying a valid session cookie.</summary>
    private static RequestDelegate RequireSession(Options options, RequestDelegate handler) => ctx =>
        ctx.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var session)
        && options.Auth.ValidateSessionToken(session)
            ? handler(ctx)
            : Refuse(ctx, StatusCodes.Status401Unauthorized, "Unauthorized.");

    /// <summary>The request's password, or null when the body is missing/blank/unparseable.</summary>
    private static async Task<string?> ReadPassword(HttpContext ctx)
    {
        try
        {
            var req = await JsonSerializer.DeserializeAsync(
                ctx.Request.Body, KnockBoxProtocolContext.Default.AdminPasswordRequest);
            return string.IsNullOrWhiteSpace(req?.Password) ? null : req.Password;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void StartSession(HttpContext ctx, Options options) =>
        ctx.Response.Cookies.Append(
            AdminAuthService.SessionCookieName, options.Auth.CreateSessionToken(), SessionCookie(ctx, options));

    /// <summary>
    /// The session cookie's attributes, in ONE place so the issuing and clearing sites cannot drift.
    /// HttpOnly keeps the token away from script; SameSite=Strict means it never rides a cross-site
    /// request; Secure follows the request's scheme, or is forced on when the configured origin is https
    /// (see <see cref="Options.CookieAlwaysSecure"/>) — so the cookie hardens behind TLS without breaking
    /// the plain-HTTP loopback the portal is reached over in dev and in Docker.
    /// </summary>
    private static CookieOptions SessionCookie(HttpContext ctx, Options options) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = options.CookieAlwaysSecure || ctx.Request.IsHttps,
        Path = "/",
    };

    private static Task Accept(HttpContext ctx) =>
        WriteJson(ctx, KnockBoxProtocolContext.Default.AdminApiResponse, new AdminApiResponse(true));

    private static Task Refuse(HttpContext ctx, int status, string error) =>
        WriteJson(ctx, KnockBoxProtocolContext.Default.AdminApiResponse, new AdminApiResponse(false, error), status);

    private static Task WriteJson<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo, T value, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, value, typeInfo);
    }
}
