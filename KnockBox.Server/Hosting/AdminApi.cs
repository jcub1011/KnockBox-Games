using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KnockBox.Contracts;
using Microsoft.AspNetCore.Http.Features;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;
using KnockBox.Server.Webhooks;
// Aliased, not imported: the namespace also carries a SameSiteMode that collides with
// Microsoft.AspNetCore.Http.SameSiteMode, which the session cookie below uses.
using ContentDispositionHeaderValue = Microsoft.Net.Http.Headers.ContentDispositionHeaderValue;

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
    /// <param name="Authorities">
    /// Null when server-authority games are disabled: the count it provides is a metric, not a
    /// dependency, so the portal reports zero rather than the origin failing to build.
    /// </param>
    /// <param name="StaleAfter">
    /// How long a lobby may go without activity before the portal calls it stale and "purge stale"
    /// collects it.
    /// </param>
    public sealed record Options(
        AdminAuthService Auth,
        LobbyManager Lobbies,
        LobbyCloser Closer,
        GameCatalog Catalog,
        AdminSettingsStore Settings,
        GameLifecycleGate Lifecycle,
        AdminOperations Operations,
        PackageManager Packages,
        PackageManagerOptions PackageOptions,
        GamePackageLimits PackageLimits,
        // Null when KnockBox:MarketplaceEnabled is false — the same nullable-when-disabled precedent as
        // Authorities. Every package route except install keeps working without it.
        MarketplaceSourceRegistry? Marketplace,
        GameUpdateCoordinator? Updates,
        // Null for the same reason as Marketplace and Updates: with KnockBox:MarketplaceEnabled=false
        // there is nothing to check, so there is no timer and the schedule routes refuse with 409.
        UpdateScheduler? Scheduler,
        AdminLogBuffer Logs,
        DiskUsageReporter Disk,
        RelayMetrics Relay,
        AuthorityMetrics Authority,
        MetricHistory History,
        int MetricSampleSeconds,
        LimitsProvider Limits,
        // Null when KnockBox:WebhooksEnabled is false — the same nullable-when-disabled precedent as
        // Marketplace. Registered endpoints are still listed; the mutating routes refuse with 409.
        WebhookDispatcher? Webhooks,
        WebhookLogSink? WebhookLog,
        WebhookOptions WebhookOptions,
        ConnectionManager Connections,
        ServerAuthorityManager? Authorities,
        ContentPaths.Resolved Paths,
        DeploymentDiagnostics Diagnostics,
        TimeProvider Time,
        ILogger Logger,
        int LoginAttemptsPerMinutePerIp,
        int LoginAttemptsPerMinuteGlobal,
        bool CookieAlwaysSecure,
        TimeSpan StaleAfter);

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
            // JsonRequired, not Json: all three ALWAYS carry a body, so the content type is demanded
            // outright. Without that, an HTML form with enctype="text/plain" posts a body that parses as
            // valid JSON, and setup needs no cookie (it is claim-on-first-use) — so a page the operator
            // merely visits could claim an unclaimed portal through their own browser, from outside the
            // loopback binding that is supposed to be the boundary. See WriteGuard.
            routes.MapPost("/admin/api/auth/setup",
                WriteGuard(ctx => Setup(ctx, options), MediaKind.JsonRequired));
            routes.MapPost("/admin/api/auth/login",
                WriteGuard(ctx => Login(ctx, options), MediaKind.JsonRequired));
            routes.MapPost("/admin/api/auth/logout",
                WriteGuard(ctx => Logout(ctx, options), MediaKind.JsonRequired));

            // ── Reads ──
            routes.MapGet("/admin/api/system/status",
                RequireSession(options, ctx => SystemStatus(ctx, options, processStartedUtc)));
            routes.MapGet("/admin/api/metrics", RequireSession(options, ctx => Metrics(ctx, options)));
            routes.MapGet("/admin/api/metrics/history",
                RequireSession(options, ctx => MetricHistoryFeed(ctx, options)));
            routes.MapGet("/admin/api/lobbies", RequireSession(options, ctx => Lobbies(ctx, options)));
            routes.MapGet("/admin/api/games", RequireSession(options, ctx => Games(ctx, options)));
            routes.MapGet("/admin/api/games/{id}/export",
                RequireSession(options, ctx => ExportGame(ctx, options)));
            routes.MapGet("/admin/api/logs", RequireSession(options, ctx => Logs(ctx, options)));
            routes.MapGet("/admin/api/logs/files", RequireSession(options, ctx => LogFiles(ctx, options)));
            routes.MapGet("/admin/api/logs/files/{name}",
                RequireSession(options, ctx => DownloadLogFile(ctx, options)));
            routes.MapGet("/admin/api/packages/jobs", RequireSession(options, ctx => Jobs(ctx, options)));
            routes.MapGet("/admin/api/packages/jobs/{jobId}", RequireSession(options, ctx => Job(ctx, options)));
            routes.MapGet("/admin/api/marketplace/catalog", RequireSession(options, ctx => Catalog(ctx, options)));
            routes.MapGet("/admin/api/limits", RequireSession(options, ctx => Limits(ctx, options)));
            routes.MapGet("/admin/api/room-codes", RequireSession(options, ctx => RoomCodes(ctx, options)));
            routes.MapGet("/admin/api/announcement", RequireSession(options, ctx => Announcement(ctx, options)));
            routes.MapGet("/admin/api/webhooks", RequireSession(options, ctx => Webhooks(ctx, options)));
            routes.MapGet("/admin/api/updates/schedule",
                RequireSession(options, ctx => UpdateScheduleRead(ctx, options)));

            // ── Mutations ──
            // Every one of these is gated by RequireSession AND by the mutation guard inside WriteGuard,
            // which rejects a request that didn't come from the portal itself.
            routes.MapPost("/admin/api/lobbies/close",
                RequireSession(options, WriteGuard(ctx => CloseLobbies(ctx, options))));
            routes.MapPost("/admin/api/lobbies/purge-stale",
                RequireSession(options, WriteGuard(ctx => PurgeStale(ctx, options))));
            routes.MapPost("/admin/api/lobbies/{code}/close",
                RequireSession(options, WriteGuard(ctx => CloseLobby(ctx, options))));
            routes.MapPost("/admin/api/lobbies/{code}/kick",
                RequireSession(options, WriteGuard(ctx => KickPlayer(ctx, options))));
            routes.MapPost("/admin/api/games/rescan",
                RequireSession(options, WriteGuard(ctx => Rescan(ctx, options))));
            routes.MapPost("/admin/api/games/{id}/availability",
                RequireSession(options, WriteGuard(ctx => SetAvailability(ctx, options))));
            routes.MapPost("/admin/api/games/{id}/delete",
                RequireSession(options, WriteGuard(ctx => DeleteGame(ctx, options))));
            routes.MapPost("/admin/api/maintenance",
                RequireSession(options, WriteGuard(ctx => SetMaintenance(ctx, options))));
            routes.MapPost("/admin/api/limits",
                RequireSession(options, WriteGuard(ctx => SetLimits(ctx, options))));
            routes.MapPost("/admin/api/room-codes",
                RequireSession(options, WriteGuard(ctx => SetRoomCodes(ctx, options))));
            routes.MapPost("/admin/api/announcement",
                RequireSession(options, WriteGuard(ctx => PostAnnouncement(ctx, options))));
            routes.MapPost("/admin/api/announcement/delete",
                RequireSession(options, WriteGuard(ctx => ClearAnnouncement(ctx, options))));
            routes.MapPost("/admin/api/webhooks",
                RequireSession(options, WriteGuard(ctx => AddWebhook(ctx, options))));
            routes.MapPost("/admin/api/webhooks/{id}/delete",
                RequireSession(options, WriteGuard(ctx => RemoveWebhook(ctx, options))));
            routes.MapPost("/admin/api/webhooks/{id}/test",
                RequireSession(options, WriteGuard(ctx => TestWebhook(ctx, options))));
            routes.MapPost("/admin/api/updates/schedule",
                RequireSession(options, WriteGuard(ctx => SetUpdateSchedule(ctx, options))));

            // ── Packages ──
            // Separate from /games/* on purpose: these are the INSTALLED-side lifecycle, and every one of
            // them keeps working on an air-gapped host where the marketplace is switched off entirely.
            routes.MapPost("/admin/api/packages/upload",
                RequireSession(options, WriteGuard(ctx => UploadPackage(ctx, options), MediaKind.Package)));
            routes.MapPost("/admin/api/packages/{id}/rollback",
                RequireSession(options, WriteGuard(ctx => Rollback(ctx, options))));
            routes.MapPost("/admin/api/packages/{id}/uninstall",
                RequireSession(options, WriteGuard(ctx => Uninstall(ctx, options))));
            routes.MapPost("/admin/api/packages/jobs/{jobId}/cancel",
                RequireSession(options, WriteGuard(ctx => CancelJob(ctx, options))));

            // ── Marketplace ──
            routes.MapPost("/admin/api/marketplace/install/{id}",
                RequireSession(options, WriteGuard(ctx => InstallFromMarketplace(ctx, options))));
            routes.MapPost("/admin/api/marketplace/sources",
                RequireSession(options, WriteGuard(ctx => AddSource(ctx, options))));
            routes.MapPost("/admin/api/marketplace/sources/{id}/delete",
                RequireSession(options, WriteGuard(ctx => RemoveSource(ctx, options))));
            routes.MapPost("/admin/api/marketplace/sources/{id}/enabled",
                RequireSession(options, WriteGuard(ctx => SetSourceEnabled(ctx, options))));
            routes.MapPost("/admin/api/marketplace/check",
                RequireSession(options, WriteGuard(ctx => CheckForUpdates(ctx, options))));
            routes.MapPost("/admin/api/packages/{id}/update-policy",
                RequireSession(options, WriteGuard(ctx => SetUpdatePolicy(ctx, options))));
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
        var now = options.Time.GetUtcNow();
        var uptime = now.UtcDateTime - processStartedUtc;

        // Process CPU. Reported as a LIFETIME average plus the raw total, not an instantaneous rate: a
        // rate needs two samples, and taking the second one here would mean either sleeping inside the
        // request or keeping per-viewer state. The portal polls anyway, so it differences the total itself.
        double cpuSeconds;
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            cpuSeconds = self.TotalProcessorTime.TotalSeconds;
        var cores = Environment.ProcessorCount;
        var wallSeconds = uptime.TotalSeconds;
        var cpuPercent = wallSeconds > 0 && cores > 0 ? cpuSeconds / (wallSeconds * cores) * 100 : 0;

        var status = new AdminSystemStatusResponse(
            $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s",
            options.Lobbies.Count,
            options.Catalog.Count,
            Environment.WorkingSet / (1024 * 1024),
            GC.GetTotalMemory(false) / (1024 * 1024),
            now.UtcDateTime.ToString("O"),
            options.Connections.ControlCount,
            options.Connections.GameCount,
            options.Authorities?.ActorCount ?? 0,
            options.Settings.MaintenanceMode,
            options.Settings.MaintenanceMessage,
            Math.Round(cpuPercent, 2),
            Math.Round(cpuSeconds, 2),
            cores,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            options.Catalog.ScanError,
            options.Settings.LoadError,
            // Current() re-evaluates the live probes, so a problem that has been fixed disappears from the
            // dashboard on the next poll without a restart.
            [.. options.Diagnostics.Current().Select(i => new AdminDiagnosticIssue(i.Title, i.Detail, i.Blocking))]);

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminSystemStatusResponse, status);
    }

    private static Task Metrics(HttpContext ctx, Options options)
    {
        // Per-game socket totals are aggregated from the live game sockets, mapping each one's lobby to
        // its game. The relay counters survive a lobby closing; these don't, and the pair is what shows
        // "this game has moved 40 GB, and right now 12 sockets are carrying it".
        var socketFrames = new Dictionary<string, (long Frames, long Bytes, long Dropped, int Sockets)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var connection in options.Connections.GameConnections())
        {
            if (connection.LobbyId is null) continue;
            var lobby = options.Lobbies.Get(connection.LobbyId);
            if (lobby is null) continue;
            socketFrames.TryGetValue(lobby.GameId, out var running);
            socketFrames[lobby.GameId] = (
                running.Frames + connection.SentFrames,
                running.Bytes + connection.SentBytes,
                running.Dropped + connection.DroppedFrames,
                running.Sockets + 1);
        }

        // Lobby and player counts per game, from the lobby snapshot rather than the socket map, so a
        // lobby whose players haven't attached their game sockets yet still appears.
        var lobbyCounts = new Dictionary<string, (int Lobbies, int Players)>(StringComparer.OrdinalIgnoreCase);
        foreach (var lobby in options.Lobbies.Snapshot())
        {
            lobbyCounts.TryGetValue(lobby.GameId, out var running);
            lobbyCounts[lobby.GameId] = (running.Lobbies + 1, running.Players + lobby.Count);
        }

        var games = new List<AdminGameRelayMetrics>();
        foreach (var relay in options.Relay.Snapshot())
        {
            socketFrames.TryGetValue(relay.GameId, out var sockets);
            lobbyCounts.TryGetValue(relay.GameId, out var counts);
            // Zero for every game that isn't server-authority, and that zero is a measurement, not a gap:
            // a browser-side game executes no code in this process.
            var authority = options.Authority.For(relay.GameId);
            games.Add(new AdminGameRelayMetrics(
                relay.GameId, relay.FramesIn, relay.FramesOut, relay.BytesIn, relay.BytesOut,
                relay.FramesDropped, Math.Round(relay.FanOut, 2),
                counts.Lobbies, counts.Players,
                sockets.Frames, sockets.Bytes, sockets.Dropped,
                authority?.Calls ?? 0,
                Math.Round(authority?.CpuSeconds ?? 0, 3),
                Math.Round(authority?.AverageCallMs ?? 0, 3),
                Math.Round(authority?.MaxCallMs ?? 0, 3),
                authority?.Errors ?? 0));
        }

        // Server-wide outbound totals across BOTH planes: the control sockets carry lobby events too, and
        // a stuck shell socket dropping frames is exactly as interesting as a stuck game one.
        long framesSent = 0, bytesSent = 0, framesDropped = 0;
        foreach (var connection in options.Connections.GameConnections().Concat(options.Connections.ControlConnections()))
        {
            framesSent += connection.SentFrames;
            bytesSent += connection.SentBytes;
            framesDropped += connection.DroppedFrames;
        }

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminMetricsResponse, new AdminMetricsResponse(
            games,
            options.Connections.ControlCount,
            options.Connections.GameCount,
            framesSent,
            bytesSent,
            framesDropped,
            0, // reserved: no per-IP limiter is reachable from here (each connection owns its own bucket)
            options.Time.GetUtcNow().UtcDateTime.ToString("O")));
    }

    private static Task Lobbies(HttpContext ctx, Options options)
    {
        var now = options.Time.GetUtcNow();
        var summaries = new List<AdminLobbySummary>();

        foreach (var lobby in options.Lobbies.Snapshot())
        {
            options.Catalog.TryGet(lobby.GameId, out var manifest);
            var disconnected = lobby.DisconnectedMembers();
            var dropped = disconnected.ToDictionary(d => d.PlayerId, d => d.Since, StringComparer.Ordinal);
            var hostId = lobby.HostId;

            var members = new List<AdminLobbyMember>();
            foreach (var player in lobby.Players)
            {
                var since = dropped.TryGetValue(player.Id, out var at) ? at : (DateTimeOffset?)null;
                members.Add(new AdminLobbyMember(
                    player.Id,
                    player.DisplayName,
                    player.Id == hostId,
                    since is null,
                    since is null ? 0 : (long)(now - since.Value).TotalSeconds));
            }

            summaries.Add(new AdminLobbySummary(
                lobby.Id,
                lobby.GameId,
                manifest?.Name,
                manifest?.Version,
                lobby.Count,
                lobby.MaxPlayers,
                disconnected.Count,
                hostId,
                lobby.Open,
                lobby.IsServerAuthority,
                lobby.CreatedAt.UtcDateTime.ToString("O"),
                (long)(now - lobby.CreatedAt).TotalSeconds,
                (long)(now - lobby.LastActivityUtc).TotalSeconds,
                StatusName(options.Operations.Classify(lobby, now, options.StaleAfter)),
                members));
        }

        // Oldest first: a directory an operator scans top-down should lead with what has been running
        // longest, which is what a stuck session looks like.
        summaries.Sort((a, b) => b.AgeSeconds.CompareTo(a.AgeSeconds));

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminLobbiesResponse, new AdminLobbiesResponse(
            summaries,
            (int)options.StaleAfter.TotalMinutes,
            now.UtcDateTime.ToString("O")));
    }

    private static string StatusName(AdminOperations.LobbyState state) => state switch
    {
        AdminOperations.LobbyState.Waiting => "waiting",
        AdminOperations.LobbyState.InGame => "in-game",
        AdminOperations.LobbyState.Empty => "empty",
        _ => "stale",
    };

    private static Task Games(HttpContext ctx, Options options)
    {
        var disk = options.Disk.Current();
        var byId = disk.Games.ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        var lobbyCounts = new Dictionary<string, (int Lobbies, int Players)>(StringComparer.OrdinalIgnoreCase);
        foreach (var lobby in options.Lobbies.Snapshot())
        {
            lobbyCounts.TryGetValue(lobby.GameId, out var running);
            lobbyCounts[lobby.GameId] = (running.Lobbies + 1, running.Players + lobby.Count);
        }

        var games = new List<AdminGameSummary>();
        foreach (var (id, location) in options.Catalog.GameLocations)
        {
            var manifest = location.Manifest;
            byId.TryGetValue(id, out var usage);
            lobbyCounts.TryGetValue(id, out var counts);

            // Resolved from the marker, not derived as GamesRoot/<id>.kbg: the installer accepts any
            // *.kbg file name, and a portal-installed package lives in the managed root entirely.
            var package = GamePackageLocations.Find(options.Paths, manifest.Id);
            var underPackages = location.Directory.StartsWith(options.Paths.GamesUnpackedRoot, StringComparison.OrdinalIgnoreCase);
            // Whether Delete could work is answered here so the portal can disable the button and say why,
            // rather than offering an action that always fails on a read-only games mount.
            var blocked = DeleteBlockedReason(options, location.Directory, package);

            games.Add(new AdminGameSummary(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                options.Settings.GetAvailability(manifest.Id).ToString().ToLowerInvariant(),
                manifest.MaxPlayers,
                manifest.ServerAuthority is not null,
                location.Directory,
                underPackages ? "packages" : "games",
                package is not null,
                package?.Root,
                usage?.TotalBytes ?? 0,
                usage?.DirectoryBytes ?? 0,
                usage?.CompressedBytes ?? 0,
                usage?.PackageBytes ?? 0,
                usage?.BackupBytes ?? 0,
                counts.Lobbies,
                counts.Players,
                blocked is null,
                blocked,
                Camel(options.Lifecycle.StateOf(manifest.Id).ToString()),
                Camel(options.Settings.GetUpdatePolicy(manifest.Id).ToString()),
                options.Packages.Jobs.ActiveFor(manifest.Id)?.JobId,
                manifest.Sdk,
                KnockBoxSdk.StatusOf(manifest.Sdk)));
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminGamesResponse, new AdminGamesResponse(
            games,
            options.Paths.GamesRoot,
            options.Paths.GamesUnpackedRoot,
            options.Catalog.ScanError,
            disk.TakenAt.UtcDateTime.ToString("O"),
            disk.CompressedCacheBytes,
            disk.LogsBytes,
            options.Paths.GamesManagedRoot,
            disk.ManagedRootBytes,
            KnockBoxSdk.VersionString));
    }

    // A cheap, read-only guess at whether the files could be removed: it checks the directories that
    // would have to be written to. AdminOperations.DeleteGame probes them for real before touching
    // anything, so this only decides whether the portal offers the button.
    //
    // Read through DiskUsageReporter's cache rather than probed here: the probe answers by WRITING a
    // file, this runs once per game on every poll of the catalog tab, and the games root is watched — so
    // probing directly meant an open tab scheduling a catalog rediscovery every poll, forever. Being a
    // minute stale is free, because the delete itself re-probes before removing anything.
    private static string? DeleteBlockedReason(
        Options options, string directory, GamePackageLocations.PackageLocation? package)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        if (parent is not null && options.Disk.WhyNotWritable(parent) is not null)
            return $"'{parent}' is not writable by the server (in production the games folder is mounted read-only).";
        // The package's OWN root, not always games/: a portal-installed package sits in the managed root,
        // which is writable by design — which is what makes deleting those games work in production.
        if (package is { } source && Path.GetDirectoryName(source.Path) is { } packageDir
            && options.Disk.WhyNotWritable(packageDir) is not null)
            return $"the source package in '{packageDir}' can't be removed, so the game would reinstall itself.";
        return null;
    }

    private static async Task ExportGame(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        if (!options.Catalog.GameLocations.TryGetValue(id, out var location))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No installed game with id '{id}'.");
            return;
        }

        try
        {
            // Opened before a single header is written: everything that can fail — the walk, a per-file
            // read, building the archive — fails here, where a clean refusal is still possible. Setting
            // the headers first meant a mid-stream IOException logged a warning and handed the operator a
            // truncated archive under HTTP 200.
            await using var export = await GamePackageExporter.OpenAsync(location, options.Paths, ctx.RequestAborted);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = export.ContentType;
            // Known, so a short read is something the browser reports rather than saves.
            ctx.Response.ContentLength = export.Length;
            ctx.Response.Headers.ContentDisposition = Attachment(export.FileName);
            await export.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            if (!ctx.Response.HasStarted)
                await Refuse(ctx, StatusCodes.Status500InternalServerError, $"Could not export game '{id}' ({ex.Message}).");
            options.Logger.LogWarning(ex, "Admin export of game {GameId} failed.", id);
        }
    }

    private static Task Logs(HttpContext ctx, Options options)
    {
        var query = ctx.Request.Query;
        _ = long.TryParse(query["after"], out var after);
        _ = int.TryParse(query["limit"], out var limit);
        var level = ParseLevel(query["level"]);
        var category = NullIfBlank(query["category"]);
        var search = NullIfBlank(query["q"]);

        var entries = options.Logs.Read(
            after, level, category, search, limit is > 0 and <= 2000 ? limit : 500);

        var mapped = new List<AdminLogEntry>(entries.Count);
        foreach (var entry in entries)
        {
            mapped.Add(new AdminLogEntry(
                entry.Sequence,
                entry.Timestamp.UtcDateTime.ToString("O"),
                entry.Level.ToString(),
                entry.Category,
                entry.Message,
                entry.Exception));
        }

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminLogsResponse, new AdminLogsResponse(
            mapped, options.Logs.LastSequence, options.Logs.TotalWritten, options.Logs.Count));
    }

    private static Serilog.Events.LogEventLevel? ParseLevel(string? value) =>
        Enum.TryParse<Serilog.Events.LogEventLevel>(value, ignoreCase: true, out var level) ? level : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Task LogFiles(HttpContext ctx, Options options)
    {
        var files = new List<AdminLogFile>();
        string? error = null;
        try
        {
            if (Directory.Exists(options.Paths.LogsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(options.Paths.LogsRoot, "*.log"))
                {
                    var info = new FileInfo(path);
                    files.Add(new AdminLogFile(info.Name, info.Length, info.LastWriteTimeUtc.ToString("O")));
                }
                files.Sort((a, b) => string.Compare(b.Name, a.Name, StringComparison.Ordinal)); // newest day first
            }
            else
            {
                error = $"The logs directory '{options.Paths.LogsRoot}' does not exist.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"The logs directory '{options.Paths.LogsRoot}' could not be listed ({ex.Message}).";
        }

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminLogFilesResponse,
            new AdminLogFilesResponse(files, options.Paths.LogsRoot, error));
    }

    private static async Task DownloadLogFile(HttpContext ctx, Options options)
    {
        var requested = ctx.GetRouteValue("name") as string ?? "";

        // The name is matched against the directory's OWN listing rather than string-checked for traversal.
        // Validating a path by inspecting it is how `…//authority.js` slipped past the game-origin gate
        // once; letting the filesystem enumerate what exists removes the parsing step that can be fooled.
        string? resolved = null;
        try
        {
            if (Directory.Exists(options.Paths.LogsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(options.Paths.LogsRoot, "*.log"))
                {
                    if (!string.Equals(Path.GetFileName(path), requested, StringComparison.OrdinalIgnoreCase)) continue;
                    resolved = path;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Refuse(ctx, StatusCodes.Status500InternalServerError,
                $"The logs directory could not be read ({ex.Message}).");
            return;
        }

        if (resolved is null)
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No log file named '{requested}'.");
            return;
        }

        try
        {
            // FileShare.ReadWrite because Serilog holds the current day's file open for writing — without
            // it, downloading today's log (the one an operator actually wants) fails with a sharing error.
            await using var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = Attachment(Path.GetFileName(resolved));
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Headers may already be out, in which case there is nothing to say but stop writing.
            if (!ctx.Response.HasStarted)
                await Refuse(ctx, StatusCodes.Status500InternalServerError, $"Could not read the log file ({ex.Message}).");
            options.Logger.LogWarning(ex, "Admin log download of {Path} failed.", resolved);
        }
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    private static async Task CloseLobbies(HttpContext ctx, Options options)
    {
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminCloseLobbiesRequest)
                   ?? new AdminCloseLobbiesRequest();
        var reason = Reason(body.Reason, "An administrator closed this lobby.");

        var closed = string.IsNullOrWhiteSpace(body.GameId)
            ? options.Closer.CloseAll(reason)
            : options.Closer.CloseForGame(body.GameId.Trim(), reason);

        options.Logger.LogWarning("Admin bulk-closed {Count} lobby/lobbies (game: {GameId}).",
            closed, body.GameId ?? "all");
        await WriteAction(ctx, new AdminActionResponse(true, Affected: closed));
    }

    // ── Packages ──────────────────────────────────────────────────────────────

    private static Task Jobs(HttpContext ctx, Options options)
    {
        _ = long.TryParse(ctx.Request.Query["after"], out var after);
        _ = int.TryParse(ctx.Request.Query["limit"], out var limit);
        var registry = options.Packages.Jobs;

        // A cursor of 0 means "I have nothing" — hand over the whole retained set so a portal that just
        // opened the tab, or came back to it, sees outcomes it missed rather than an empty strip.
        var jobs = after > 0
            ? registry.Read(after, limit is > 0 and <= 500 ? limit : 200)
            : registry.Snapshot();

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobsResponse, new AdminJobsResponse(
            [.. jobs.Select(ToSummary)],
            registry.LastSequence,
            registry.ActiveCount,
            registry.Count));
    }

    private static Task Job(HttpContext ctx, Options options)
    {
        var jobId = ctx.GetRouteValue("jobId") as string ?? "";
        return options.Packages.Jobs.Get(jobId) is { } job
            ? WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobSummary, ToSummary(job))
            : Refuse(ctx, StatusCodes.Status404NotFound, $"No job with id '{jobId}'.");
    }

    private static AdminJobSummary ToSummary(PackageJob job) => new(
        job.JobId,
        job.Sequence,
        Camel(job.Kind.ToString()),
        Camel(job.Source.ToString()),
        job.GameId,
        job.GameName,
        job.FromVersion,
        job.ToVersion,
        Camel(job.Status.ToString()),
        job.Phase,
        job.BytesDone,
        job.BytesTotal,
        Camel(job.Mode.ToString()),
        job.StartedAt.UtcDateTime.ToString("O"),
        job.EndedAt?.UtcDateTime.ToString("O"),
        job.Error,
        job.Warning,
        job.LobbiesWaiting,
        job.Cancellable,
        job.IsTerminal);

    // Enum names go over the wire camelCased, matching how GameAvailability is reported: the portal
    // compares them as strings, and "waitingForLobbies" is what a JS switch reads naturally.
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static async Task UploadPackage(HttpContext ctx, Options options)
    {
        var manager = options.Packages;
        if (manager.InstallBlockedReason() is { } blocked)
        {
            // 409, not 500: a deployment limit rather than a fault — the same meaning DeleteGame gives it.
            await Refuse(ctx, StatusCodes.Status409Conflict, blocked);
            return;
        }

        // Kestrel's default 30 MB body cap would reject a large game long before MaxPackageBytes had
        // anything to say about it. Raised for THIS endpoint only — no other route has any business
        // accepting a body this size.
        //
        // Both uses honour GamePackageLimits' convention that a non-positive value disables that
        // individual check — the same `> 0` PackageManager.ReceiveAsync applies, and for the same reason
        // it had to: MaxPackageBytes=0 is documented as "no limit", but read literally here it set a
        // 4096-byte Kestrel cap and refused every upload with a message about a 0-byte limit.
        var maxBytes = options.PackageLimits.MaxBytes;
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = maxBytes > 0 ? maxBytes + 4096 : null;

        // Rejected in one round trip when the client is honest about the size. The real enforcement is
        // still the byte count while streaming, because Content-Length is the client's claim.
        if (maxBytes > 0 && ctx.Request.ContentLength > maxBytes)
        {
            await Refuse(ctx, StatusCodes.Status413PayloadTooLarge,
                $"The package exceeds the {maxBytes:N0}-byte limit " +
                "(KnockBox:MaxPackageBytes).");
            return;
        }

        PackageManager.StagedPackage staged;
        try
        {
            staged = await manager.ReceiveAsync(ctx.Request.Body, ctx.RequestAborted);
        }
        catch (PackageManager.PackageTooLargeException ex)
        {
            await Refuse(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Refuse(ctx, StatusCodes.Status500InternalServerError,
                $"The upload could not be stored ({ex.Message}).");
            return;
        }

        var mode = ParseMode(ctx.Request.Query["mode"]);
        var start = manager.StartInstallFromFile(staged, PackageJobSource.Upload, mode);
        if (!start.Started)
        {
            await Refuse(ctx, StatusFor(start.Refusal), start.Error ?? "The package could not be installed.");
            return;
        }

        options.Logger.LogInformation("Admin uploaded a package for '{GameId}' ({Bytes} bytes).",
            start.Job!.GameId, staged.Bytes);
        // 202: the bytes are accepted and validated, but nothing has been swapped yet. Claiming 200 here
        // would say the game is installed when it may still be waiting for a lobby to end.
        await WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobResponse,
            new AdminJobResponse(true, JobId: start.Job.JobId, Detail: start.Job.Phase),
            StatusCodes.Status202Accepted);
    }

    private static async Task Rollback(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminRollbackRequest)
                   ?? new AdminRollbackRequest();

        var start = options.Packages.StartRollback(id, NullIfBlank(body.Version), ParseMode(body.Mode));
        if (!start.Started)
        {
            await Refuse(ctx, StatusFor(start.Refusal), start.Error ?? "The rollback could not be started.");
            return;
        }

        options.Logger.LogWarning("Admin started a rollback of '{GameId}' to {Version}.",
            start.Job!.GameId, start.Job.ToVersion ?? "the retained version");
        await WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobResponse,
            new AdminJobResponse(true, JobId: start.Job.JobId, Detail: start.Job.Phase),
            StatusCodes.Status202Accepted);
    }

    private static async Task Uninstall(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        if (!options.Catalog.GameLocations.TryGetValue(id, out var location))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No installed game with id '{id}'.");
            return;
        }

        var gameId = location.Manifest.Id;
        var registry = options.Packages.Jobs;
        if (registry.ActiveFor(gameId) is { } running)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                $"'{gameId}' already has a {Camel(running.Kind.ToString())} in progress ({running.Phase}).");
            return;
        }

        // Deletion itself is AdminOperations' job — it already closes lobbies first, probes every parent
        // for writability before removing anything, and cleans the compressed cache and backups. This
        // only wraps it so the operation shows up in the same feed as everything else the portal starts.
        var job = registry.Create(PackageJobKind.Uninstall, PackageJobSource.None, gameId,
            location.Manifest.Name, location.Manifest.Version, null, PackageApplyMode.Force);
        var operations = options.Operations;
        var logger = options.Logger;

        _ = Task.Run(() =>
        {
            // Wrapped the way PackageManager wraps its own workers, and for a sharper reason here: this
            // job is already Applying, which is past the point Cancel will act on. An escaping exception
            // would fault the task silently and leave the job non-terminal forever — ActiveFor would keep
            // returning it, so every later install/update/rollback/uninstall of this game would 409 until
            // the process restarted. DeleteGame only catches IO/UnauthorizedAccess around its own
            // deletes, so a throw out of lobby closing or a bad path reaches here.
            try
            {
                registry.SetStatus(job.JobId, PackageJobStatus.Applying, "Removing files.");
                var result = operations.DeleteGame(gameId);
                if (result.Success)
                {
                    logger.LogWarning("Admin uninstalled '{GameId}', closing {Lobbies} lobby/lobbies.",
                        gameId, result.LobbiesClosed);
                    registry.Finish(job.JobId, PackageJobStatus.Succeeded,
                        result.LobbiesClosed > 0
                            ? $"Uninstalled, and closed {result.LobbiesClosed} running lobby/lobbies."
                            : "Uninstalled.");
                }
                else
                {
                    registry.Finish(job.JobId, PackageJobStatus.Failed, "Failed.", result.Error);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Uninstall job {JobId} for '{GameId}' failed.", job.JobId, gameId);
                registry.Finish(job.JobId, PackageJobStatus.Failed, "Failed.", ex.Message);
            }
        });

        await WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobResponse,
            new AdminJobResponse(true, JobId: job.JobId, Detail: "Uninstalling."),
            StatusCodes.Status202Accepted);
    }

    private static Task CancelJob(HttpContext ctx, Options options)
    {
        var jobId = ctx.GetRouteValue("jobId") as string ?? "";
        return options.Packages.Jobs.Cancel(jobId) switch
        {
            PackageCancelOutcome.Cancelled => WriteAction(ctx,
                new AdminActionResponse(true, Detail: "Cancelling; the job stops at its next checkpoint.")),
            PackageCancelOutcome.NotFound => Refuse(ctx, StatusCodes.Status404NotFound,
                $"No job with id '{jobId}'."),
            // 409 rather than 400: nothing about the request is wrong, the job has simply passed the
            // point where stopping would leave a half-swapped game directory behind.
            _ => Refuse(ctx, StatusCodes.Status409Conflict,
                "This job is already installing files and can no longer be cancelled."),
        };
    }

    // ── Marketplace ───────────────────────────────────────────────────────────

    private static async Task Catalog(HttpContext ctx, Options options)
    {
        var manager = options.Packages;
        var registry = options.Marketplace;
        var jobs = manager.Jobs;
        var blocked = manager.InstallBlockedReason();

        // The cached snapshot by default. ?refresh=1 is the ONLY thing that reaches the network: a fetch
        // carries a 30-second timeout, so making it the tab's poll target would be indefensible.
        var refresh = ctx.Request.Query["refresh"] == "1";
        IReadOnlyList<SourceCatalog> fetched = registry is null
            ? []
            : await registry.FetchAllAsync(refresh, ctx.RequestAborted);

        var installed = options.Catalog.GameLocations;
        var managed = new HashSet<string>(
            installed.Keys.Where(id => GamePackageLocations.Find(options.Paths, id) is { Managed: true }),
            StringComparer.OrdinalIgnoreCase);

        var lobbyCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var lobby in options.Lobbies.Snapshot())
            lobbyCounts[lobby.GameId] = lobbyCounts.GetValueOrDefault(lobby.GameId) + 1;

        var entries = MarketplaceProjection.Project(fetched, installed, managed, KnockBoxVersion.Current)
            .Select(e => new AdminMarketplaceEntry(
                e.Id, e.Name, e.Description, e.Author, e.Tags,
                e.License, e.Homepage, e.Bugs, e.ContentRating, e.MinPlayers, e.MaxPlayers,
                e.AvailableVersion, e.InstalledVersion, e.Status, e.Reason,
                e.SizeBytes, e.PublishedAt, e.MinAppVersion, e.MaxAppVersion,
                e.SourceId, e.SourceName, e.ShadowedBy, e.Managed, e.Installed,
                lobbyCounts.GetValueOrDefault(e.Id),
                jobs.ActiveFor(e.Id)?.JobId,
                [.. manager.Backups(e.Id).Select(b => new AdminRetainedVersion(
                    b.Version, b.Bytes, b.RetainedAt.UtcDateTime.ToString("O")))]))
            .ToList();

        var sources = (registry?.Sources ?? []).Select(s =>
        {
            var result = fetched.FirstOrDefault(f => f.Source.Id == s.Id);
            return new AdminMarketplaceSource(
                s.Id, s.Name, s.CatalogUrl, s.DownloadBaseUrl, s.Enabled,
                BuiltIn: s.Id == MarketplaceSourceRegistry.OfficialId,
                Entries: result?.Catalog?.Plugins?.Count ?? 0,
                Error: result?.Error);
        }).ToList();

        await WriteJson(ctx, KnockBoxProtocolContext.Default.AdminMarketplaceResponse,
            new AdminMarketplaceResponse(
                entries,
                sources,
                [.. jobs.Snapshot().Select(ToSummary)],
                jobs.LastSequence,
                registry is not null,
                KnockBoxVersion.Current.ToString(),
                fetched.Count > 0 ? options.Time.GetUtcNow().UtcDateTime.ToString("O") : null,
                registry?.MaxSources ?? 0,
                options.PackageOptions.BackupCount,
                options.PackageLimits.MaxBytes,
                blocked is null,
                blocked,
                options.Paths.GamesManagedRoot));
    }

    private static async Task InstallFromMarketplace(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        if (options.Marketplace is not { } registry)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "The marketplace is disabled (KnockBox:MarketplaceEnabled=false). Upload a .kbg instead.");
            return;
        }

        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminInstallRequest)
                   ?? new AdminInstallRequest();

        // Resolved from the cached catalog, so clicking Install does not wait on a network round trip
        // the operator already paid for when the page loaded.
        var fetched = await registry.FetchAllAsync(false, ctx.RequestAborted);
        MarketplacePlugin? plugin = null;
        MarketplaceClient? client = null;
        foreach (var source in fetched)
        {
            if (body.SourceId is { Length: > 0 } wanted
                && !string.Equals(source.Source.Id, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            // p?.Id, not p.Id: Plugins comes from catalog JSON we did not write, where `null` survives as
            // a list element whatever the element type says. Both other readers already guard it
            // (MarketplaceClient, PluginUpdateEvaluator) — without it a stray null renders the tab fine
            // and then 500s the moment somebody clicks Install.
            if (source.Catalog?.Plugins?.FirstOrDefault(
                    p => string.Equals(p?.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } match) continue;
            plugin = match;
            client = registry.For(source.Source.Id);
            break;
        }

        if (plugin is null || client is null)
        {
            await Refuse(ctx, StatusCodes.Status404NotFound,
                $"No marketplace entry with id '{id}'" +
                (body.SourceId is { Length: > 0 } s ? $" in source '{s}'." : "."));
            return;
        }

        // "Install anyways" is the operator overriding a min/maxAppVersion bound, and the override has to
        // stop at the point where PLAYERS would meet the result. GameManifest carries no version bounds,
        // so once the package is extracted nothing server-side can tell it was force-installed — the game
        // would simply be Available, and a player could start a lobby against a build this server was
        // told it cannot run. Staged is exactly the state for that: hidden from the catalog, still
        // startable by direct link, so the operator can try it without putting it in front of anyone.
        //
        // Set BEFORE the job starts, because availability is keyed by id and persisted, so it does not
        // need the install to have finished — and there is no window in which the extracted game is
        // listed. For an update this also hides the working current version while the incompatible one
        // installs, which is the honest reading of "you are replacing it with something that may not run".
        var incompatible = PluginUpdateEvaluator.Evaluate(
            plugin, options.Catalog.GameLocations, KnockBoxVersion.Current)
            .Status == PluginUpdateStatus.Incompatible;
        string? staged = null;
        if (incompatible)
        {
            options.Settings.SetAvailability(plugin.Id!, GameAvailability.Staged);
            staged = $"'{plugin.Id}' declares it needs a different KnockBox version, so it has been staged — " +
                     "hidden from the game list, but startable from its own link. Set it to Available once you " +
                     "have confirmed it runs.";
            options.Logger.LogWarning(
                "Admin force-installed incompatible '{GameId}' {Version}; staging it so players cannot start it.",
                plugin.Id, plugin.Version ?? "(no version)");
        }

        var start = options.Packages.StartMarketplaceInstall(client, plugin, ParseMode(body.Mode));
        if (!start.Started)
        {
            await Refuse(ctx, StatusFor(start.Refusal), start.Error ?? "The install could not be started.");
            return;
        }

        options.Logger.LogInformation("Admin started a marketplace install of '{GameId}' {Version}.",
            start.Job!.GameId, plugin.Version ?? "(no version)");
        await WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobResponse,
            new AdminJobResponse(true, JobId: start.Job.JobId, Detail: staged ?? start.Job.Phase),
            StatusCodes.Status202Accepted);
    }

    private static async Task AddSource(HttpContext ctx, Options options)
    {
        if (options.Marketplace is not { } registry)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "The marketplace is disabled (KnockBox:MarketplaceEnabled=false).");
            return;
        }

        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminSourceRequest);
        if (body is null)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, "Send a marketplace to register.");
            return;
        }

        // An existing row supplies anything the body left out, so toggling Enabled doesn't need the
        // caller to resend both URLs.
        var existing = registry.Sources.FirstOrDefault(
            s => string.Equals(s.Id, body.Id, StringComparison.OrdinalIgnoreCase));
        var source = new RegisteredMarketplace(
            (body.Id ?? existing?.Id ?? "").Trim(),
            (body.Name ?? existing?.Name ?? "").Trim(),
            (body.CatalogUrl ?? existing?.CatalogUrl ?? "").Trim(),
            (body.DownloadBaseUrl ?? existing?.DownloadBaseUrl ?? MarketplaceOptions.Default.DownloadBaseUrl)
                .Trim().TrimEnd('/'),
            body.Enabled ?? existing?.Enabled ?? true);

        if (registry.Validate(source) is { } why)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, why);
            return;
        }

        var warning = options.Settings.UpsertSource(source);
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning,
            Detail: $"Registered '{source.Name}'. Refresh the catalog to see what it offers."));
    }

    private static async Task RemoveSource(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        if (string.Equals(id, MarketplaceSourceRegistry.OfficialId, StringComparison.OrdinalIgnoreCase))
        {
            // 409, not 403: the request is well-formed, the target simply isn't removable. Disabling it
            // achieves what the operator wanted and is reversible.
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "The official marketplace is built in and can't be removed. Disable it instead.");
            return;
        }

        if (!options.Settings.RemoveSource(id, out var warning))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No registered marketplace with id '{id}'.");
            return;
        }

        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning, Detail: "Removed."));
    }

    /// <summary>
    /// Switches a marketplace off without losing its configuration — including the official one, which is
    /// what <see cref="RemoveSource"/> and <c>MarketplaceSourceRegistry.Validate</c> both point an operator
    /// at when they try to remove it.
    /// </summary>
    private static async Task SetSourceEnabled(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminSourceEnabledRequest);
        if (body?.Enabled is not { } enabled)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, "Send { \"enabled\": true | false }.");
            return;
        }

        if (!options.Settings.SetSourceEnabled(id, enabled, out var warning))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No marketplace with id '{id}'.");
            return;
        }

        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning,
            Detail: enabled ? "Enabled." : "Disabled; it offers nothing until you switch it back on."));
    }

    private static async Task SetUpdatePolicy(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminUpdatePolicyRequest);

        if (!Enum.TryParse<UpdatePolicy>(body?.Policy, ignoreCase: true, out var policy))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest,
                "Policy must be one of: manual, auto, drain, force.");
            return;
        }

        // Deliberately NOT gated on the game being installed: enrolling a game whose files are briefly
        // absent — a mount that hasn't come up, a package mid-replace — must not silently fail, and the
        // settings store already keeps overrides for games it can't currently see.
        var canonical = options.Catalog.GameLocations.TryGetValue(id, out var location)
            ? location.Manifest.Id
            : id;
        var warning = options.Settings.SetUpdatePolicy(canonical, policy);

        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning, Detail: policy switch
        {
            UpdatePolicy.Manual => "This game will only be updated when you ask.",
            UpdatePolicy.Auto => "This game will update itself whenever it has no lobbies running.",
            UpdatePolicy.Drain =>
                "This game will stop accepting new lobbies when an update is found, and update once the " +
                "running ones finish.",
            _ => "This game will close its running lobbies and update as soon as one is found.",
        }));
    }

    private static async Task CheckForUpdates(HttpContext ctx, Options options)
    {
        if (options.Updates is not { } coordinator)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "The marketplace is disabled (KnockBox:MarketplaceEnabled=false).");
            return;
        }

        var pass = await coordinator.RunOnceAsync(ctx.RequestAborted);
        if (pass.Error is not null)
        {
            await Refuse(ctx, StatusCodes.Status502BadGateway, pass.Error);
            return;
        }

        await WriteAction(ctx, new AdminActionResponse(true, Affected: pass.Started,
            Detail: pass.Started > 0
                ? $"Started {pass.Started} update(s); follow them in the operations list."
                : "Nothing to do — every game enrolled in automatic updates is already current."));
    }

    private static PackageApplyMode ParseMode(string? mode) => mode?.Trim().ToLowerInvariant() switch
    {
        "auto" => PackageApplyMode.Auto,
        "force" => PackageApplyMode.Force,
        // Drain is the default: it never interrupts a game in progress, and unlike auto it does not
        // silently give up when one happens to be running.
        _ => PackageApplyMode.Drain,
    };

    private static int StatusFor(PackageRefusal refusal) => refusal switch
    {
        PackageRefusal.NotFound => StatusCodes.Status404NotFound,
        PackageRefusal.Invalid => StatusCodes.Status400BadRequest,
        // Busy, NotManaged and Unavailable are all "the request is fine, the state or the deployment
        // says no" — which is what 409 means throughout this API.
        _ => StatusCodes.Status409Conflict,
    };

    private static async Task PurgeStale(HttpContext ctx, Options options)
    {
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminPurgeStaleRequest)
                   ?? new AdminPurgeStaleRequest();
        // A caller-supplied window overrides the configured one, clamped so "0" can't be read as "close
        // every lobby on the server" — that is what the bulk close is for, and it says so.
        var staleAfter = body.IdleMinutes is > 0 and <= 10080
            ? TimeSpan.FromMinutes(body.IdleMinutes.Value)
            : options.StaleAfter;

        var closed = options.Operations.PurgeStale(staleAfter, "This lobby was idle and has been closed.");
        await WriteAction(ctx, new AdminActionResponse(true, Affected: closed,
            Detail: $"Purged lobbies idle for {staleAfter.TotalMinutes:0} minute(s) or with nobody connected."));
    }

    private static async Task CloseLobby(HttpContext ctx, Options options)
    {
        var code = ctx.GetRouteValue("code") as string ?? "";
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminCloseLobbiesRequest)
                   ?? new AdminCloseLobbiesRequest();

        if (!options.Closer.Close(code, Reason(body.Reason, "An administrator closed this lobby.")))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No active lobby with code '{code}'.");
            return;
        }

        options.Logger.LogWarning("Admin closed lobby {LobbyId}.", code);
        await WriteAction(ctx, new AdminActionResponse(true, Affected: 1));
    }

    private static async Task KickPlayer(HttpContext ctx, Options options)
    {
        var code = ctx.GetRouteValue("code") as string ?? "";
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminKickRequest);
        if (string.IsNullOrWhiteSpace(body?.PlayerId))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, "A playerId is required.");
            return;
        }

        var lobby = options.Lobbies.Get(code);
        if (lobby is null)
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No active lobby with code '{code}'.");
            return;
        }

        var playerId = body.PlayerId.Trim();

        // The host kick REFUSES to remove the owner (WebSocketHandler.HandleKickPlayer), because
        // Lobby.Kick drops a member without touching HostId — leaving the id of a non-member holding the
        // lobby powers. Nobody then passes `PlayerId == HostId && Contains(PlayerId)`, so SetLobbyOpen and
        // the in-game kick are dead for everyone, and a to:"host" relay finds no game connection and fans
        // out to nobody: the game freezes until the lobby goes dark. An operator, unlike a host, has no
        // other way to remove that person — so this hands the powers on rather than refusing.
        string? promoted = null;
        if (string.Equals(playerId, lobby.HostId, StringComparison.Ordinal))
        {
            promoted = LobbyOwnership.NextOwner(lobby, options.Connections, playerId);
            if (promoted is null)
            {
                // Nobody to hand the lobby to, and nobody it could still be running for. Close it rather
                // than kick into an empty room — and say so, because "kicked" and "the session ended" are
                // different outcomes to report.
                options.Closer.Close(lobby.Id, "An administrator closed this lobby.");
                options.Logger.LogWarning(
                    "Admin kicked host {PlayerId} from lobby {LobbyId}; nobody else was connected, so the lobby was closed.",
                    playerId, lobby.Id);
                await WriteAction(ctx, new AdminActionResponse(true, Affected: 1,
                    Detail: $"'{playerId}' held lobby '{lobby.Id}' and nobody else was connected, so it was closed."));
                return;
            }

            // Before the kick, so the lobby is never observably ownerless.
            LobbyOwnership.Reassign(lobby, options.Connections, promoted);
        }

        if (!lobby.Kick(playerId))
        {
            // Kick records the bar even for a non-member, so this is "they had already gone" rather than a
            // failure — but the operator should not be told someone was removed who wasn't there.
            await Refuse(ctx, StatusCodes.Status404NotFound,
                $"'{playerId}' is not in lobby '{code}' (they are now barred from rejoining it).");
            return;
        }

        // Tell the player and the rest of the lobby, then cut the sockets — the same sequence the in-game
        // host kick performs, so an admin kick is indistinguishable from a host one to every client.
        options.Connections.SendTo(playerId, new KickedMessage(lobby.Id));
        var left = ConnectionManager.Serialize(new PlayerLeftMessage(lobby.Id, playerId));
        var leftGame = ConnectionManager.Serialize(new GamePlayerLeftMessage(playerId));
        foreach (var member in lobby.Players)
        {
            options.Connections.SendRawTo(member.Id, left);
            options.Connections.SendRawToGame(member.Id, leftGame);
        }
        options.Connections.GetGame(playerId)?.Abort();

        // The roster post the host kick makes (WebSocketHandler's PostAuthorityRoster). Without it the
        // module keeps a player the lobby has dropped: if it was their turn it waits forever on an intent
        // that can no longer arrive, with no error and nothing in the log tying it to the kick.
        if (lobby.IsServerAuthority && options.Authorities?.TryGet(lobby.Id, out var authority) == true)
            authority.PostPlayerLeft(playerId);

        options.Logger.LogWarning("Admin kicked {PlayerId} from lobby {LobbyId}.", playerId, lobby.Id);
        await WriteAction(ctx, new AdminActionResponse(true, Affected: 1,
            Detail: promoted is null ? null : $"'{promoted}' now holds lobby '{lobby.Id}'."));
    }

    private static Task Rescan(HttpContext ctx, Options options)
    {
        // ScheduleRescan, never Discover(): the latter has no mutual exclusion, so a manual rescan racing
        // the watcher could let the older scan win the publish.
        options.Catalog.ScheduleRescan();
        options.Logger.LogInformation("Admin requested a catalog rescan.");
        return WriteAction(ctx, new AdminActionResponse(true,
            Detail: "Rescan scheduled; the catalog updates within a moment."));
    }

    private static async Task SetAvailability(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminAvailabilityRequest);
        if (!Enum.TryParse<GameAvailability>(body?.State, ignoreCase: true, out var state))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest,
                "State must be one of: available, disabled, staged.");
            return;
        }

        if (!options.Catalog.TryGet(id, out var manifest))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No installed game with id '{id}'.");
            return;
        }

        // Existing lobbies deliberately play on (spec §3.1): only listing and creation change. Saying so
        // in the response is what stops an operator assuming Disable ended the sessions.
        var warning = options.Settings.SetAvailability(manifest.Id, state);
        var running = options.Lobbies.Snapshot()
            .Count(l => string.Equals(l.GameId, manifest.Id, StringComparison.OrdinalIgnoreCase));
        var detail = state == GameAvailability.Available || running == 0
            ? null
            : $"{running} lobby/lobbies are still running this game and will continue until they finish.";

        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning, Detail: detail));
    }

    private static async Task DeleteGame(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";

        // The same two refusals Uninstall makes, for the same reason: DeleteGame removes the unpacked
        // directory, the package and the backups, and an install pass is meanwhile moving exactly those
        // things. Losing that race half-deletes a game the installer is mid-way through putting back —
        // the one outcome an all-or-nothing operation exists to prevent. Uninstall reached this check by
        // being a job; a direct delete has to make it itself.
        if (options.Catalog.GameLocations.TryGetValue(id, out var location))
        {
            var gameId = location.Manifest.Id;
            if (options.Packages.Jobs.ActiveFor(gameId) is { } running)
            {
                await Refuse(ctx, StatusCodes.Status409Conflict,
                    $"'{gameId}' has a {Camel(running.Kind.ToString())} in progress ({running.Phase}). " +
                    "Wait for it to finish, or cancel it, then delete.");
                return;
            }

            // Draining/Updating are held by an apply that has passed the point of cancellation, so they
            // are a "not yet", not a "never" — and they are never persisted, so this cannot wedge.
            if (options.Lifecycle.StateOf(gameId) is var state && state != GameLifecycle.Idle)
            {
                await Refuse(ctx, StatusCodes.Status409Conflict,
                    $"'{gameId}' is {Camel(state.ToString())} — an update is in flight. Wait for it to finish, then delete.");
                return;
            }
        }

        var result = options.Operations.DeleteGame(id);
        if (!result.Success)
        {
            // 409 rather than 500: nothing is broken, the deployment simply doesn't permit this (a
            // read-only games mount), and the portal offers Disable instead.
            await Refuse(ctx, result.Blocked is not null ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest,
                result.Error ?? "The game could not be deleted.");
            return;
        }

        await WriteAction(ctx, new AdminActionResponse(true, Affected: result.LobbiesClosed,
            Detail: result.LobbiesClosed > 0
                ? $"Deleted, and closed {result.LobbiesClosed} running lobby/lobbies."
                : "Deleted."));
    }

    private static async Task SetMaintenance(HttpContext ctx, Options options)
    {
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminMaintenanceRequest)
                   ?? new AdminMaintenanceRequest();
        var warning = options.Settings.SetMaintenance(body.Enabled, body.Message);
        // Notified from here rather than from the store: the store is IPlatformPolicy's lock-free backing
        // and is read on the lobby-create path, so it must not own an outbound side-effect.
        options.Webhooks?.Publish(WebhookDispatcher.Payload(
            WebhookEvent.MaintenanceChanged,
            body.Enabled
                ? $"Maintenance mode ON. {body.Message ?? "No message set."}"
                : "Maintenance mode off; new lobbies are allowed again.",
            options.Time.GetUtcNow(),
            title: body.Enabled ? "Maintenance on" : "Maintenance off"));

        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning,
            Detail: body.Enabled
                ? "New lobbies are blocked platform-wide. Running sessions continue until they finish."
                : "New lobbies are allowed again."));
    }

    // ── Metric history ────────────────────────────────────────────────────────

    private static Task MetricHistoryFeed(HttpContext ctx, Options options)
    {
        // Cursor-polled, like the log ring and the job feed: `after` is the last sequence the client holds,
        // so an open dashboard transfers one sample per tick instead of the whole hour every five seconds.
        var after = long.TryParse(ctx.Request.Query["after"], out var parsed) && parsed > 0 ? parsed : 0;
        var samples = options.History.Read(after).Select(s => new AdminMetricSample(
            s.Sequence,
            s.At.UtcDateTime.ToString("O"),
            s.CpuSeconds,
            s.WorkingSetMb,
            s.ManagedHeapMb,
            s.Lobbies,
            s.Players,
            s.GameSockets,
            s.AuthorityLobbies,
            [.. s.Games.Select(g => new AdminMetricGame(
                g.GameId, g.Lobbies, g.FramesOut, g.BytesOut, g.FramesDropped,
                Math.Round(g.AuthorityCpuSeconds, 3)))]));

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminMetricHistoryResponse,
            new AdminMetricHistoryResponse(
                Enabled: options.MetricSampleSeconds > 0,
                Samples: [.. samples],
                LastSequence: options.History.LastSequence,
                Retained: options.History.Count,
                Capacity: options.History.Capacity,
                SampleSeconds: options.MetricSampleSeconds,
                ProcessorCount: Environment.ProcessorCount));
    }

    // ── Platform limits ───────────────────────────────────────────────────────

    private static Task Limits(HttpContext ctx, Options options)
    {
        var provider = options.Limits;
        var overrides = provider.Overrides;
        var startup = provider.Configured;
        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminLimitsResponse, new AdminLimitsResponse(
            Defaults: Values(startup),
            Effective: Values(provider.Current),
            Overridden: OverriddenKeys(overrides),
            // Reported read-only. The portal shows them greyed with the reason rather than omitting them:
            // an operator looking for "handshake timeout" and not finding it assumes the portal is
            // incomplete, where a disabled field with "set in configuration" answers the question.
            HandshakeTimeoutSeconds: startup.HandshakeTimeout.TotalSeconds,
            DisconnectGraceSeconds: startup.DisconnectGrace.TotalSeconds,
            AdminLoginAttemptsPerMinute: startup.AdminLoginAttemptsPerMinute,
            AdminLoginAttemptsPerMinuteGlobal: startup.AdminLoginAttemptsPerMinuteGlobal,
            ActiveLobbies: options.Lobbies.Count,
            ConnectedPlayers: options.Connections.ControlCount));

        static AdminLimitValues Values(ServerLimits limits) => new(
            limits.GameMessagesPerSecond, limits.GameMessagesBurst,
            limits.ControlMessagesPerSecond, limits.ControlMessagesBurst,
            limits.LobbyCreatesPerMinute, limits.MaxConnectionsPerIp,
            limits.MaxLobbies, limits.MaxLobbiesPerGame);

        static IReadOnlyList<string> OverriddenKeys(OperatorLimits o)
        {
            var keys = new List<string>(8);
            if (o.GameMessagesPerSecond is not null) keys.Add("gameMessagesPerSecond");
            if (o.GameMessagesBurst is not null) keys.Add("gameMessagesBurst");
            if (o.ControlMessagesPerSecond is not null) keys.Add("controlMessagesPerSecond");
            if (o.ControlMessagesBurst is not null) keys.Add("controlMessagesBurst");
            if (o.LobbyCreatesPerMinute is not null) keys.Add("lobbyCreatesPerMinute");
            if (o.MaxConnectionsPerIp is not null) keys.Add("maxConnectionsPerIp");
            if (o.MaxLobbies is not null) keys.Add("maxLobbies");
            if (o.MaxLobbiesPerGame is not null) keys.Add("maxLobbiesPerGame");
            return keys;
        }
    }

    private static async Task SetLimits(HttpContext ctx, Options options)
    {
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminLimitsRequest)
                   ?? new AdminLimitsRequest();
        var overrides = new OperatorLimits(
            body.GameMessagesPerSecond, body.GameMessagesBurst,
            body.ControlMessagesPerSecond, body.ControlMessagesBurst,
            body.LobbyCreatesPerMinute, body.MaxConnectionsPerIp,
            body.MaxLobbies, body.MaxLobbiesPerGame);

        // 400, not 409: an out-of-range number or a burst that would refuse every message is a bad
        // request, not a state conflict. The message names the field and the range, matching how the
        // availability and update-policy routes enumerate their legal values.
        if (overrides.Validate(options.Limits.Configured) is { } error)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, error);
            return;
        }

        // Persist first, then publish — both happen regardless of whether the write succeeded, which is
        // the store's contract: a change is in effect even when it can't be saved.
        var warning = options.Settings.SetLimits(overrides);
        options.Limits.Apply(overrides);

        var effective = options.Limits.Current;
        var detail = overrides.IsEmpty
            ? "Every limit is back to its default."
            : Applied(effective, options.Lobbies);
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning, Detail: detail));

        // What the operator most needs told: the change reached sockets that were already open (that is
        // the whole point), but a lowered cap never tears anything down — it refuses the next one.
        static string Applied(ServerLimits effective, LobbyManager lobbies)
        {
            var note = "In force now, including for connections that are already open.";
            if (effective.MaxLobbies > 0 && lobbies.Count > effective.MaxLobbies)
                note += $" {lobbies.Count} lobbies are already running, above the new cap of " +
                        $"{effective.MaxLobbies} — they continue until they finish; no new ones start " +
                        "until the count falls below it.";
            return note;
        }
    }

    // ── Update schedule ───────────────────────────────────────────────────────

    private static Task UpdateScheduleRead(HttpContext ctx, Options options)
    {
        if (options.Scheduler is not { } scheduler)
        {
            return Refuse(ctx, StatusCodes.Status409Conflict,
                "The marketplace is disabled (KnockBox:MarketplaceEnabled=false), so nothing is checked " +
                "on a schedule.");
        }

        var schedule = scheduler.Current;
        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminUpdateScheduleResponse,
            new AdminUpdateScheduleResponse(
                Cadence: Camel(schedule.Cadence.ToString()),
                DayOfWeek: Camel(schedule.DayOfWeek.ToString()),
                HourUtc: schedule.HourUtc,
                Overridden: options.Settings.UpdateSchedule is not null,
                Summary: schedule.Describe(),
                // Round-trip format, not a rendered date: the portal renders it in the reader's own zone,
                // and every other timestamp this API emits does the same.
                NextRunUtc: scheduler.NextRun?.ToUniversalTime().ToString("O"),
                Enrolled: options.Settings.UpdatePolicies.Count));
    }

    private static async Task SetUpdateSchedule(HttpContext ctx, Options options)
    {
        if (options.Scheduler is not { } scheduler)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "The marketplace is disabled (KnockBox:MarketplaceEnabled=false), so there is no schedule " +
                "to set.");
            return;
        }

        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminUpdateScheduleRequest)
                   ?? new AdminUpdateScheduleRequest();

        UpdateSchedule? next = null;
        if (body.Cadence is { Length: > 0 } rawCadence)
        {
            if (!Enum.TryParse<UpdateCadence>(rawCadence, ignoreCase: true, out var cadence)
                || !Enum.IsDefined(cadence))
            {
                await Refuse(ctx, StatusCodes.Status400BadRequest,
                    $"'{rawCadence}' is not a cadence. Use off, hourly, daily or weekly.");
                return;
            }

            var day = UpdateSchedule.Default.DayOfWeek;
            if (body.DayOfWeek is { Length: > 0 } rawDay
                && (!Enum.TryParse(rawDay, ignoreCase: true, out day) || !Enum.IsDefined(day)))
            {
                await Refuse(ctx, StatusCodes.Status400BadRequest,
                    $"'{rawDay}' is not a day of the week.");
                return;
            }

            // Rejected rather than clamped: an operator who typed 25 meant something, and silently
            // running at 03:00 instead would be a schedule they never chose and would not notice.
            var hour = body.HourUtc ?? UpdateSchedule.Default.HourUtc;
            if (hour is < 0 or > 23)
            {
                await Refuse(ctx, StatusCodes.Status400BadRequest,
                    $"hourUtc must be between 0 and 23 (got {hour}).");
                return;
            }

            next = new UpdateSchedule(cadence, day, hour);
        }

        var warning = options.Settings.SetUpdateSchedule(next);
        // Re-armed here rather than inside the store: that class records policy and knows nothing about
        // timers, the same split SetLimits and LimitsProvider keep.
        scheduler.Reschedule();

        var enrolled = options.Settings.UpdatePolicies.Count;
        var detail = $"Update checks run {scheduler.Current.Describe()}.";
        if (scheduler.NextRun is { } due)
            detail += $" Next check {due.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.";
        if (enrolled == 0)
        {
            // Worth saying out loud: a schedule with nothing enrolled makes no request and installs
            // nothing, so an operator who set one and saw no activity would reasonably think it was broken.
            detail += " No game is enrolled in automatic updates yet, so a check currently does nothing —" +
                      " set a game's update policy on the Marketplace tab.";
        }
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning, Detail: detail));
    }

    // ── Room codes ────────────────────────────────────────────────────────────

    /// <summary>
    /// The largest share of the code space a blocklist may remove. Well past any real word list, and short
    /// of the point where the generator starts failing to find a free code — which would surface to players
    /// as "could not create a lobby", with nothing to connect it to the blocklist that caused it.
    /// </summary>
    private const int MaxBlockedPercent = 50;

    private static Task RoomCodes(HttpContext ctx, Options options) =>
        WriteRoomCodes(ctx, options.Settings.RoomCodes);

    private static async Task SetRoomCodes(HttpContext ctx, Options options)
    {
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminRoomCodesRequest)
                   ?? new AdminRoomCodesRequest();

        // Report a bad entry rather than dropping it: on the way IN, silence would leave an operator sure
        // they had blocked something. (Compile still drops junk when LOADING a hand-edited file, where
        // there is nobody to tell and the alternative is losing the rest of the list.)
        foreach (var word in body.Words ?? [])
            if (RoomCodeFilter.ValidateEntry(word, pattern: false) is { } wordError)
            {
                await Refuse(ctx, StatusCodes.Status400BadRequest, $"'{word}': {wordError}");
                return;
            }
        foreach (var pattern in body.Patterns ?? [])
            if (RoomCodeFilter.ValidateEntry(pattern, pattern: true) is { } patternError)
            {
                await Refuse(ctx, StatusCodes.Status400BadRequest, $"'{pattern}': {patternError}");
                return;
            }

        // `dropped`, not filter.Count: Compile trims to the cap itself, so the compiled filter can never
        // exceed it and a `filter.Count > MaxEntries` test could never fire — an over-cap save answered
        // 200 and silently discarded the overflow. Asking Compile what it had to throw away is the only
        // way to tell, and counting the submitted lists instead would refuse a list that was only over
        // the cap before de-duplication.
        var filter = RoomCodeFilter.Compile(body.Words, body.Patterns, out var dropped);
        if (dropped > 0)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest,
                $"At most {RoomCodeFilter.MaxEntries} entries, and that list has {dropped} too many. " +
                "One pattern usually replaces many words.");
            return;
        }

        var blocked = filter.CountBlocked();
        var space = RoomCodeFilter.CodeSpaceSize();
        if (blocked * 100L > space * (long)MaxBlockedPercent)
        {
            // 409: the request is well-formed, the consequence is what's refused — the same reading of 409
            // this API uses everywhere else.
            await Refuse(ctx, StatusCodes.Status409Conflict,
                $"That blocklist removes {blocked:N0} of {space:N0} possible codes " +
                $"({blocked * 100.0 / space:F0}%), over the {MaxBlockedPercent}% limit. Lobby creation " +
                "would start failing for reasons no player could act on.");
            return;
        }

        var warning = options.Settings.SetRoomCodes(filter);
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning,
            Detail: filter.IsEmpty
                ? "No codes are blocked."
                : $"Blocking {blocked:N0} of {space:N0} possible codes ({blocked * 100.0 / space:F1}%). " +
                  "Codes already in use are unaffected."));
    }

    private static Task WriteRoomCodes(HttpContext ctx, RoomCodeFilter filter)
    {
        var unreachable = new List<string>();
        foreach (var entry in filter.Words.Concat(filter.Patterns))
            if (RoomCodeFilter.IsUnreachable(entry)) unreachable.Add(entry);

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminRoomCodesResponse, new AdminRoomCodesResponse(
            Words: filter.Words,
            Patterns: filter.Patterns,
            Unreachable: unreachable,
            Blocked: filter.CountBlocked(),
            CodeSpace: RoomCodeFilter.CodeSpaceSize(),
            MaxEntries: RoomCodeFilter.MaxEntries,
            MaxBlockedPercent: MaxBlockedPercent,
            Alphabet: LobbyManager.CodeAlphabet,
            CodeLength: LobbyManager.CodeLength));
    }

    // ── Announcements ─────────────────────────────────────────────────────────

    /// <summary>
    /// Cap on announcement text, matching the maintenance message's and the <see cref="Reason"/> helper's.
    /// A banner is one line above the game grid; anything longer is a page, and belongs somewhere a player
    /// can choose to read it.
    /// </summary>
    private const int MaxAnnouncementLength = 200;

    private static Task Announcement(HttpContext ctx, Options options)
    {
        var live = options.Settings.Announcement;
        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminAnnouncementResponse,
            new AdminAnnouncementResponse(
                Id: live?.Id,
                Text: live?.Text,
                Severity: live?.Severity,
                GameId: live?.GameId,
                PostedAt: live?.PostedAt.UtcDateTime.ToString("O"),
                // How many players would see it — the number that tells an operator whether posting now is
                // worth anything, and the same number the POST reports having reached.
                ConnectedPlayers: options.Connections.ControlCount,
                MaxLength: MaxAnnouncementLength,
                Games: [.. options.Catalog.Games.Select(g => new AdminGameName(g.Id, g.Name))]));
    }

    private static async Task PostAnnouncement(HttpContext ctx, Options options)
    {
        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminAnnouncementRequest)
                   ?? new AdminAnnouncementRequest();
        var text = body.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest,
                "Text is required. To take the banner down, use Clear.");
            return;
        }

        var gameId = string.IsNullOrWhiteSpace(body.GameId) ? null : body.GameId.Trim();
        if (gameId is not null)
        {
            // Resolved through the catalog so the stored id has the manifest's casing — an override keyed
            // differently than the manifest is the bug the availability store's comparer exists to avoid.
            if (!options.Catalog.TryGet(gameId, out var scoped))
            {
                await Refuse(ctx, StatusCodes.Status404NotFound, $"No installed game with id '{gameId}'.");
                return;
            }
            gameId = scoped.Id;
        }

        var announcement = new PlatformAnnouncement(
            // A NEW id every time, including an edit: dismissal is remembered against it, so reusing one
            // would leave an edited notice invisible to everyone who dismissed the previous version.
            Id: Guid.NewGuid().ToString("N"),
            Text: text.Length > MaxAnnouncementLength ? text[..MaxAnnouncementLength] : text,
            PostedAt: options.Time.GetUtcNow(),
            Severity: AdminSettingsStore.AnnouncementSeverity(body.Severity),
            GameId: gameId);

        var warning = options.Settings.SetAnnouncement(announcement);
        var reached = options.Connections.BroadcastToAllControl(new AnnouncementPostedMessage(
            announcement.Id, announcement.Text, announcement.PostedAt,
            announcement.Severity, announcement.GameId));

        await WriteAction(ctx, new AdminActionResponse(true, Affected: reached, Warning: warning,
            Detail: reached == 0
                // Not a failure worth an error: nobody is on the site right now, and the whole point of
                // pushing it on connect is that the next arrival still sees it.
                ? "Posted. Nobody is connected right now; anyone who arrives will see it."
                : $"Posted to {reached} connected player(s). Anyone arriving later sees it too."));
    }

    private static async Task ClearAnnouncement(HttpContext ctx, Options options)
    {
        if (options.Settings.Announcement is not { } live)
        {
            await WriteAction(ctx, new AdminActionResponse(true, Detail: "There was no announcement posted."));
            return;
        }

        var warning = options.Settings.SetAnnouncement(null);
        // The id goes with it so a shell already showing a NEWER announcement doesn't clear the wrong one.
        var reached = options.Connections.BroadcastToAllControl(new AnnouncementClearedMessage(live.Id));
        await WriteAction(ctx, new AdminActionResponse(true, Affected: reached, Warning: warning,
            Detail: $"Cleared for {reached} connected player(s)."));
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    private static Task Webhooks(HttpContext ctx, Options options)
    {
        var dispatcher = options.Webhooks;
        var endpoints = options.Settings.Webhooks.Select(e =>
        {
            var last = dispatcher?.LastResult(e.Id);
            return new AdminWebhookSummary(
                e.Id, e.Name, e.Url,
                Events: [.. (e.Events ?? []).Select(v => Camel(v.ToString()))],
                Enabled: e.Enabled,
                LastAt: last?.At.UtcDateTime.ToString("O"),
                LastOk: last?.Ok,
                LastStatus: last?.Status,
                LastError: last?.Error,
                LastEvent: last is null ? null : Camel(last.Event.ToString()));
        });

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminWebhooksResponse, new AdminWebhooksResponse(
            Enabled: options.WebhookOptions.Enabled,
            Endpoints: [.. endpoints],
            KnownEvents: [.. Enum.GetValues<WebhookEvent>().Select(v => Camel(v.ToString()))],
            MaxEndpoints: options.WebhookOptions.MaxEndpoints,
            Delivered: dispatcher?.Delivered ?? 0,
            Failed: dispatcher?.Failed ?? 0,
            Dropped: dispatcher?.Dropped ?? 0,
            Suppressed: options.WebhookLog?.Suppressed ?? 0,
            TimeoutSeconds: (int)options.WebhookOptions.Timeout.TotalSeconds,
            ErrorsPerMinute: options.WebhookOptions.ErrorsPerMinute));
    }

    private static async Task AddWebhook(HttpContext ctx, Options options)
    {
        if (!options.WebhookOptions.Enabled)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "Webhooks are switched off (KnockBox:WebhooksEnabled=false).");
            return;
        }

        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.AdminWebhookRequest);
        if (body is null)
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, "Send an id, a name and a URL.");
            return;
        }

        var existing = options.Settings.Webhooks
            .FirstOrDefault(w => string.Equals(w.Id, body.Id, StringComparison.OrdinalIgnoreCase));

        if (!MarketplaceSourceRegistry.IsValidId(body.Id))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest,
                "Id must be 1-32 characters of letters, digits, dash or underscore.");
            return;
        }

        var url = body.Url ?? existing?.Url;
        if (!WebhookDispatcher.IsAllowedUrl(url))
        {
            // The downloader's own rule, exposed rather than copied — so a URL that registers can never be
            // one the sender would then refuse. The loopback half of it is only reachable once the
            // address guard is lifted, and saying so here is what stops the two rules contradicting each
            // other: this message inviting a loopback URL that the next check then refuses.
            await Refuse(ctx, StatusCodes.Status400BadRequest,
                options.WebhookOptions.AllowPrivateTargets
                    ? "The URL must be https, or http on loopback (for a local monitoring agent)."
                    : "The URL must be https. An http loopback endpoint (a local monitoring agent) also "
                      + $"needs {PrivateAddressGuard.Knob}=true.");
            return;
        }

        // A literal address can be judged now, which is worth doing purely so the operator learns the rule
        // while they are typing the URL rather than from a delivery failure later. It is NOT the boundary:
        // a hostname is only resolved at connect time, and that is where the guard actually runs — see
        // MarketplaceClient's connect callback. So this rejects the obvious case and lets everything else
        // through to be judged against the address finally dialled.
        if (!options.WebhookOptions.AllowPrivateTargets
            && Uri.TryCreate(url, UriKind.Absolute, out var target)
            && IPAddress.TryParse(target.Host.Trim('[', ']'), out var literal)
            && PrivateAddressGuard.IsBlocked(literal))
        {
            await Refuse(ctx, StatusCodes.Status400BadRequest, PrivateAddressGuard.Refusal(target.Host));
            return;
        }

        var events = new List<WebhookEvent>();
        foreach (var name in body.Events ?? [])
        {
            if (!Enum.TryParse<WebhookEvent>(name, ignoreCase: true, out var parsed))
            {
                await Refuse(ctx, StatusCodes.Status400BadRequest,
                    $"'{name}' is not an event. Valid: " +
                    string.Join(", ", Enum.GetValues<WebhookEvent>().Select(v => Camel(v.ToString()))) + ".");
                return;
            }
            if (!events.Contains(parsed)) events.Add(parsed);
        }

        if (existing is null && options.Settings.Webhooks.Count >= options.WebhookOptions.MaxEndpoints)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                $"At most {options.WebhookOptions.MaxEndpoints} endpoints (KnockBox:MaxWebhooks).");
            return;
        }

        var endpoint = new WebhookEndpoint(
            Id: body.Id!,
            Name: string.IsNullOrWhiteSpace(body.Name) ? existing?.Name ?? body.Id! : body.Name.Trim(),
            Url: url!,
            Events: events,
            Enabled: body.Enabled ?? existing?.Enabled ?? true);

        var warning = options.Settings.UpsertWebhook(endpoint);
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning,
            Detail: events.Count == 0
                // Stated because it is the opposite of what an empty multi-select looks like it means.
                ? "Saved. With no events selected it receives all of them."
                : $"Saved, subscribed to {events.Count} event(s)."));
    }

    private static async Task RemoveWebhook(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        if (!options.Settings.RemoveWebhook(id, out var warning))
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No webhook with id '{id}'.");
            return;
        }
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning, Detail: "Removed."));
    }

    private static async Task TestWebhook(HttpContext ctx, Options options)
    {
        var id = ctx.GetRouteValue("id") as string ?? "";
        if (options.Webhooks is not { } dispatcher)
        {
            await Refuse(ctx, StatusCodes.Status409Conflict,
                "Webhooks are switched off (KnockBox:WebhooksEnabled=false).");
            return;
        }

        var endpoint = options.Settings.Webhooks
            .FirstOrDefault(w => string.Equals(w.Id, id, StringComparison.OrdinalIgnoreCase));
        if (endpoint is null)
        {
            await Refuse(ctx, StatusCodes.Status404NotFound, $"No webhook with id '{id}'.");
            return;
        }

        // Sent through the real delivery path, and AWAITED rather than queued: the operator clicked this to
        // find out whether the URL works, so the answer belongs in the response instead of appearing in a
        // panel a poll later. A disabled endpoint is still tested — testing is how you check one before
        // enabling it.
        var result = await dispatcher.DeliverAsync(endpoint, WebhookDispatcher.Payload(
            WebhookEvent.MaintenanceChanged,
            "Test notification from the KnockBox admin portal.",
            options.Time.GetUtcNow(),
            title: "Test"), ctx.RequestAborted);

        if (!result.Ok)
        {
            // 502: the request here was fine, the upstream endpoint is what failed — the same reading the
            // marketplace routes give an unreachable catalog host.
            await Refuse(ctx, StatusCodes.Status502BadGateway,
                $"Delivery failed ({result.Status?.ToString() ?? "no response"}): {result.Error}");
            return;
        }

        await WriteAction(ctx, new AdminActionResponse(true,
            Detail: $"Delivered ({result.Status})." + (endpoint.Enabled ? "" : " This endpoint is disabled, so it receives nothing until you enable it.")));
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>Wraps a handler so it only runs for a request carrying a valid session cookie.</summary>
    private static RequestDelegate RequireSession(Options options, RequestDelegate handler) => ctx =>
        ctx.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var session)
        && options.Auth.ValidateSessionToken(session)
            ? handler(ctx)
            : Refuse(ctx, StatusCodes.Status401Unauthorized, "Unauthorized.");

    /// <summary>What body a mutation route accepts. Most take optional JSON.</summary>
    internal enum MediaKind
    {
        /// <summary>No body, or <c>application/json</c>.</summary>
        Json,

        /// <summary>A body is mandatory and must be <c>application/json</c> — the auth routes.</summary>
        JsonRequired,

        /// <summary>Raw <c>.kbg</c> bytes — the package upload route, and only that one.</summary>
        Package,
    }

    /// <summary>
    /// Wraps a mutating handler so it only runs for a request that plausibly came from the portal itself.
    /// </summary>
    /// <remarks>
    /// <para>The session cookie is <c>SameSite=Strict</c>, which already means it never rides a cross-site
    /// request, and the origin is not meant to be publicly reachable at all — so this is defence in depth
    /// rather than the primary control. What it adds is cheap: a JSON content type (so a plain HTML form
    /// post, the one shape <c>SameSite</c> historically leaked on, can't reach these), and a rejection of
    /// any <c>Sec-Fetch-Site</c> that says cross-site. Requests without the header (curl, the CI smoke
    /// test) are allowed through, because a header a client may simply omit cannot be a security boundary
    /// and pretending otherwise would only break operator tooling.</para>
    /// <para>Which makes the CONTENT TYPE the load-bearing half, and it is checked against a body that
    /// might exist rather than one that declares its length: <c>Transfer-Encoding: chunked</c> carries no
    /// <c>Content-Length</c>, and reading the absent length as "no body" waved a cross-site form post
    /// straight through on that technicality. The three auth routes take <see cref="MediaKind.JsonRequired"/>
    /// because they have no no-arguments case at all.</para>
    /// </remarks>
    private static RequestDelegate WriteGuard(RequestDelegate handler, MediaKind media = MediaKind.Json) => ctx =>
    {
        var refusal = WriteGuardRefusal(media, ctx.Request.ContentType, ctx.Request.ContentLength,
                                        ctx.Request.Headers["Sec-Fetch-Site"].ToString());
        return refusal is { } r ? Refuse(ctx, r.Status, r.Error) : handler(ctx);
    };

    /// <summary>
    /// The guard's decision, as a pure function of the three request facts it reads: null to run the
    /// handler, else the status and message to refuse with.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="WriteGuard"/> and kept free of <c>HttpContext</c> for the reason
    /// <see cref="OriginRouting"/> is: it is a security decision, and one that has already been wrong in a
    /// way nothing but a real request would have shown. Composing the whole route table to exercise it
    /// needs thirty-odd dependencies, so the rule would otherwise be pinned only by the Docker job — the
    /// slowest and last thing to run.
    /// </remarks>
    internal static (int Status, string Error)? WriteGuardRefusal(
        MediaKind media, string? contentType, long? contentLength, string? secFetchSite)
    {
        // A header a client may simply omit cannot be a security boundary, and pretending otherwise would
        // only break operator tooling (curl, the CI smoke test) — so an ABSENT value passes and a value
        // that says cross-site does not.
        if (!string.IsNullOrEmpty(secFetchSite)
            && !secFetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase))
        {
            return (StatusCodes.Status403Forbidden, "Cross-site admin requests are refused.");
        }

        return media switch
        {
            // An empty body is fine — several of these actions take no arguments — but a body that IS
            // present must be JSON. A body-less POST reports Content-Length: 0, so a NULL length here
            // means chunked, i.e. a body whose size simply wasn't declared: it must clear the same bar.
            MediaKind.Json when contentLength is null or > 0 && !Has(contentType, "application/json")
                => (StatusCodes.Status415UnsupportedMediaType, "Send application/json."),

            // The auth routes: the body is not optional, so the type is required outright — the same
            // reasoning as Package below, and for a sharper reason (see the routes' own comment).
            MediaKind.JsonRequired when !Has(contentType, "application/json")
                => (StatusCodes.Status415UnsupportedMediaType, "Send application/json."),

            // An upload ALWAYS has a body, so the type is required outright rather than only when
            // ContentLength says so — a chunked request has no ContentLength at all, and the JSON rule
            // above would wave it straight through on that technicality.
            MediaKind.Package when !Has(contentType, "application/octet-stream")
                => (StatusCodes.Status415UnsupportedMediaType,
                    "Send the .kbg bytes as application/octet-stream."),

            _ => null,
        };

        static bool Has(string? contentType, string expected) =>
            contentType is not null && contentType.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Deserializes a request body, or null when it is absent, empty or unparseable. Every request record
    /// has all-defaulted members, so callers substitute a default instance and treat "no body" as "no
    /// arguments" rather than as an error.
    /// </summary>
    private static async Task<T?> ReadJson<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo) where T : class
    {
        // `is 0`, NOT `is null or 0`. A chunked request declares no length, and treating that as "no body"
        // discarded one silently — every handler then substituted its all-defaulted record, which for
        // CloseLobbies means closing EVERY lobby on the server and reporting success. An empty stream still
        // throws JsonException below and still yields null, so "no body ⇒ no arguments" is unchanged.
        if (ctx.Request.ContentLength is 0) return null;
        try { return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted); }
        catch (JsonException) { return null; }
    }

    private static Task WriteAction(HttpContext ctx, AdminActionResponse response) =>
        WriteJson(ctx, KnockBoxProtocolContext.Default.AdminActionResponse, response);

    // Operator-supplied close reasons are shown to players by the shell, so cap the length and fall back
    // to a sensible sentence rather than sending an empty one.
    private static string Reason(string? supplied, string fallback)
    {
        var trimmed = supplied?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return fallback;
        return trimmed.Length <= 200 ? trimmed : trimmed[..200];
    }

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

    /// <summary>
    /// Builds an <c>attachment</c> Content-Disposition for a download.
    /// </summary>
    /// <remarks>
    /// Through <see cref="ContentDispositionHeaderValue"/> rather than string interpolation, which is what
    /// both download routes did: a game id is the folder name on disk, and on Linux that may contain a
    /// double quote, which truncated the header at the quote. SetHttpFileName emits both the quoted ASCII
    /// <c>filename</c> and the encoded <c>filename*</c>.
    /// </remarks>
    private static string Attachment(string fileName)
    {
        var disposition = new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        return disposition.ToString();
    }

    private static Task Refuse(HttpContext ctx, int status, string error) =>
        WriteJson(ctx, KnockBoxProtocolContext.Default.AdminApiResponse, new AdminApiResponse(false, error), status);

    private static Task WriteJson<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo, T value, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, value, typeInfo);
    }
}
