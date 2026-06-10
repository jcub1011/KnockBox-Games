using System.Net.WebSockets;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Resolve the repo root (holds web/ and games/ at the flat top level) by walking up from the
// content root until we find the solution file. Robust for `dotnet run` and a published exe alike.
var repoRoot = FindRepoRoot(builder.Environment.ContentRootPath);
var webRoot = Path.Combine(repoRoot, "web");
var gamesRoot = Path.Combine(repoRoot, "games");
Directory.CreateDirectory(webRoot);
Directory.CreateDirectory(gamesRoot);

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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
    new GameCatalog(gamesRoot, sp.GetRequiredService<ILogger<GameCatalog>>()));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<LobbyManager>();
builder.Services.AddSingleton<WebSocketHandler>();

var app = builder.Build();

// Discover games at startup, then watch the folder so dropping in (or removing) a game needs no
// restart — server managers add games with no code and no downtime.
var catalog = app.Services.GetRequiredService<GameCatalog>();
catalog.Discover();
catalog.StartWatching();

if (allowedOrigins.Length == 0)
    app.Logger.LogWarning("KnockBox:AllowedOrigins is empty — /ws accepts any Origin. Set it for production.");

app.UseWebSockets();

var webFiles = new PhysicalFileProvider(webRoot);
var gamesFiles = new PhysicalFileProvider(gamesRoot);

// The single real-time transport (both origins/ports). The connection's role is decided by its
// first frame: Hello = control (shell), Attach = data (game). See WebSocketHandler.
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

    // The game origin the shell should use to embed iframes (subdomain in prod, games port in dev).
    var gameOrigin = OriginRouting.ResolveGameOrigin(
        ctx.Request.Scheme, ctx.Request.Host.Host, gamesPort, gamesHost, gamesOrigin);

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await handler.HandleAsync(socket, gameOrigin, ctx.RequestAborted);
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
        gameApp.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });   // /knockbox.js
        gameApp.UseStaticFiles(new StaticFileOptions { FileProvider = gamesFiles, RequestPath = "/games" });
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
app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = gamesFiles, RequestPath = "/games" });

app.Run();

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

static string FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "KnockBox-Games.slnx")))
            return dir.FullName;
    return start;
}
