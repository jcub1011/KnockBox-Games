using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KnockBox.Contracts;
using KnockBox.Server.Admin;
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
        AdminOperations Operations,
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

            var packageBacked = File.Exists(Path.Combine(options.Paths.GamesRoot, manifest.Id + GamePackage.Extension));
            var underPackages = location.Directory.StartsWith(options.Paths.GamesUnpackedRoot, StringComparison.OrdinalIgnoreCase);
            // Whether Delete could work is answered here so the portal can disable the button and say why,
            // rather than offering an action that always fails on a read-only games mount.
            var blocked = DeleteBlockedReason(options, location.Directory, packageBacked);

            games.Add(new AdminGameSummary(
                manifest.Id,
                manifest.Name,
                manifest.Version,
                options.Settings.GetAvailability(manifest.Id).ToString().ToLowerInvariant(),
                manifest.MaxPlayers,
                manifest.ServerAuthority is not null,
                location.Directory,
                underPackages ? "packages" : "games",
                packageBacked,
                usage?.TotalBytes ?? 0,
                usage?.DirectoryBytes ?? 0,
                usage?.CompressedBytes ?? 0,
                usage?.PackageBytes ?? 0,
                counts.Lobbies,
                counts.Players,
                blocked is null,
                blocked));
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return WriteJson(ctx, KnockBoxProtocolContext.Default.AdminGamesResponse, new AdminGamesResponse(
            games,
            options.Paths.GamesRoot,
            options.Paths.GamesUnpackedRoot,
            options.Catalog.ScanError,
            disk.TakenAt.UtcDateTime.ToString("O"),
            disk.CompressedCacheBytes,
            disk.LogsBytes));
    }

    // A cheap, read-only guess at whether the files could be removed: it checks the directories that
    // would have to be written to. AdminOperations.DeleteGame probes them for real before touching
    // anything, so this only decides whether the portal offers the button.
    private static string? DeleteBlockedReason(Options options, string directory, bool packageBacked)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(directory));
        if (parent is not null && !DirectoryWritable(parent))
            return $"'{parent}' is not writable by the server (in production the games folder is mounted read-only).";
        if (packageBacked && !DirectoryWritable(options.Paths.GamesRoot))
            return $"the source package in '{options.Paths.GamesRoot}' can't be removed, so the game would reinstall itself.";
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
    private static RequestDelegate WriteGuard(RequestDelegate handler) => ctx =>
    {
        var site = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0 && !site.Equals("same-origin", StringComparison.OrdinalIgnoreCase))
            return Refuse(ctx, StatusCodes.Status403Forbidden, "Cross-site admin requests are refused.");

        var contentType = ctx.Request.ContentType;
        // An empty body is fine — several of these actions take no arguments — but a body that IS present
        // must be JSON.
        if (ctx.Request.ContentLength is > 0
            && (contentType is null || !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)))
            return Refuse(ctx, StatusCodes.Status415UnsupportedMediaType, "Send application/json.");

        return handler(ctx);
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
