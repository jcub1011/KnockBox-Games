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
var (webRoot, gamesRoot, logsRoot) = ContentPaths.Resolve(
    builder.Configuration["KnockBox:WebRoot"],
    builder.Configuration["KnockBox:GamesRoot"],
    builder.Configuration["KnockBox:LogsRoot"],
    builder.Environment.ContentRootPath,
    AppContext.BaseDirectory);

// Best-effort: a read-only games mount (recommended in Docker) or a root-owned parent must not
// crash startup — GameCatalog and the static-file setup below both tolerate a missing directory.
var directoryWarnings = new List<string>();
foreach (var dir in new[] { webRoot, gamesRoot, logsRoot })
{
    try { Directory.CreateDirectory(dir); }
    catch (Exception ex) { directoryWarnings.Add($"Could not create '{dir}': {ex.Message}"); }
}

// Persist logs to a file that rolls once per day (knockbox-YYYYMMDD.log) while still echoing to the
// console for dev. Daily files are retained for KnockBox:LogRetentionDays days (default 31); because
// we roll once per day, the retained-file count equals the retained-day count. All existing
// ILogger<T> usage routes through this unchanged.
var logRetentionDays = builder.Configuration.GetValue("KnockBox:LogRetentionDays", 31);
builder.Host.UseSerilog((context, services, config) => config
    .ReadFrom.Configuration(context.Configuration)
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
app.Logger.LogInformation("Content roots — web: {WebRoot}, games: {GamesRoot}, logs: {LogsRoot}",
    webRoot, gamesRoot, logsRoot);
foreach (var warning in directoryWarnings)
    app.Logger.LogWarning("{Warning}", warning);
// A web root without the shell means a blank site — make the misconfiguration loud and diagnosable
// instead of silently serving nothing.
if (!File.Exists(Path.Combine(webRoot, "index.html")))
    app.Logger.LogError(
        "Web root {WebRoot} has no index.html — the platform shell will serve a blank site. " +
        "Set KnockBox:WebRoot to the folder containing the shell, or verify the install/publish output.",
        webRoot);

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
catalog.Discover();
catalog.StartWatching();
// Polling safety net for bind mounts where file events don't propagate (Docker Desktop). 0 = off.
var gamesPollSeconds = builder.Configuration.GetValue("KnockBox:GamesPollSeconds", 0);
if (gamesPollSeconds > 0)
    catalog.StartPolling(TimeSpan.FromSeconds(gamesPollSeconds));

if (allowedOrigins.Length == 0)
    app.Logger.LogWarning("KnockBox:AllowedOrigins is empty — /ws accepts any Origin. Set it for production.");

// Must precede the static-file maps (and the MapWhen branch) so it can wrap their responses.
app.UseResponseCompression();
app.UseWebSockets();

// PhysicalFileProvider throws when its root is missing; if directory creation failed above, fall
// back to an empty provider so the server still starts (the LogError above tells the admin why).
IFileProvider webFiles = Directory.Exists(webRoot) ? new PhysicalFileProvider(webRoot) : new NullFileProvider();
IFileProvider gamesFiles = Directory.Exists(gamesRoot) ? new PhysicalFileProvider(gamesRoot) : new NullFileProvider();

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
        gameApp.UseStaticFiles(WebStaticOptions());   // /knockbox.js
        gameApp.UseStaticFiles(GamesStaticOptions());
    });

// ── Shell origin (default port / apex host) ────────────────────────────────────
// Platform shell + SDK at the site root; game thumbnails under /games for the lobby browser.
// Optionally cross-origin isolate the shell so it can host threaded engine exports (see IsolateShell).
if (isolateShell)
    app.Use(async (ctx, next) =>
    {
        ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        // credentialless lets the shell still embed the cross-origin game iframe without requiring
        // every shell subresource to carry CORP, while keeping the page cross-origin isolated.
        ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "credentialless";
        await next();
    });

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
app.UseStaticFiles(WebStaticOptions());
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
