using System.IO.Compression;
using System.Net.WebSockets;
using System.Text.Json;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Where web/, games/, and logs/ live: explicit config wins, else repo discovery (dev), else the
// app base directory (published exe / container — publish bakes web/ in, games/ sits alongside or
// is volume-mounted). See ContentPaths for the precedence rules.
var (webRoot, gamesRoot, logsRoot, gamesCompressedRoot, gamesUnpackedRoot) = ContentPaths.Resolve(
    builder.Configuration["KnockBox:WebRoot"],
    builder.Configuration["KnockBox:GamesRoot"],
    builder.Configuration["KnockBox:LogsRoot"],
    builder.Configuration["KnockBox:GamesCompressedRoot"],
    builder.Configuration["KnockBox:GamesUnpackedRoot"],
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

// Install .kbg game packages dropped into the games folder. They can't be expanded in place (that mount
// is read-only in production), so they're extracted into gamesUnpackedRoot, which GameCatalog searches
// after gamesRoot. Master switch — off ⇒ .kbg files are ignored entirely and only plain game folders work.
var packagesEnabled = builder.Configuration.GetValue("KnockBox:Packages", true);
var packageLimits = GamePackageLimits.FromConfiguration(builder.Configuration);

// Best-effort: a read-only games mount (recommended in Docker) or a root-owned parent must not crash
// startup. GameCatalog and the static-file setup below tolerate a directory that is missing OR exists
// but is unreadable; any problem found here (and the live probes registered later) is collected in
// DeploymentDiagnostics and surfaced on the shell home page so a misconfigured deployment is loud.
var diagnostics = new DeploymentDiagnostics();
// The two cache roots are independent features, so each is added on its own rather than sharing one
// conditional — the precompressed cache can be off while packages are on, and vice versa.
List<string> bootstrapDirs = [webRoot, gamesRoot, logsRoot];
if (precompressEnabled) bootstrapDirs.Add(gamesCompressedRoot);
if (packagesEnabled) bootstrapDirs.Add(gamesUnpackedRoot);
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
List<(string Dir, string Label)> writableDirs = [(logsRoot, "Logs folder")];
if (precompressEnabled) writableDirs.Add((gamesCompressedRoot, "Pre-compressed cache"));
if (packagesEnabled) writableDirs.Add((gamesUnpackedRoot, "Game package cache"));
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
var adminPort = builder.Configuration.GetValue("KnockBox:AdminPort", 5116);
// In prod the game origin is a subdomain rather than a port; set these so routing and the origin
// handed to the shell work behind a reverse proxy where every request shares one local port.
var gamesHost = builder.Configuration["KnockBox:GamesHost"];           // e.g. "games.knockbox.example"
var gamesOrigin = builder.Configuration["KnockBox:GamesOrigin"];       // explicit override, e.g. "https://games.knockbox.example"
var adminHost = builder.Configuration["KnockBox:AdminHost"];           // e.g. "admin.knockbox.example"
var adminOrigin = builder.Configuration["KnockBox:AdminOrigin"];       // explicit override, e.g. "https://admin.knockbox.example"

// launchSettings (dev) and ASPNETCORE_HTTP_PORTS (Docker) tell Kestrel which ports to bind; a bare
// published exe gets neither, so Kestrel would bind only the single framework default and the games
// origin (GamesPort) would refuse connections. When the host wasn't told what to bind, bind ALL THREE
// origins (shell, games, admin) ourselves so the exe works out of the box. Anything explicit
// (URLS/HTTP_PORTS/Kestrel endpoints) wins and we stay out of the way — so dev and Docker are unaffected.
//
// THE TRAP: because an explicit setting replaces this list wholesale rather than adding to it, every
// origin must appear in EVERY place that sets ports — launchSettings.json `applicationUrl`, the
// Dockerfile's ASPNETCORE_HTTP_PORTS, any Kestrel:Endpoints section. An origin omitted from one of those
// simply never binds and answers "connection refused". The ApplicationStarted check below turns that
// silent hole into a startup warning; keep it if you add a fourth origin.
var portsConfigured =
    !string.IsNullOrEmpty(builder.Configuration["urls"])          // ASPNETCORE_URLS / --urls
    || !string.IsNullOrEmpty(builder.Configuration["http_ports"]) // ASPNETCORE_HTTP_PORTS
    || builder.Configuration.GetSection("Kestrel:Endpoints").Exists();
if (!portsConfigured)
    builder.WebHost.UseUrls("http://localhost:5114", $"http://localhost:{gamesPort}", $"http://localhost:{adminPort}");

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

// Server-authority sandbox knobs (per-game opt-in via GAME.json serverAuthority).
var authorityOptions = AuthorityOptions.FromConfiguration(builder.Configuration);

// Periodic memory diagnostics. Each server-authority lobby holds a Jint engine, so footprint scales
// with concurrent authority lobbies; this log lets an operator correlate working set with live
// lobby/actor counts (and see whether memory falls back after lobbies close). 0 = off (default).
var memoryLogSeconds = builder.Configuration.GetValue("KnockBox:MemoryLogSeconds", 0);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(limits);
builder.Services.AddSingleton(authorityOptions);
// Search order matters: the administrator's games folder first, then games extracted from .kbg
// packages, so a hand-placed folder always wins a contested id. With packages off there is only one root.
List<string> gameRoots = packagesEnabled ? [gamesRoot, gamesUnpackedRoot] : [gamesRoot];
builder.Services.AddSingleton(sp =>
    new GameCatalog(gameRoots, sp.GetRequiredService<ILogger<GameCatalog>>(),
        authorityOptions.MaxScriptBytes, authorityOptions.MaxWordFileBytes));
builder.Services.AddSingleton<IAuthorityWordService, AuthorityWordService>();
if (precompressEnabled)
    builder.Services.AddSingleton(sp => new GameAssetPrecompressor(
        gamesCompressedRoot, precompressGzip, precompressMinBytes,
        sp.GetRequiredService<ILogger<GameAssetPrecompressor>>()));
// Registered as a singleton (rather than constructed inline) so the container disposes it on shutdown.
if (packagesEnabled)
    builder.Services.AddSingleton(sp => new GamePackageInstaller(
        gamesRoot, gamesUnpackedRoot, packageLimits,
        precompressEnabled ? sp.GetRequiredService<GameAssetPrecompressor>() : null,
        sp.GetRequiredService<ILogger<GamePackageInstaller>>()));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<LobbyManager>();
builder.Services.AddSingleton(sp => new ServerAuthorityManager(
    // Where a game's files live is the catalog's answer, not gamesRoot/<id>: a game installed from a
    // .kbg is served out of the unpacked-package cache instead.
    id => sp.GetRequiredService<GameCatalog>().TryGetDirectory(id, out var dir) ? dir : null,
    authorityOptions,
    sp.GetRequiredService<ConnectionManager>(), sp.GetRequiredService<LobbyManager>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IAuthorityWordService>(),
    builder.Environment.IsDevelopment(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<WebSocketHandler>();
builder.Services.AddSingleton<AdminAuthService>();

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
app.Logger.LogInformation(
    "Content roots — web: {WebRoot}, games: {GamesRoot}, logs: {LogsRoot}, games-compressed: {GamesCompressedRoot} " +
    "(precompress: {Precompress}), games-unpacked: {GamesUnpackedRoot} (packages: {Packages})",
    webRoot, gamesRoot, logsRoot, gamesCompressedRoot, precompressEnabled, gamesUnpackedRoot, packagesEnabled);

// Where the admin portal actually is — read from Kestrel's BOUND addresses, not from configuration.
// Announcing a configured-but-unbound URL is exactly how the portal came to answer "connection refused"
// while the log insisted it was up, so this waits for ApplicationStarted (the bound set isn't known
// before then) and warns when the admin port isn't among them. See the port-binding trap above.
app.Lifetime.ApplicationStarted.Register(() =>
{
    // Host-routed deployments share the public port, so there is no separate admin port to look for —
    // the configured origin IS the answer, and its scheme is whatever the fronting proxy terminates.
    if (!string.IsNullOrWhiteSpace(adminOrigin))
    {
        app.Logger.LogInformation("Admin portal served on {AdminOrigin} (host-routed).", adminOrigin.TrimEnd('/'));
        return;
    }
    if (!string.IsNullOrWhiteSpace(adminHost))
    {
        app.Logger.LogInformation(
            "Admin portal served on host {AdminHost} (host-routed; scheme follows your proxy). Any request " +
            "carrying this Host reaches the admin app, including one arriving on the public port — keep " +
            "KnockBox:ForwardedHeaders and your proxy's Host handling correct.", adminHost);
        return;
    }

    var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
    var adminAddress = addresses?.FirstOrDefault(a => BoundPort(a) == adminPort);
    if (adminAddress is not null)
    {
        app.Logger.LogInformation("Admin portal listening at {AdminUrl}", adminAddress);
        return;
    }

    app.Logger.LogWarning(
        "Admin portal is UNREACHABLE: nothing is listening on admin port {AdminPort} (Kestrel bound: {BoundAddresses}). " +
        "An explicit ASPNETCORE_URLS / ASPNETCORE_HTTP_PORTS / Kestrel:Endpoints setting REPLACES the built-in port " +
        "defaults instead of adding to them, so the admin port has to be listed there too. Add it, or point " +
        "KnockBox:AdminPort at a port that is bound, or set KnockBox:AdminHost / KnockBox:AdminOrigin to route the " +
        "admin origin by host instead of by port.",
        adminPort,
        addresses is null || addresses.Count == 0 ? "(unknown)" : string.Join(", ", addresses));
});

// A writable cache root nested inside the games folder would be self-defeating: the catalog's recursive
// watcher would see the server's own extraction writes and re-trigger itself, and every extracted game
// would also be found under gamesRoot, colliding with its own id.
if (packagesEnabled && (IsUnder(gamesUnpackedRoot, gamesRoot) || IsUnder(gamesRoot, gamesUnpackedRoot)))
    diagnostics.Report("Game package cache overlaps the games folder",
        $"KnockBox:GamesUnpackedRoot ('{gamesUnpackedRoot}') and KnockBox:GamesRoot ('{gamesRoot}') must not contain " +
        "one another — the cache is written by the server and must stay outside the games folder it reads.",
        blocking: true);
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
diagnostics.AddProbe("Games folder is not accessible", () => catalog.ScanError, blocking: true);

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

// Keep the shared word pools in lock-step with the catalog so they don't accumulate stale copies as
// games/dictionaries change. Subscribing before the first Discover() means startup discovery also
// runs the initial (no-op) prune. Prune is trivial in-memory set work, so it runs inline (unlike the
// precompressor's slow compression) — but guarded so a throw can't break sibling Discovered handlers.
var wordService = app.Services.GetRequiredService<IAuthorityWordService>();
catalog.Discovered += games =>
{
    try { wordService.Prune(games); }
    catch (Exception ex) { app.Logger.LogError(ex, "Word-pool prune failed."); }
};

// Same rationale for the shared authority module cache: drop parsed modules for games that no longer
// exist so they don't leak a parsed AST for the process lifetime. Trivial in-memory set work, so
// inline like the word-pool prune — guarded so a throw can't break sibling Discovered handlers.
var authorityManager = app.Services.GetRequiredService<ServerAuthorityManager>();
catalog.Discovered += games =>
{
    try { authorityManager.PruneModuleCache(games); }
    catch (Exception ex) { app.Logger.LogError(ex, "Authority module-cache prune failed."); }
};

// Install .kbg packages off the same signal, for the same reason: it rides the catalog's watcher and
// polling instead of adding a second watcher, and extraction (potentially hundreds of megabytes) is
// offloaded so it never blocks a discovery running on a timer callback. Having changed something, it
// asks for a rediscovery through ScheduleRescan — never Discover() directly, which has no mutual
// exclusion and could let an older scan win the publish and hide the game just installed.
GamePackageInstaller? installer = packagesEnabled ? app.Services.GetRequiredService<GamePackageInstaller>() : null;
if (installer is not null)
{
    catalog.Discovered += _ => Task.Run(() =>
    {
        try
        {
            // Rescan on Pending too, not just Changed: a package that hasn't settled yet (still being
            // copied) or one counting down to removal needs another pass, and this is the only thing
            // that will schedule one when no further file events arrive.
            var (changed, pending) = installer.Reconcile();
            if (changed || pending) catalog.ScheduleRescan();
        }
        catch (Exception ex) { app.Logger.LogError(ex, "Game package install pass failed."); }
    });

    // An unwritable cache root is only a non-blocking warning, and the warning PAGE is only shown for
    // blocking issues — so without this probe an operator who ships nothing but .kbg files to a
    // container whose cache dir wasn't chowned would see zero games and zero explanation. Packages
    // present but no games discovered is precisely "the server can't serve its core purpose".
    diagnostics.AddProbe("Game packages could not be installed", () =>
        installer.PackagesObserved > 0 && catalog.Games.Count == 0 && catalog.ScanError is null
            ? installer.InstallFailure
                ?? $"{installer.PackagesObserved} .kbg package(s) are in '{gamesRoot}' but no games were installed. " +
                   $"Check that '{gamesUnpackedRoot}' is writable by the server — in Docker the container runs as UID 1654."
            : null,
        blocking: true);
    // Malformed packages while other games work: worth reporting, but not worth blanking the site.
    diagnostics.AddProbe("A game package could not be installed", () => installer.InstallFailure);
}

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
        try { precompressor.ReconcileAll(catalog.GameLocations); }
        catch (Exception ex) { app.Logger.LogError(ex, "Scheduled pre-compression reconcile failed."); }
    }, null, interval, interval);
    app.Lifetime.ApplicationStopping.Register(() => precompressTimer.Dispose());
}

// Reconnect-grace reaper: a member whose shell socket drops is kept in their lobby for
// limits.DisconnectGrace so a tab refresh/blip doesn't kick them out; this timer evicts the ones
// who never came back. Sweep frequently enough that eviction latency stays small without a
// per-player timer: at most ~5s (cheap for the 60s default), but never tighter than 1s so a very
// low configured grace doesn't spin a hot loop. Disabled when grace is 0. Disposed on shutdown.
Timer? disconnectReaperTimer = null;
if (limits.DisconnectGrace > TimeSpan.Zero)
{
    var handler = app.Services.GetRequiredService<WebSocketHandler>();
    var interval = TimeSpan.FromSeconds(Math.Clamp(limits.DisconnectGrace.TotalSeconds, 1, 5));
    disconnectReaperTimer = new Timer(_ =>
    {
        try { handler.ReapDisconnectedPlayers(); }
        catch (Exception ex) { app.Logger.LogError(ex, "Reconnect-grace reaper sweep failed."); }
    }, null, interval, interval);
    app.Lifetime.ApplicationStopping.Register(() => disconnectReaperTimer.Dispose());
}

// Memory diagnostics: log GC/working-set stats alongside the live lobby and authority-actor counts,
// so an operator can see whether footprint scales with concurrent authority lobbies and whether it
// falls back after lobbies close. Off by default (0); disposed on shutdown.
Timer? memoryLogTimer = null;
if (memoryLogSeconds > 0)
{
    var lobbyManager = app.Services.GetRequiredService<LobbyManager>();
    // authorityManager is captured above (module-cache prune wiring) — reuse it.
    var interval = TimeSpan.FromSeconds(memoryLogSeconds);
    memoryLogTimer = new Timer(_ =>
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            app.Logger.LogInformation(
                "Memory — workingSet: {WorkingSetMB} MB, managedHeap: {ManagedHeapMB} MB, gcCommitted: {GcCommittedMB} MB, " +
                "gc(g0/g1/g2): {Gen0}/{Gen1}/{Gen2}, lobbies: {Lobbies}, authorityActors: {Actors}",
                Environment.WorkingSet / (1024 * 1024),
                GC.GetTotalMemory(false) / (1024 * 1024),
                info.TotalCommittedBytes / (1024 * 1024),
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                lobbyManager.Count, authorityManager.ActorCount);
        }
        catch (Exception ex) { app.Logger.LogError(ex, "Memory diagnostics sweep failed."); }
    }, null, interval, interval);
    app.Lifetime.ApplicationStopping.Register(() => memoryLogTimer.Dispose());
}

// Server-authority actors hold live Jint engines and tick timers — stop them all on shutdown
// (each actor's drain task disposes its own engine).
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<ServerAuthorityManager>().StopAll());

if (allowedOrigins.Length == 0)
    app.Logger.LogWarning("KnockBox:AllowedOrigins is empty — /ws accepts any Origin. Set it for production.");

// Must precede the static-file maps (and the MapWhen branch) so it can wrap their responses.
app.UseResponseCompression();
app.UseWebSockets();

// PhysicalFileProvider throws when its root is missing; if directory creation failed above, fall
// back to an empty provider so the server still starts (the LogError above tells the admin why).
IFileProvider webFiles = Directory.Exists(webRoot) ? new PhysicalFileProvider(webRoot) : new NullFileProvider();
var adminWebRoot = Path.Combine(webRoot, "admin");
IFileProvider adminWebFiles = Directory.Exists(adminWebRoot) ? new PhysicalFileProvider(adminWebRoot) : new NullFileProvider();
// Games are served from the games folder first and the unpacked-package cache second — the same
// precedence GameCatalog applies, so the manifest a request resolves through and the assets it fetches
// always come from the same place. CompositeFileProvider returns the first provider whose file exists,
// and each member is still a PhysicalFileProvider, so ETags, ranges and sendfile behave exactly as before.
IFileProvider gamesFiles = new CompositeFileProvider(
    Directory.Exists(gamesRoot) ? new PhysicalFileProvider(gamesRoot) : new NullFileProvider(),
    packagesEnabled && Directory.Exists(gamesUnpackedRoot)
        ? new PhysicalFileProvider(gamesUnpackedRoot) : new NullFileProvider());
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

// Serves a pre-compressed variant after GameAssetNegotiation.Negotiate has rewritten the path to the
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

// ── Admin origin (separate port in dev, subdomain in prod) ─────────────────────
// Dedicated admin portal. Public player files, game bundles (/games), and /ws are excluded.
app.MapWhen(
    ctx => OriginRouting.IsAdminOrigin(ctx.Connection.LocalPort, ctx.Request.Host.Host, adminPort, adminHost),
    adminApp =>
    {
        // Resolved once while the pipeline is built, never per request. `catalog` is already resolved
        // above and reused here.
        var authService = app.Services.GetRequiredService<AdminAuthService>();
        var lobbyManager = app.Services.GetRequiredService<LobbyManager>();
        // Read once: Process.GetCurrentProcess() allocates a handle on every call, and the dashboard
        // polls /admin/api/system/status every few seconds — resolving it per request leaked a handle
        // per poll. The start time can't change, so there is nothing to re-read.
        DateTime processStartedUtc;
        using (var self = System.Diagnostics.Process.GetCurrentProcess())
            processStartedUtc = self.StartTime.ToUniversalTime();
        // Every password attempt costs a 600k-iteration PBKDF2 (~0.4s of a core, by design), so these two
        // endpoints are the only unauthenticated way to make this server do real work. Without a throttle
        // that is both a guessing oracle and a CPU-exhaustion lever that starves the game relay — a handful
        // of requests per second saturates every core. Checked BEFORE any hashing, so a refused attempt is
        // free. Burst = the per-minute allowance: a human typo-ing a password is unaffected.
        var adminAuthLimiter = new IpRateLimiter(
            limits.AdminLoginAttemptsPerMinute / 60.0, limits.AdminLoginAttemptsPerMinute,
            app.Services.GetRequiredService<TimeProvider>());

        adminApp.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "";

            // Both password endpoints share one bucket: they are equally expensive, and an attacker who
            // could spend the login budget on setup attempts would just have two budgets.
            var isPasswordAttempt =
                HttpMethods.IsPost(ctx.Request.Method) &&
                (string.Equals(path, "/admin/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(path, "/admin/api/auth/setup", StringComparison.OrdinalIgnoreCase));
            if (isPasswordAttempt &&
                !adminAuthLimiter.TryTake(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"))
            {
                app.Logger.LogWarning(
                    "Throttled an admin password attempt from {Ip} ({Path}): more than {Limit} per minute.",
                    ctx.Connection.RemoteIpAddress, path, limits.AdminLoginAttemptsPerMinute);
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.Response.Headers.RetryAfter = "60";
                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(ctx.Response.Body,
                    new AdminApiResponse(false, "Too many attempts. Wait a minute and try again."),
                    KnockBoxProtocolContext.Default.AdminApiResponse);
                return;
            }

            if (string.Equals(path, "/admin/api/auth/status", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(ctx.Request.Method))
            {
                var configured = authService.IsConfigured;
                var authenticated = false;
                if (ctx.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var sessionCookie))
                {
                    authenticated = authService.ValidateSessionToken(sessionCookie);
                }
                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminAuthStatusResponse(configured, authenticated), KnockBoxProtocolContext.Default.AdminAuthStatusResponse);
                return;
            }

            if (string.Equals(path, "/admin/api/auth/setup", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(ctx.Request.Method))
            {
                var req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, KnockBoxProtocolContext.Default.AdminPasswordRequest);
                if (req is null || string.IsNullOrWhiteSpace(req.Password))
                {
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    ctx.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(false, "Password required."), KnockBoxProtocolContext.Default.AdminApiResponse);
                    return;
                }

                // Distinct messages per outcome: "already configured" is an expected race, a too-short
                // password is the operator's to fix, but an unwritable secret file is a DEPLOYMENT fault
                // (wrong owner on the mount) that previously hid behind the same "already configured or
                // invalid" text — the single most confusing way this could fail.
                var setup = authService.SetupPassword(req.Password);
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
                             $"Could not save the password to '{authService.SecretFilePath}'. Check that the path exists " +
                             "and is writable by the server (in Docker, the container runs as UID 1654)."),
                    };
                    ctx.Response.StatusCode = status;
                    ctx.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(false, error), KnockBoxProtocolContext.Default.AdminApiResponse);
                    return;
                }

                AppendAdminSessionCookie(ctx, authService.CreateSessionToken());

                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(true), KnockBoxProtocolContext.Default.AdminApiResponse);
                return;
            }

            if (string.Equals(path, "/admin/api/auth/login", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(ctx.Request.Method))
            {
                var req = await JsonSerializer.DeserializeAsync(ctx.Request.Body, KnockBoxProtocolContext.Default.AdminPasswordRequest);
                if (req is null || string.IsNullOrWhiteSpace(req.Password) || !authService.VerifyPassword(req.Password))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(false, "Invalid admin password."), KnockBoxProtocolContext.Default.AdminApiResponse);
                    return;
                }

                AppendAdminSessionCookie(ctx, authService.CreateSessionToken());

                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(true), KnockBoxProtocolContext.Default.AdminApiResponse);
                return;
            }

            if (string.Equals(path, "/admin/api/auth/logout", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsPost(ctx.Request.Method))
            {
                // Deleting a cookie is really a Set-Cookie with an expiry, and the browser only replaces
                // the existing one when the attributes match how it was issued — so mirror them.
                ctx.Response.Cookies.Delete(AdminAuthService.SessionCookieName, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = ctx.Request.IsHttps,
                    Path = "/"
                });
                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(true), KnockBoxProtocolContext.Default.AdminApiResponse);
                return;
            }

            if (string.Equals(path, "/admin/api/system/status", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(ctx.Request.Method))
            {
                if (!ctx.Request.Cookies.TryGetValue(AdminAuthService.SessionCookieName, out var sessionCookie) || !authService.ValidateSessionToken(sessionCookie))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    ctx.Response.ContentType = "application/json";
                    await JsonSerializer.SerializeAsync(ctx.Response.Body, new AdminApiResponse(false, "Unauthorized."), KnockBoxProtocolContext.Default.AdminApiResponse);
                    return;
                }

                var uptime = DateTime.UtcNow - processStartedUtc;
                var uptimeStr = $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";

                var workingSetMb = Environment.WorkingSet / (1024 * 1024);
                var managedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024);

                var status = new AdminSystemStatusResponse(
                    uptimeStr,
                    lobbyManager.Count,
                    catalog.Games.Count,
                    workingSetMb,
                    managedHeapMb,
                    DateTime.UtcNow.ToString("O")
                );

                ctx.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, status, KnockBoxProtocolContext.Default.AdminSystemStatusResponse);
                return;
            }

            await next();
        });

        adminApp.UseDefaultFiles(new DefaultFilesOptions { FileProvider = adminWebFiles });
        adminApp.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = adminWebFiles,
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
        });
    });

// Gate public origins to return 404 for any /admin* request
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value;
    if (path is not null && (path.Equals("/admin", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/admin/", StringComparison.OrdinalIgnoreCase)))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
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
        // FIRST, before the encoding negotiation can rewrite paths: never serve a game's
        // server-authority module (or a stale pre-compressed variant of it) — it is server-side
        // code, and for hidden-information games its secrecy is the point (design §11). This holds
        // however the game arrived: the gate matches the catalog's manifest, so a module extracted
        // from a .kbg is refused exactly like one in a hand-placed folder.
        gameApp.Use(async (ctx, next) =>
        {
            if (GameOriginAssetGate.IsDeniedAuthorityAsset(ctx.Request.Path.Value ?? "", catalog))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
        // .kbg packages sit in the games folder, which is served under /games — and GamesStaticOptions
        // sets ServeUnknownFileTypes, so /games/<name>.kbg would hand out the whole archive at a
        // guessable URL, uncached. Its CONTENTS are public (they're the game), but a multi-megabyte
        // uncacheable download is a needless bandwidth amplifier, so refuse it. The shell origin already
        // refuses it via the thumbnail allowlist below.
        gameApp.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.Value?.EndsWith(GamePackage.Extension, StringComparison.OrdinalIgnoreCase) == true)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
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
                GameAssetNegotiation.Negotiate(ctx, gamesCompressedFiles, gameContentTypes, precompressGzip);
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
        // Best-effort cleanup of the zero-byte probe file; a failure here leaves a harmless orphan,
        // nothing an operator can act on. The MEANINGFUL error — that the dir isn't writable — is not
        // swallowed: it's returned below and surfaced/logged via the deployment diagnostics. (No logger
        // exists yet anyway; this probe runs before Serilog is configured.)
        try { File.Delete(probe); } catch { /* best effort: nothing to clean up if the write never landed */ }
        return ex.Message;
    }
}

// Issues the admin session cookie. One place for it so the setup and login paths can't drift apart.
// HttpOnly keeps the token away from script; SameSite=Strict means it never rides a cross-site request;
// Secure follows the CURRENT request's scheme, so the cookie hardens behind TLS without breaking the
// plain-HTTP loopback the portal is reached over in dev and in Docker (where it binds 127.0.0.1 only).
static void AppendAdminSessionCookie(HttpContext ctx, string token) =>
    ctx.Response.Cookies.Append(AdminAuthService.SessionCookieName, token, new CookieOptions
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Secure = ctx.Request.IsHttps,
        Path = "/"
    });

// The port out of one of Kestrel's bound addresses ("http://localhost:5116", "http://[::]:8082"), or null
// if it can't be parsed. Only ever used to decide whether to warn, so an unparseable address must not
// throw its way out of the ApplicationStarted callback.
static int? BoundPort(string address)
{
    try { return BindingAddress.Parse(address).Port; }
    catch (Exception) { return null; }
}

// True when `inner` is the same directory as, or nested inside, `outer`. Used to reject a configuration
// where a server-written cache root overlaps the games folder it reads.
static bool IsUnder(string inner, string outer)
{
    var innerFull = Path.GetFullPath(inner).TrimEnd(Path.DirectorySeparatorChar);
    var outerFull = Path.GetFullPath(outer).TrimEnd(Path.DirectorySeparatorChar);
    if (string.Equals(innerFull, outerFull, StringComparison.OrdinalIgnoreCase)) return true;
    return innerFull.StartsWith(outerFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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

