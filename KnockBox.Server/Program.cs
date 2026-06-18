using System.IO.Compression;
using System.Net.WebSockets;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Where web/, games/, and logs/ live: explicit config wins, else repo discovery (dev), else the
// app base directory (published exe / container — publish bakes web/ in, games/ sits alongside or
// is volume-mounted). See ContentPaths for the precedence rules.
var (webRoot, gamesRoot, logsRoot, gamesCompressedRoot) = ContentPaths.Resolve(
    builder.Configuration["KnockBox:WebRoot"],
    builder.Configuration["KnockBox:GamesRoot"],
    builder.Configuration["KnockBox:LogsRoot"],
    builder.Configuration["KnockBox:GamesCompressedRoot"],
    builder.Environment.ContentRootPath,
    AppContext.BaseDirectory);

// Pre-compress game assets once into gamesCompressedRoot and serve those variants via Accept-Encoding
// negotiation, instead of re-compressing every full-body response on the fly (see the ResponseCompression
// note below). Master switch — off ⇒ exactly the on-the-fly behavior. The other Precompress* knobs and the
// cache root are read by the precompressor/serving setup further down.
var precompressEnabled = builder.Configuration.GetValue("KnockBox:Precompress", true);
var precompressGzip = builder.Configuration.GetValue("KnockBox:PrecompressGzip", true);
var precompressMinBytes = builder.Configuration.GetValue("KnockBox:PrecompressMinBytes", 1024);
// Periodic reconcile interval. The Discovered event already covers manifest add/remove/edit; this is
// the schedule that also catches asset-only edits under Docker bind-mount polling (the poll only
// fingerprints GAME.json) and is a general safety net. 0 = off (rely on the Discovered event).
var precompressReconcileSeconds = builder.Configuration.GetValue("KnockBox:PrecompressReconcileSeconds", 60);

// Best-effort: a read-only games mount (recommended in Docker) or a root-owned parent must not crash
// startup. GameCatalog and the static-file setup below tolerate a directory that is missing OR exists
// but is unreadable; any problem found here (and a live games-access probe) is collected in
// DeploymentDiagnostics and surfaced on the shell home page so a misconfigured deployment is loud.
var diagnostics = new DeploymentDiagnostics();
var bootstrapDirs = precompressEnabled
    ? new[] { webRoot, gamesRoot, logsRoot, gamesCompressedRoot }
    : new[] { webRoot, gamesRoot, logsRoot };
foreach (var dir in bootstrapDirs)
{
    try { Directory.CreateDirectory(dir); }
    catch (Exception ex)
    {
        // No web root ⇒ blank shell; no games root ⇒ no games can ever load — both block. A missing
        // logs/cache dir only degrades (the sinks tolerate it), so it's a non-blocking warning.
        var blocking = dir == webRoot || dir == gamesRoot;
        diagnostics.Report("A required directory could not be created",
            $"'{dir}' is missing and could not be created ({ex.Message}). Check the mount and its permissions.",
            blocking);
    }
}

// Probe the directories the server must WRITE to (logs always; the pre-compressed cache when enabled).
// An unwritable/wrong-owner mount here doesn't crash — the Serilog file sink and the precompressor both
// degrade gracefully — but the admin should know, so surface it on the warning page.
var writableDirs = precompressEnabled
    ? new[] { (logsRoot, "Logs folder"), (gamesCompressedRoot, "Pre-compressed cache") }
    : new[] { (logsRoot, "Logs folder") };
foreach (var (dir, label) in writableDirs)
{
    if (!Directory.Exists(dir)) continue; // a create failure above already reported it
    var writeError = ProbeWritable(dir);
    if (writeError is not null)
        // Non-blocking: the Serilog file sink and the precompressor both degrade gracefully, so this
        // never blanks a working site — but it's logged below and shown on the warning page if one is
        // already up for a blocking reason, so it gets fixed before a proper deployment.
        diagnostics.Report($"{label} is not writable",
            $"'{dir}' is not writable by the server ({writeError}). In Docker the container runs as UID 1654, so chown the mounted folder to that user.");
}

// Persist logs to a file that rolls once per day (knockbox-YYYYMMDD.log) while still echoing to the
// console for dev. Daily files are retained for KnockBox:LogRetentionDays days (default 31); because
// we roll once per day, the retained-file count equals the retained-day count. All existing
// ILogger<T> usage routes through this unchanged.
var logRetentionDays = builder.Configuration.GetValue("KnockBox:LogRetentionDays", 31);
// Levels are configured in code, NOT via ReadFrom.Configuration: that pulls in
// Serilog.Settings.Configuration, whose assembly scanning (DependencyContext / Assembly.Location) is
// not Native-AOT-safe and emits IL2104/IL3002/IL3053 at publish. ReadFrom.Services is DI-only and fine.
builder.Host.UseSerilog((context, services, config) => config
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logsRoot, "knockbox-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: logRetentionDays,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Games are served from a SEPARATE ORIGIN (a second port in dev, a subdomain in prod) so that
// untrusted game code is isolated from the shell — it cannot read the shell's identity token or
// socket — while still keeping a real origin (engine storage / COOP-COEP work). The game's own
// data-role websocket connects back to this origin's /ws.
var gamesPort = builder.Configuration.GetValue("KnockBox:GamesPort", 5115);
// In prod the game origin is a subdomain rather than a port; set these so routing and the origin
// handed to the shell work behind a reverse proxy where every request shares one local port.
var gamesHost = builder.Configuration["KnockBox:GamesHost"];           // e.g. "games.knockbox.example"
var gamesOrigin = builder.Configuration["KnockBox:GamesOrigin"];       // explicit override, e.g. "https://games.knockbox.example"

// launchSettings (dev) and ASPNETCORE_HTTP_PORTS (Docker) tell Kestrel which ports to bind; a bare
// published exe gets neither, so Kestrel would bind only the single framework default and the games
// origin (GamesPort) would refuse connections. When the host wasn't told what to bind, bind BOTH
// origins ourselves so the exe works out of the box. Anything explicit (URLS/HTTP_PORTS/Kestrel
// endpoints) wins and we stay out of the way — so dev and Docker are unaffected.
var portsConfigured =
    !string.IsNullOrEmpty(builder.Configuration["urls"])          // ASPNETCORE_URLS / --urls
    || !string.IsNullOrEmpty(builder.Configuration["http_ports"]) // ASPNETCORE_HTTP_PORTS
    || builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
if (!portsConfigured)
    builder.WebHost.UseUrls("http://localhost:5114", $"http://localhost:{gamesPort}");

// When true, the shell page itself is served cross-origin isolated (COOP/COEP) so threaded engine
// exports embedded in a cross-origin iframe can use SharedArrayBuffer. Off by default — single-
// threaded games don't need it and it constrains what the shell can embed.
var isolateShell = builder.Configuration.GetValue("KnockBox:IsolateShell", false);

// Origin allowlist for /ws (defense-in-depth; the real auth is the identity token / game ticket).
// Empty ⇒ allow all (dev convenience) with a startup warning to configure it for production.
var allowedOrigins = builder.Configuration.GetSection("KnockBox:AllowedOrigins").Get<string[]>() ?? [];

// Behind a TLS-terminating reverse proxy the request Scheme/Host are the proxy's, which would
// break the game origin (http instead of https → ws instead of wss) and GamesHost routing. Opt-in
// (KnockBox:ForwardedHeaders=true) because trusting X-Forwarded-* from arbitrary clients lets them
// spoof their IP past the per-IP connection cap.
var forwardedHeaders = builder.Configuration.GetValue("KnockBox:ForwardedHeaders", false);

// Abuse-protection limits (handshake deadline, per-connection rate limits, per-IP connection cap).
var limits = ServerLimits.FromConfiguration(builder.Configuration);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(sp =>
    new GameCatalog(gamesRoot, sp.GetRequiredService<ILogger<GameCatalog>>()));
if (precompressEnabled)
    builder.Services.AddSingleton(sp => new GameAssetPrecompressor(
        gamesRoot, gamesCompressedRoot, precompressGzip, precompressMinBytes,
        sp.GetRequiredService<ILogger<GameAssetPrecompressor>>()));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<LobbyManager>();
builder.Services.AddSingleton<WebSocketHandler>();

// Compress responses (game bundles are large). Brotli + Gzip, including the engine asset
// types that are off the default list. Level = Fastest to bound the CPU cost of compressing
// big payloads on the fly; combined with the ETag/Cache-Control below a client compresses an
// unchanged asset roughly once and then revalidates with 304s. NOTE: for production scale,
// precompressed `.br`/`.gz` next to each asset (served via content negotiation) avoids
// per-request CPU entirely — see the plan's load-time follow-up.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/wasm", "application/octet-stream"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();

// The resolved roots are the first thing an admin needs when "my games don't show up".
app.Logger.LogInformation("Content roots — web: {WebRoot}, games: {GamesRoot}, logs: {LogsRoot}, games-compressed: {GamesCompressedRoot} (precompress: {Precompress})",
    webRoot, gamesRoot, logsRoot, gamesCompressedRoot, precompressEnabled);
// A web root without the shell means a blank site — make the misconfiguration loud and diagnosable
// instead of silently serving nothing. (Blocking: surfaced on the home-page warning below.)
if (!File.Exists(Path.Combine(webRoot, "index.html")))
    diagnostics.Report("Platform shell is missing",
        $"No index.html under the web root '{webRoot}', so the shell can't be served. Verify the install/publish output, or set KnockBox:WebRoot to the folder containing the shell.",
        blocking: true);

// Log every bootstrap problem so it's visible without opening the site: blocking ones as errors,
// degraded-but-functional ones as warnings. (The live games-access error is logged by GameCatalog.)
foreach (var issue in diagnostics.Current())
{
    if (issue.Blocking)
        app.Logger.LogError("Deployment problem — {Title}: {Detail}", issue.Title, issue.Detail);
    else
        app.Logger.LogWarning("Deployment warning — {Title}: {Detail}", issue.Title, issue.Detail);
}

// Must run before anything that reads Request.Scheme/Host (the /ws map, OriginRouting.IsGameOrigin).
if (forwardedHeaders)
{
    var fho = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
                         | ForwardedHeaders.XForwardedHost,
    };
    // The proxy's address isn't knowable here (it differs per deployment); the explicit opt-in flag
    // is the admin's statement that a trusted proxy fronts this server.
    fho.KnownIPNetworks.Clear();
    fho.KnownProxies.Clear();
    app.UseForwardedHeaders(fho);
    app.Logger.LogInformation("ForwardedHeaders enabled — trusting X-Forwarded-For/Proto/Host from the fronting proxy.");
}

// Discover games at startup, then watch the folder so dropping in (or removing) a game needs no
// restart — server managers add games with no code and no downtime.
var catalog = app.Services.GetRequiredService<GameCatalog>();
// Surface the games folder's live read state on the warning page: an unreadable mount no longer
// crashes Discover() (below), it sets ScanError, which clears once a rescan succeeds.
diagnostics.GamesAccessError = () => catalog.ScanError;

// Keep the pre-compressed asset cache in lock-step with the catalog. Subscribing BEFORE the first
// Discover() means startup discovery also kicks the initial reconcile. The work is offloaded to a
// background task because SmallestSize over a large .wasm is slow and must never block discovery
// (it runs from FileSystemWatcher/poll callbacks) or startup.
GameAssetPrecompressor? precompressor = precompressEnabled ? app.Services.GetRequiredService<GameAssetPrecompressor>() : null;
if (precompressor is not null)
    catalog.Discovered += games => Task.Run(() =>
    {
        try { precompressor.ReconcileAll(games); }
        catch (Exception ex) { app.Logger.LogError(ex, "Pre-compression reconcile failed."); }
    });

catalog.Discover();
catalog.StartWatching();
// Polling safety net for bind mounts where file events don't propagate (Docker Desktop). 0 = off.
var gamesPollSeconds = builder.Configuration.GetValue("KnockBox:GamesPollSeconds", 0);
if (gamesPollSeconds > 0)
    catalog.StartPolling(TimeSpan.FromSeconds(gamesPollSeconds));

// Periodic reconcile: the schedule the cache also relies on to catch asset-only edits (the poll
// fingerprints GAME.json only) and to recover from any missed event. First tick is one interval out,
// after the startup reconcile above. Disposed on shutdown.
Timer? precompressTimer = null;
if (precompressor is not null && precompressReconcileSeconds > 0)
{
    var interval = TimeSpan.FromSeconds(precompressReconcileSeconds);
    precompressTimer = new Timer(_ =>
    {
        try { precompressor.ReconcileAll(catalog.Games); }
        catch (Exception ex) { app.Logger.LogError(ex, "Scheduled pre-compression reconcile failed."); }
    }, null, interval, interval);
    app.Lifetime.ApplicationStopping.Register(() => precompressTimer.Dispose());
}

if (allowedOrigins.Length == 0)
    app.Logger.LogWarning("KnockBox:AllowedOrigins is empty — /ws accepts any Origin. Set it for production.");

// Must precede the static-file maps (and the MapWhen branch) so it can wrap their responses.
app.UseResponseCompression();
app.UseWebSockets();

// PhysicalFileProvider throws when its root is missing; if directory creation failed above, fall
// back to an empty provider so the server still starts (the LogError above tells the admin why).
IFileProvider webFiles = Directory.Exists(webRoot) ? new PhysicalFileProvider(webRoot) : new NullFileProvider();
IFileProvider gamesFiles = Directory.Exists(gamesRoot) ? new PhysicalFileProvider(gamesRoot) : new NullFileProvider();
// The pre-compressed cache (.br/.gz siblings). NullFileProvider when precompression is off, so the
// negotiation middleware below always misses and serving falls back to raw + on-the-fly compression.
IFileProvider gamesCompressedFiles = precompressEnabled && Directory.Exists(gamesCompressedRoot)
    ? new PhysicalFileProvider(gamesCompressedRoot) : new NullFileProvider();

// `.wasm` is built in (application/wasm — REQUIRED for streaming WebAssembly compilation); keep
// the explicit `.pck`/`.data` mappings for clarity. Everything else falls through to the
// octet-stream default below, so any engine export's assets serve with zero server edits.
var gameContentTypes = new FileExtensionContentTypeProvider();
gameContentTypes.Mappings[".pck"] = "application/octet-stream";
gameContentTypes.Mappings[".data"] = "application/octet-stream";

// Shared options for serving game folders (used on both the game origin and the shell origin):
//   • ServeUnknownFileTypes + octet-stream default → no future engine asset 404s (zero-edit hosting).
//   • Cache-Control public/must-revalidate → caches store assets and revalidate via the ETag that
//     UseStaticFiles already emits, so unchanged builds (esp. the large .wasm) return 304 — safe
//     even with hot-reload because filenames aren't content-hashed.
StaticFileOptions GamesStaticOptions() => new()
{
    FileProvider = gamesFiles,
    RequestPath = "/games",
    ContentTypeProvider = gameContentTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public, max-age=0, must-revalidate",
};

// Serves a pre-compressed variant after NegotiateGameAssetEncoding has rewritten the path to the
// `.br`/`.gz` file and stashed the negotiated encoding + original content-type in HttpContext.Items.
// We reuse StaticFileMiddleware (free ETag/304/range/Content-Length on the variant bytes) and just fix
// up the headers in OnPrepareResponse: the body is the encoded representation, so we advertise
// Content-Encoding (which also makes ResponseCompression skip it — no double-compression), Vary on
// Accept-Encoding, and the DECOMPRESSED content-type (e.g. application/wasm, not octet-stream).
StaticFileOptions GamesCompressedStaticOptions() => new()
{
    FileProvider = gamesCompressedFiles,
    RequestPath = "/games",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        headers.CacheControl = "public, max-age=0, must-revalidate";
        headers.Vary = "Accept-Encoding";
        if (ctx.Context.Items[GameAssetNegotiation.EncodingItem] is string enc)
            headers.ContentEncoding = enc;
        if (ctx.Context.Items[GameAssetNegotiation.ContentTypeItem] is string contentType)
            ctx.Context.Response.ContentType = contentType;
    },
};

// Shell files (index.html, shell.js, home.css, knockbox.js) change between deploys and are tiny, so
// always revalidate — otherwise a browser can keep serving a heuristically-cached old shell after an
// update (e.g. a fresh shell.js with new message handling), which looks like "the fix didn't deploy".
StaticFileOptions WebStaticOptions() => new()
{
    FileProvider = webFiles,
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
};

// The single real-time transport (both origins/ports). The connection's role is decided by its
// first frame: Hello = control (shell), Attach = data (game). See WebSocketHandler.
// One machine gets a bounded number of concurrent sockets — a player legitimately holds two
// (control + game) per tab, so the cap is per-IP, generous, and released with the connection.
var ipGate = new IpConnectionGate(limits.MaxConnectionsPerIp);
app.Map("/ws", async (HttpContext ctx, WebSocketHandler handler) =>
{
    var origin = ctx.Request.Headers.Origin.ToString();
    if (!OriginRouting.OriginAllowed(origin, allowedOrigins))
    {
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var clientIp = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (!ipGate.TryEnter(clientIp))
    {
        app.Logger.LogWarning("Refusing /ws connection from {Ip}: per-IP connection cap reached.", clientIp);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        return;
    }

    try
    {
        // The game origin the shell should use to embed iframes (subdomain in prod, games port in dev).
        var gameOrigin = OriginRouting.ResolveGameOrigin(
            ctx.Request.Scheme, ctx.Request.Host.Host, gamesPort, gamesHost, gamesOrigin);

        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        await handler.HandleAsync(socket, gameOrigin, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled error on /ws connection.");
    }
    finally
    {
        ipGate.Exit(clientIp);
    }
});

// ── Game origin (separate port in dev, subdomain in prod) ──────────────────────
// Serves each game's static build under /games/{id}/… plus the game SDK at /knockbox.js, with
// per-game COOP/COEP opt-in for threaded engine exports. /ws is excluded so the shared WebSocket
// endpoint (mapped above) is reachable on this origin too — the game's data socket connects to it.
app.MapWhen(
    ctx => OriginRouting.IsGameOrigin(ctx.Connection.LocalPort, ctx.Request.Host.Host, gamesPort, gamesHost)
           && !ctx.Request.Path.StartsWithSegments("/ws"),
    gameApp =>
    {
        gameApp.Use(async (ctx, next) =>
        {
            ApplyCrossOriginIsolation(ctx, catalog);
            await next();
        });
        // Pre-compressed content negotiation: if a `.br`/`.gz` variant exists and the client accepts it,
        // rewrite to that path so the next middleware serves the cached, max-effort-compressed bytes
        // (no per-request CPU). On a miss this is a no-op and serving falls through to the raw file.
        if (precompressEnabled)
        {
            gameApp.Use(async (ctx, next) =>
            {
                NegotiateGameAssetEncoding(ctx, gamesCompressedFiles, gameContentTypes, precompressGzip);
                await next();
            });
            gameApp.UseStaticFiles(GamesCompressedStaticOptions());
        }
        gameApp.UseStaticFiles(WebStaticOptions());   // /knockbox.js
        gameApp.UseStaticFiles(GamesStaticOptions());
    });

// ── Shell origin (default port / apex host) ────────────────────────────────────
// Platform shell + SDK at the site root. The ONLY game asset the shell needs is each game's
// thumbnail for the lobby browser — the full (untrusted) build must load solely from the isolated
// game origin, never here, or it could run in the shell origin and read the identity token in
// sessionStorage. Optionally cross-origin isolate the shell so it can host threaded engine exports.
if (isolateShell)
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        // credentialless lets the shell still embed the cross-origin game iframe without requiring
        // every shell subresource to carry CORP, while keeping the page cross-origin isolated.
        ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
        await next();
    });

// Replace the shell home page with a diagnostic when a BLOCKING file-access problem is detected (see
// DeploymentWarningMiddleware). Registered before UseDefaultFiles/UseStaticFiles so it wins over a
// broken index.html.
app.UseMiddleware<DeploymentWarningMiddleware>(diagnostics);

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
app.UseStaticFiles(WebStaticOptions());

// Gate /games/* on the shell origin to each game's declared thumbnail only; everything else 404s,
// so untrusted game HTML/JS/WASM is unreachable here (it serves from the game origin). The static
// middleware below still handles content-type/ETag/caching for the allowed thumbnail.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value;
    if (path is not null
        && path.StartsWith("/games/", StringComparison.OrdinalIgnoreCase)
        && !IsAllowedThumbnail(path, catalog))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});
app.UseStaticFiles(GamesStaticOptions());

// app.Run() blocks for the server's lifetime. Guard it so an unhandled exception that would
// otherwise terminate the process is recorded, and the log buffer is always flushed on shutdown
// (UseSerilog assigns the static Log.Logger, so these route through the configured sinks).
try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "KnockBox server terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

// Best-effort writability check: create and delete a uniquely-named probe file. Returns null when the
// directory is writable, else the failure message. Side-effect-free on success (the probe is removed).
static string? ProbeWritable(string dir)
{
    var probe = Path.Combine(dir, $".kb-write-probe-{Guid.NewGuid():N}");
    try
    {
        File.WriteAllBytes(probe, []);
        File.Delete(probe);
        return null;
    }
    catch (Exception ex)
    {
        try { File.Delete(probe); } catch { /* nothing to clean up if the write never landed */ }
        return ex.Message;
    }
}

// Sets cross-origin-isolation headers for a CrossOriginIsolated game's assets so threaded
// Godot/Unity exports get SharedArrayBuffer. CORP: cross-origin lets the shell embed the frame.
// (Fully isolating a cross-origin iframe also requires the shell page to be cross-origin isolated
// and the iframe to carry allow="cross-origin-isolated" — see docs; single-threaded exports need
// none of this.)
static void ApplyCrossOriginIsolation(HttpContext ctx, GameCatalog catalog)
{
    var path = ctx.Request.Path.Value;
    if (path is null || !path.StartsWith("/games/", StringComparison.OrdinalIgnoreCase)) return;

    var rest = path["/games/".Length..];
    var slash = rest.IndexOf('/');
    var id = slash < 0 ? rest : rest[..slash];
    if (string.IsNullOrEmpty(id) || !catalog.TryGet(id, out var manifest) || !manifest.CrossOriginIsolated) return;

    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
}

// True only for "/games/{id}/{thumb}" where {thumb} exactly equals game {id}'s declared thumbnail.
// The exact-string whitelist is the control; PhysicalFileProvider also blocks any traversal.
static bool IsAllowedThumbnail(string path, GameCatalog catalog)
{
    var rest = path["/games/".Length..];
    var slash = rest.IndexOf('/');
    if (slash < 0) return false;
    var id = rest[..slash];
    var file = rest[(slash + 1)..];
    return catalog.TryGet(id, out var manifest)
        && !string.IsNullOrEmpty(manifest.Thumbnail)
        && string.Equals(file, manifest.Thumbnail, StringComparison.Ordinal);
}

// For a GET/HEAD of /games/{id}/…, if a pre-compressed variant the client accepts exists in the cache,
// rewrite the request to it and stash the negotiated encoding + the original (decompressed) content-type
// so GamesCompressedStaticOptions can set the right headers. A miss leaves the request untouched.
static void NegotiateGameAssetEncoding(
    HttpContext ctx, IFileProvider compressedFiles,
    Microsoft.AspNetCore.StaticFiles.IContentTypeProvider contentTypes, bool gzipEnabled)
{
    if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)) return;
    var path = ctx.Request.Path.Value;
    if (string.IsNullOrEmpty(path)
        || !path.StartsWith("/games/", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith('/')) return; // directory request — no single variant to serve

    var encoding = GameAssetPrecompressor.NegotiateEncoding(ctx.Request.Headers.AcceptEncoding.ToString(), gzipEnabled);
    if (encoding is null) return;

    var ext = encoding == "br" ? ".br" : ".gz";
    // PhysicalFileProvider.GetFileInfo is traversal-safe (blocks "..", rooted paths); the subpath is
    // relative to the provider root, mirroring the "/games" RequestPath the static options use.
    var variant = compressedFiles.GetFileInfo(path["/games".Length..] + ext);
    if (!variant.Exists || variant.IsDirectory) return;

    ctx.Items[GameAssetNegotiation.EncodingItem] = encoding;
    ctx.Items[GameAssetNegotiation.ContentTypeItem] =
        contentTypes.TryGetContentType(path, out var contentType) ? contentType : "application/octet-stream";
    ctx.Request.Path = path + ext;
}

// HttpContext.Items keys passing negotiated state from NegotiateGameAssetEncoding to the static-file
// OnPrepareResponse hook.
internal static class GameAssetNegotiation
{
    public const string EncodingItem = "kb.precompressed.encoding";
    public const string ContentTypeItem = "kb.precompressed.contentType";
}
