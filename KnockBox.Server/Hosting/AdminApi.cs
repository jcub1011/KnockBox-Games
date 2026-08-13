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
        AdminLogBuffer Logs,
        DiskUsageReporter Disk,
        RelayMetrics Relay,
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
            routes.MapPost("/admin/api/auth/setup", ctx => Setup(ctx, options));
            routes.MapPost("/admin/api/auth/login", ctx => Login(ctx, options));
            routes.MapPost("/admin/api/auth/logout", ctx => Logout(ctx, options));

            // ── Reads ──
            routes.MapGet("/admin/api/system/status",
                RequireSession(options, ctx => SystemStatus(ctx, options, processStartedUtc)));
            routes.MapGet("/admin/api/metrics", RequireSession(options, ctx => Metrics(ctx, options)));
            routes.MapGet("/admin/api/lobbies", RequireSession(options, ctx => Lobbies(ctx, options)));
            routes.MapGet("/admin/api/games", RequireSession(options, ctx => Games(ctx, options)));
            routes.MapGet("/admin/api/logs", RequireSession(options, ctx => Logs(ctx, options)));
            routes.MapGet("/admin/api/logs/files", RequireSession(options, ctx => LogFiles(ctx, options)));
            routes.MapGet("/admin/api/logs/files/{name}",
                RequireSession(options, ctx => DownloadLogFile(ctx, options)));
            routes.MapGet("/admin/api/packages/jobs", RequireSession(options, ctx => Jobs(ctx, options)));
            routes.MapGet("/admin/api/packages/jobs/{jobId}", RequireSession(options, ctx => Job(ctx, options)));
            routes.MapGet("/admin/api/marketplace/catalog", RequireSession(options, ctx => Catalog(ctx, options)));

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
            games.Add(new AdminGameRelayMetrics(
                relay.GameId, relay.FramesIn, relay.FramesOut, relay.BytesIn, relay.BytesOut,
                relay.FramesDropped, Math.Round(relay.FanOut, 2),
                counts.Lobbies, counts.Players,
                sockets.Frames, sockets.Bytes, sockets.Dropped));
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
                options.Packages.Jobs.ActiveFor(manifest.Id)?.JobId));
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
            disk.ManagedRootBytes));
    }

    // A cheap, read-only guess at whether the files could be removed: it checks the directories that
    // would have to be written to. AdminOperations.DeleteGame probes them for real before touching
    // anything, so this only decides whether the portal offers the button.
    private static string? DeleteBlockedReason(
        Options options, string directory, GamePackageLocations.PackageLocation? package)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        if (parent is not null && !DirectoryWritable(parent))
            return $"'{parent}' is not writable by the server (in production the games folder is mounted read-only).";
        // The package's OWN root, not always games/: a portal-installed package sits in the managed root,
        // which is writable by design — which is what makes deleting those games work in production.
        if (package is { } source && Path.GetDirectoryName(source.Path) is { } packageDir
            && !DirectoryWritable(packageDir))
            return $"the source package in '{packageDir}' can't be removed, so the game would reinstall itself.";
        return null;
    }

    private static bool DirectoryWritable(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        var probe = Path.Combine(directory, $".kb-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
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
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileName(resolved)}\"";
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
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = options.PackageLimits.MaxBytes + 4096;

        // Rejected in one round trip when the client is honest about the size. The real enforcement is
        // still the byte count while streaming, because Content-Length is the client's claim.
        if (ctx.Request.ContentLength > options.PackageLimits.MaxBytes)
        {
            await Refuse(ctx, StatusCodes.Status413PayloadTooLarge,
                $"The package exceeds the {options.PackageLimits.MaxBytes:N0}-byte limit " +
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
            if (source.Catalog?.Plugins?.FirstOrDefault(
                    p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) is not { } match) continue;
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

        var start = options.Packages.StartMarketplaceInstall(client, plugin, ParseMode(body.Mode));
        if (!start.Started)
        {
            await Refuse(ctx, StatusFor(start.Refusal), start.Error ?? "The install could not be started.");
            return;
        }

        options.Logger.LogInformation("Admin started a marketplace install of '{GameId}' {Version}.",
            start.Job!.GameId, plugin.Version ?? "(no version)");
        await WriteJson(ctx, KnockBoxProtocolContext.Default.AdminJobResponse,
            new AdminJobResponse(true, JobId: start.Job.JobId, Detail: start.Job.Phase),
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

        options.Logger.LogWarning("Admin kicked {PlayerId} from lobby {LobbyId}.", playerId, lobby.Id);
        await WriteAction(ctx, new AdminActionResponse(true, Affected: 1));
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
        await WriteAction(ctx, new AdminActionResponse(true, Warning: warning,
            Detail: body.Enabled
                ? "New lobbies are blocked platform-wide. Running sessions continue until they finish."
                : "New lobbies are allowed again."));
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>Wraps a handler so it only runs for a request carrying a valid session cookie.</summary>
    private static RequestDelegate RequireSession(Options options, RequestDelegate handler) => ctx =>
        ctx.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var session)
        && options.Auth.ValidateSessionToken(session)
            ? handler(ctx)
            : Refuse(ctx, StatusCodes.Status401Unauthorized, "Unauthorized.");

    /// <summary>
    /// Wraps a mutating handler so it only runs for a request that plausibly came from the portal itself.
    /// </summary>
    /// <remarks>
    /// The session cookie is <c>SameSite=Strict</c>, which already means it never rides a cross-site
    /// request, and the origin is not meant to be publicly reachable at all — so this is defence in depth
    /// rather than the primary control. What it adds is cheap: a JSON content type (so a plain HTML form
    /// post, the one shape <c>SameSite</c> historically leaked on, can't reach these), and a rejection of
    /// any <c>Sec-Fetch-Site</c> that says cross-site. Requests without the header (curl, the CI smoke
    /// test) are allowed through, because a header a client may simply omit cannot be a security boundary
    /// and pretending otherwise would only break operator tooling.
    /// </remarks>
    /// <summary>What body a mutation route accepts. All but one take JSON.</summary>
    private enum MediaKind
    {
        /// <summary>No body, or <c>application/json</c>.</summary>
        Json,

        /// <summary>Raw <c>.kbg</c> bytes — the package upload route, and only that one.</summary>
        Package,
    }

    private static RequestDelegate WriteGuard(RequestDelegate handler, MediaKind media = MediaKind.Json) => ctx =>
    {
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && !site.Equals("same-origin", StringComparison.OrdinalIgnoreCase))
            return Refuse(ctx, StatusCodes.Status403Forbidden, "Cross-site admin requests are refused.");

        var contentType = ctx.Request.ContentType;
        return media switch
        {
            // An empty body is fine — several of these actions take no arguments — but a body that IS
            // present must be JSON.
            MediaKind.Json when ctx.Request.ContentLength is > 0 && !Has(contentType, "application/json")
                => Refuse(ctx, StatusCodes.Status415UnsupportedMediaType, "Send application/json."),

            // An upload ALWAYS has a body, so the type is required outright rather than only when
            // ContentLength says so — a chunked request has no ContentLength at all, and the JSON rule
            // above would wave it straight through on that technicality.
            MediaKind.Package when !Has(contentType, "application/octet-stream")
                => Refuse(ctx, StatusCodes.Status415UnsupportedMediaType,
                    "Send the .kbg bytes as application/octet-stream."),

            _ => handler(ctx),
        };

        static bool Has(string? contentType, string expected) =>
            contentType is not null && contentType.Contains(expected, StringComparison.OrdinalIgnoreCase);
    };

    /// <summary>
    /// Deserializes a request body, or null when it is absent, empty or unparseable. Every request record
    /// has all-defaulted members, so callers substitute a default instance and treat "no body" as "no
    /// arguments" rather than as an error.
    /// </summary>
    private static async Task<T?> ReadJson<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo) where T : class
    {
        if (ctx.Request.ContentLength is null or 0) return null;
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

    private static Task Refuse(HttpContext ctx, int status, string error) =>
        WriteJson(ctx, KnockBoxProtocolContext.Default.AdminApiResponse, new AdminApiResponse(false, error), status);

    private static Task WriteJson<T>(HttpContext ctx, JsonTypeInfo<T> typeInfo, T value, int status = StatusCodes.Status200OK)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, value, typeInfo);
    }
}
