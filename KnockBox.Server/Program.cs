using System.Net.WebSockets;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Resolve the repo root (holds web/ and games/ at the flat top level) by walking up from the
// content root until we find the solution file. Robust for `dotnet run` and a published exe alike.
var repoRoot = FindRepoRoot(builder.Environment.ContentRootPath);
var webRoot = Path.Combine(repoRoot, "web");
var gamesRoot = Path.Combine(repoRoot, "games");
Directory.CreateDirectory(webRoot);
Directory.CreateDirectory(gamesRoot);

builder.Services.AddSingleton(sp =>
    new GameCatalog(gamesRoot, sp.GetRequiredService<ILogger<GameCatalog>>()));
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<LobbyManager>();
builder.Services.AddSingleton<WebSocketHandler>();

var app = builder.Build();

// Discover games once at startup.
app.Services.GetRequiredService<GameCatalog>().Discover();

app.UseWebSockets();

// Platform shell + KnockBox JS SDK served at the site root (same origin as /ws).
var webFiles = new PhysicalFileProvider(webRoot);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = webFiles });

// Each game's static build served under /games/{gameId}/...
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(gamesRoot),
    RequestPath = "/games",
});

// The single real-time transport: lobby ops + in-game relay over one socket.
app.Map("/ws", async (HttpContext ctx, WebSocketHandler handler) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    await handler.HandleAsync(socket, ctx.RequestAborted);
});

app.Run();

static string FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "KnockBox-Games.slnx")))
            return dir.FullName;
    return start;
}
