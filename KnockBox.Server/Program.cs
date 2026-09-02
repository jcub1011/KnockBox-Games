using System.IO.Compression;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using KnockBox.Server.Webhooks;
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
var contentPaths = ContentPaths.Resolve(
    builder.Configuration["KnockBox:WebRoot"],
    builder.Configuration["KnockBox:GamesRoot"],
    builder.Configuration["KnockBox:LogsRoot"],
    builder.Configuration["KnockBox:GamesCompressedRoot"],
    builder.Configuration["KnockBox:GamesUnpackedRoot"],
    builder.Configuration["KnockBox:GamesManagedRoot"],
    builder.Environment.ContentRootPath,
    AppContext.BaseDirectory);
var (webRoot, gamesRoot, logsRoot, gamesCompressedRoot, gamesUnpackedRoot, gamesManagedRoot) = contentPaths;

// Pre-compress game assets once into gamesCompressedRoot and serve those variants via Accept-Encoding
// negotiation, instead of re-compressing every full-body response on the fly (see the ResponseCompression
// note below). Master switch — off ⇒ exactly the on-the-fly behavior. The other Precompress* knobs and the
// cache root are read by the precompressor/serving setup further down.
var precompressEnabled = builder.Configuration.GetValue("KnockBox:Precompress", true);
// Gzip variants are OFF by default, unlike Brotli. A .kbg already carries its Brotli blobs, so seeding
// those costs a byte copy; a .gz has to be produced here at max effort from the extracted file, which is
// the one expensive thing left on an install. Brotli is accepted by ~97% of browsers and anything else
// still gets gzip on the fly from ResponseCompression — so the variants on disk buy a slightly better
// ratio for the last few percent of clients, at the price of the slowest step of every cold boot. An
// operator who wants them can say so; when they do, PruneOrphanVariants removes them again on the next
// reconcile if they change their mind.
var precompressGzip = builder.Configuration.GetValue("KnockBox:PrecompressGzip", false);
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

// The writable package root the ADMIN PORTAL installs into. gamesRoot is mounted read-only in
// production, so a package the portal fetched or was handed cannot be written there — it goes here and
// the installer scans both roots. Off ⇒ the root is never created and portal installs are refused;
// hand-dropped packages in gamesRoot keep working either way.
// Folded together because a managed root is worthless without the installer that reads it: with
// KnockBox:Packages=false nothing would ever extract what the portal installed, so the engine reports
// itself unavailable rather than accepting packages into a folder nothing scans.
var packageManagerOptions = PackageManagerOptions.FromConfiguration(builder.Configuration) with
{
    Enabled = packagesEnabled && builder.Configuration.GetValue("KnockBox:ManagedPackages", true),
};
var managedPackagesEnabled = packageManagerOptions.Enabled;

// Official marketplace: where admins browse and download game packages from. See docs/MARKETPLACE.md.
var marketplaceOptions = MarketplaceOptions.FromConfiguration(builder.Configuration);

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
if (managedPackagesEnabled) bootstrapDirs.Add(gamesManagedRoot);
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
if (managedPackagesEnabled) writableDirs.Add((gamesManagedRoot, "Managed package folder"));
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

// Will the state we write still be here after the next image update? In a container, anything not
// covered by a mount lives in the container's own writable layer, and replacing the container is
// exactly what updating an image does. Nothing about that failure is visible at runtime — the server
// runs perfectly against the empty replacement — so it has to be announced, or it is discovered when
// an operator finds the portal asking to be claimed again and the games they disabled serving players.
// This project's predecessor lost operator state on every TrueNAS Custom App update for exactly this
// reason. See StatePersistence for why a Dockerfile VOLUME is the wrong fix.
//
// Only the roots that CANNOT be rebuilt are reported. games-compressed, games-unpacked and logs are
// derived or disposable, and warning about all six would train an operator to skim past the one line
// that matters — the same reason the portal badges only actionable game states.
//
// Reported HERE, in bootstrap, rather than next to the other admin diagnostics further down: the
// issue-logging loop below runs before those, so a warning registered later would never reach the
// startup log — which is the one place an operator setting up a container actually looks.
var containerMounts = StatePersistence.InContainer() ? StatePersistence.CurrentMountPoints() : null;
if (containerMounts is not null)
{
    // Resolved through the services' own static helpers so this can never disagree with where the
    // files are actually written.
    var adminSecretPath = AdminAuthService.ResolveSecretPath(builder.Configuration);
    var adminSettingsPath = AdminSettingsStore.ResolveFilePath(builder.Configuration, adminSecretPath);

    List<(string Path, string Title, string Lost)> persistentState =
    [
        (Path.GetDirectoryName(adminSecretPath) ?? adminSecretPath, "Admin state is not persisted",
            $"the admin password ('{adminSecretPath}') and every saved operator policy decision " +
            $"('{adminSettingsPath}') — disabled and staged games, maintenance mode, runtime limit " +
            "overrides, banned room codes, the announcement, registered marketplaces and webhooks"),
    ];
    if (managedPackagesEnabled)
        persistentState.Add((gamesManagedRoot, "Installed packages are not persisted",
            "every game the admin portal installed. A marketplace package can be downloaded again; " +
            "one that was UPLOADED exists nowhere else"));
    // The games mount is the operator's own library. Warned about even when empty: the failure is
    // that anything dropped in later is lost, and by the time games are there the update has run.
    persistentState.Add((gamesRoot, "Games folder is not persisted",
        "every game in it, including any .kbg package copied in later"));

    foreach (var (path, title, lost) in persistentState)
    {
        if (!StatePersistence.IsEphemeral(containerMounts, path)) continue;
        // Non-blocking: the platform serves players correctly today, and blanking a working site over
        // a future loss would be its own outage. The startup log and the portal's Overview tab are the
        // channels here, the same two every other non-blocking issue uses.
        diagnostics.Report(title,
            $"'{path}' is inside the container, so the next image update will DESTROY {lost}. " +
            "Mount a named volume or a host directory there — see docs/HOSTING.md, 'Updating KnockBox'. " +
            "A host directory must be writable by UID 1654.");
    }
}

// Persist logs to a file that rolls once per day (knockbox-YYYYMMDD.log) while still echoing to the
// console for dev. Daily files are retained for KnockBox:LogRetentionDays days (default 31); because
// we roll once per day, the retained-file count equals the retained-day count. All existing
// ILogger<T> usage routes through this unchanged.
var logRetentionDays = builder.Configuration.GetValue("KnockBox:LogRetentionDays", 31);
// The admin portal's live log view reads from this ring rather than tailing the rolling file, so level
// and subsystem stay structured fields instead of text to re-parse. Constructed here (not resolved from
// DI) because Serilog is configured while the host is still being built; it is registered below so the
// admin API can read it.
var adminLogBuffer = new AdminLogBuffer(
    builder.Configuration.GetValue("KnockBox:AdminLogBufferSize", AdminLogBuffer.DefaultCapacity));
// Outbound webhooks (§4.2). The QUEUE is created here, before the host is built, for the same reason as
// the log ring above: the error sink that feeds it is constructed inside UseSerilog, where DI does not yet
// exist. The dispatcher that drains it is DI-built further down (it needs the settings store), and with
// KnockBox:WebhooksEnabled=false neither it nor any HttpClient is ever created — nothing reaches out.
var webhookOptions = WebhookOptions.FromConfiguration(builder.Configuration);
var webhookQueue = webhookOptions.Enabled ? new WebhookQueue() : null;
var webhookSink = webhookQueue is null
    ? null
    : new WebhookLogSink(webhookQueue, webhookOptions, TimeProvider.System);

// Levels are configured in code, NOT via ReadFrom.Configuration: that pulls in
// Serilog.Settings.Configuration, whose assembly scanning (DependencyContext / Assembly.Location) is
// not Native-AOT-safe and emits IL2104/IL3002/IL3053 at publish. ReadFrom.Services is DI-only and fine.
builder.Host.UseSerilog((context, services, config) =>
{
    config
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.Sink(adminLogBuffer)
        .WriteTo.File(
            Path.Combine(logsRoot, "knockbox-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: logRetentionDays,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
    // Only when webhooks are switched on — an extra sink that would never publish anything is still a
    // string render per error event.
    if (webhookSink is not null) config.WriteTo.Sink(webhookSink);
});

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
// How long a lobby may go without a relayed frame, a join or a leave before the admin portal calls it
// stale and "purge stale" collects it. Independent of DisconnectGraceSeconds, which is about one player's
// socket dropping; this is about a whole session nobody is playing any more (a tab left open on a
// finished game). 0 disables the idle test, leaving only "nobody is connected" as staleness.
var adminStaleAfter = TimeSpan.FromMinutes(
    builder.Configuration.GetValue("KnockBox:AdminStaleLobbyMinutes",
        (int)AdminOperations.DefaultStaleAfter.TotalMinutes));

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
// Which addresses may set X-Forwarded-*: IPs and/or CIDR ranges. Naming your proxy here is what makes
// every per-IP limit on this server mean anything, because otherwise the address they key on is one the
// client writes. Empty keeps the historical "trust any forwarder" behaviour (with a warning), so
// enabling ForwardedHeaders never silently breaks an existing deployment.
var knownProxies = builder.Configuration.GetSection("KnockBox:KnownProxies").Get<string[]>() ?? [];

// Abuse-protection limits (handshake deadline, per-connection rate limits, per-IP connection cap).
var limits = ServerLimits.FromConfiguration(builder.Configuration);

// Server-authority sandbox knobs (per-game opt-in via GAME.json serverAuthority).
var authorityOptions = AuthorityOptions.FromConfiguration(builder.Configuration);

// Periodic memory diagnostics. Each server-authority lobby holds a Jint engine, so footprint scales
// with concurrent authority lobbies; this log lets an operator correlate working set with live
// lobby/actor counts (and see whether memory falls back after lobbies close). 0 = off (default).
var memoryLogSeconds = builder.Configuration.GetValue("KnockBox:MemoryLogSeconds", 0);

// The dashboard's metric time series (§5.2). Sampled server-side so the graph is a property of the SERVER,
// not of one open browser tab — an operator opening the portal mid-incident gets the last hour, not an empty
// chart that starts filling from now. 0 = off.
var metricSampleSeconds = builder.Configuration.GetValue("KnockBox:MetricSampleSeconds", 15);
var metricHistoryPoints = builder.Configuration.GetValue(
    "KnockBox:MetricHistoryPoints", MetricHistory.DefaultCapacity);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(limits);
// The limits actually in force: the configured baseline above, plus whatever the operator has overridden
// from the portal. Registered as well as (not instead of) ServerLimits, because the values read once at
// startup — the IP gate's cap being the exception, see below — genuinely are startup values.
builder.Services.AddSingleton(sp => new LimitsProvider(sp.GetRequiredService<ServerLimits>()));
// The same shape for the server-authority knobs. A second provider rather than more fields on
// OperatorLimits: ApplyTo targets a different record, and ServerLimits.MaxLobbies (every lobby) and
// AuthorityMaxLobbies (only those holding a Jint engine) are different caps that must not merge.
builder.Services.AddSingleton(sp => new AuthorityOptionsProvider(sp.GetRequiredService<AuthorityOptions>()));
// The resolved content roots as one object: the admin API needs LogsRoot for the log downloads and all
// three game roots for disk-usage reporting, which is less error-prone than threading four strings
// through AdminApi.Options.
builder.Services.AddSingleton(contentPaths);
builder.Services.AddSingleton(adminLogBuffer);
builder.Services.AddSingleton(new MetricHistory(metricHistoryPoints));
// Per-game authority cost. A singleton like RelayMetrics, and pruned off the same catalog event.
builder.Services.AddSingleton<AuthorityMetrics>();
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
// The root ORDER is the precedence rule: games/ is scanned first, so a package an operator dropped in
// by hand beats a portal-installed one for a contested id — matching GameCatalog's folder precedence.
List<GamePackageInstaller.PackageRoot> packageRoots =
    [new(gamesRoot, PackageMarker.GamesRoot)];
if (managedPackagesEnabled) packageRoots.Add(new(gamesManagedRoot, PackageMarker.ManagedRoot));
if (packagesEnabled)
    builder.Services.AddSingleton(sp => new GamePackageInstaller(
        packageRoots, gamesUnpackedRoot, packageLimits,
        precompressEnabled ? sp.GetRequiredService<GameAssetPrecompressor>() : null,
        sp.GetRequiredService<ILogger<GamePackageInstaller>>()));
// The game marketplaces: the official catalog plus any the operator registered. Off ⇒ the server holds
// no HttpClient at all and makes no outbound request, which is the posture an air-gapped deployment
// wants — while uploading, rolling back and uninstalling a package all keep working.
//
// The OPTIONS are registered unconditionally (they carry Enabled themselves, so anything that needs to
// explain the switch can resolve them); only the registry, which owns the HttpClient, is conditional.
builder.Services.AddSingleton(marketplaceOptions);
if (marketplaceOptions.Enabled)
{
    // One HttpClient shared by every source: CreateHttpClient reads nothing source-specific, so a
    // client per marketplace would only cost connection pools.
    var marketplaceHttp = MarketplaceClient.CreateHttpClient();
    builder.Services.AddSingleton(sp => new MarketplaceSourceRegistry(
        marketplaceHttp, marketplaceOptions, packageLimits,
        sp.GetRequiredService<AdminSettingsStore>(),
        builder.Configuration.GetValue("KnockBox:MarketplaceMaxSources",
            MarketplaceSourceRegistry.DefaultMaxSources),
        sp.GetRequiredService<ILoggerFactory>()));
    // The policy layer over the install engine — which games this server may update on its own.
    builder.Services.AddSingleton<GameUpdateCoordinator>();
    // …and the schedule it runs on. Registered here rather than unconditionally, like everything else
    // marketplace-shaped: with the marketplace off there is nothing to check and no timer to arm.
    builder.Services.AddSingleton(sp => new UpdateScheduler(
        sp.GetRequiredService<GameUpdateCoordinator>(),
        sp.GetRequiredService<AdminSettingsStore>(),
        UpdateSchedule.FromConfiguration(builder.Configuration),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILogger<UpdateScheduler>>()));
}
// Outbound webhooks. Registered only when enabled, exactly like the marketplace registry, so a disabled
// deployment holds no HttpClient — and consumers resolve it as nullable and report the switch rather than
// failing to build. The client factory and the URL allow-rule are the marketplace's own, not copies.
builder.Services.AddSingleton(webhookOptions);
if (webhookQueue is not null)
{
    builder.Services.AddSingleton(webhookQueue);
    builder.Services.AddSingleton(webhookSink!);
    // The one client in the server with a destination filter. The marketplace's own instance keeps none:
    // its rule deliberately permits loopback http so a test or an offline mirror can serve a catalog, and
    // its URLs are derived from configuration rather than chosen through the portal.
    var webhookHttp = MarketplaceClient.CreateHttpClient(
        webhookOptions.AllowPrivateTargets ? null : PrivateAddressGuard.IsBlocked);
    builder.Services.AddSingleton(sp => new WebhookDispatcher(
        webhookQueue, sp.GetRequiredService<AdminSettingsStore>(), webhookHttp, webhookOptions,
        sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<WebhookDispatcher>>()));
}
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ConnectionManager>();
// Explicit factory (not AddSingleton<LobbyManager>) so the clock that stamps Lobby.CreatedAt — which
// the admin portal reports session duration from — is the registered TimeProvider, not the fallback.
// The code blocklist is passed as a delegate, not a value: it is read per generated code, so an operator's
// edit applies to the next lobby rather than the next restart.
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<AdminSettingsStore>();
    return new LobbyManager(sp.GetRequiredService<TimeProvider>(), () => settings.RoomCodes);
});
builder.Services.AddSingleton<LobbyCloser>();
builder.Services.AddSingleton(sp => new ServerAuthorityManager(
    // Where a game's files live is the catalog's answer, not gamesRoot/<id>: a game installed from a
    // .kbg is served out of the unpacked-package cache instead.
    id => sp.GetRequiredService<GameCatalog>().TryGetDirectory(id, out var dir) ? dir : null,
    authorityOptions,
    sp.GetRequiredService<ConnectionManager>(), sp.GetRequiredService<LobbyCloser>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<IAuthorityWordService>(),
    builder.Environment.IsDevelopment(),
    sp.GetRequiredService<ILoggerFactory>(),
    sp.GetRequiredService<AuthorityMetrics>(),
    sp.GetRequiredService<AuthorityOptionsProvider>()));
builder.Services.AddSingleton<RelayMetrics>();
builder.Services.AddSingleton<WebSocketHandler>();
builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<AdminSettingsStore>();
// The relay asks the policy questions through the narrow IPlatformPolicy. Two layers answer them: the
// settings store (persisted operator policy) and the lifecycle gate laid over it (transient "this game
// is mid-update"). The gate composes the store rather than replacing it, and is what IPlatformPolicy
// resolves to — so a toggle in the portal AND an in-flight update are both seen by the next
// lobby-create immediately, with no second copy of either state to keep in step.
builder.Services.AddSingleton<GameLifecycleGate>();
builder.Services.AddSingleton<IPlatformPolicy>(sp => sp.GetRequiredService<GameLifecycleGate>());
builder.Services.AddSingleton<DiskUsageReporter>();
builder.Services.AddSingleton<AdminOperations>();

// The install engine: the job feed the portal polls, and the manager that writes to the managed root.
// Both are registered even when ManagedPackages is off — the manager then refuses every install with a
// reason the portal can show, which is far more useful than an endpoint that isn't there.
builder.Services.AddSingleton(packageManagerOptions);
builder.Services.AddSingleton(sp => new PackageJobRegistry(
    sp.GetRequiredService<TimeProvider>(), packageManagerOptions.JobRetention));
builder.Services.AddSingleton(sp => new PackageManager(
    contentPaths,
    sp.GetRequiredService<GameCatalog>(),
    packagesEnabled ? sp.GetRequiredService<GamePackageInstaller>() : null,
    sp.GetRequiredService<PackageJobRegistry>(),
    sp.GetRequiredService<GameLifecycleGate>(),
    sp.GetRequiredService<LobbyManager>(),
    sp.GetRequiredService<LobbyCloser>(),
    packageLimits,
    packageManagerOptions,
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<PackageManager>>()));

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

// The resolved roots are the first thing an admin needs when "my games don't show up" — and the
// second thing they need when state vanished on an image update, which is why games-managed and the
// admin files are here too. All eight, not the five this once logged: a root missing from this line is
// a root whose mount an operator has to infer.
app.Logger.LogInformation(
    "Content roots — web: {WebRoot}, games: {GamesRoot}, logs: {LogsRoot}, games-compressed: {GamesCompressedRoot} " +
    "(precompress: {Precompress}), games-unpacked: {GamesUnpackedRoot} (packages: {Packages}), " +
    "games-managed: {GamesManagedRoot} (managed: {ManagedPackages}), admin state: {AdminStateRoot}",
    webRoot, gamesRoot, logsRoot, gamesCompressedRoot, precompressEnabled, gamesUnpackedRoot, packagesEnabled,
    gamesManagedRoot, managedPackagesEnabled,
    Path.GetDirectoryName(AdminAuthService.ResolveSecretPath(builder.Configuration)));

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
// The managed package root gets the same treatment, and additionally must not sit inside the unpacked
// root: the installer would find its own packages while enumerating extracted games, and Uninstall
// deletes any folder there that carries no marker.
if (managedPackagesEnabled &&
    (IsUnder(gamesManagedRoot, gamesRoot) || IsUnder(gamesRoot, gamesManagedRoot)
     || IsUnder(gamesManagedRoot, gamesUnpackedRoot) || IsUnder(gamesUnpackedRoot, gamesManagedRoot)))
    diagnostics.Report("Managed package folder overlaps another root",
        $"KnockBox:GamesManagedRoot ('{gamesManagedRoot}') must not contain, or sit inside, " +
        $"KnockBox:GamesRoot ('{gamesRoot}') or KnockBox:GamesUnpackedRoot ('{gamesUnpackedRoot}'). " +
        "It holds packages the server writes and must stay outside both.",
        blocking: true);
// A web root without the shell means a blank site — make the misconfiguration loud and diagnosable
// instead of silently serving nothing. (Blocking: surfaced on the home-page warning below.)
if (!File.Exists(Path.Combine(webRoot, "index.html")))
    diagnostics.Report("Platform shell is missing",
        $"No index.html under the web root '{webRoot}', so the shell can't be served. Verify the install/publish output, or set KnockBox:WebRoot to the folder containing the shell.",
        blocking: true);
// The admin portal's files get the same treatment. Silence here produced the worst kind of failure: an
// operator pointing KnockBox:WebRoot at a directory that predates the portal got a bound, reachable,
// completely blank origin, while the startup log still announced the portal as up. Non-blocking, because
// the shell and the games are unaffected — and the warning PAGE only replaces the shell home page, which
// is why the admin origin also explains itself in its own response (see the MapWhen branch below).
var adminWebRoot = Path.Combine(webRoot, "admin");
if (!Directory.Exists(adminWebRoot))
    diagnostics.Report("Admin portal files are missing",
        $"No 'admin' folder under the web root '{webRoot}', so the admin portal has nothing to serve and every " +
        "request to it answers 503. Verify the install/publish output, or set KnockBox:WebRoot to the folder " +
        "containing the shell.");

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
    // Start from empty rather than the loopback default, then add whatever the operator named. With both
    // collections empty ASP.NET Core skips the known-forwarder check entirely and takes X-Forwarded-* from
    // anyone — which is why an unset KnownProxies is a warning, not a default worth being quiet about: it
    // lets any caller choose the address every per-IP limit on this server keys on.
    fho.KnownIPNetworks.Clear();
    fho.KnownProxies.Clear();
    var rejectedProxies = new List<string>();
    foreach (var entry in knownProxies)
    {
        if (System.Net.IPNetwork.TryParse(entry, out var network)) fho.KnownIPNetworks.Add(network);
        else if (System.Net.IPAddress.TryParse(entry, out var address)) fho.KnownProxies.Add(address);
        else rejectedProxies.Add(entry);
    }
    if (rejectedProxies.Count > 0)
        app.Logger.LogError(
            "KnockBox:KnownProxies has {Count} unparseable entr(ies) — {Entries}. Each must be an IP address " +
            "(1.2.3.4, ::1) or a CIDR range (10.0.0.0/8). They are IGNORED, so the proxy they were meant to " +
            "name is not trusted.", rejectedProxies.Count, string.Join(", ", rejectedProxies));

    app.UseForwardedHeaders(fho);
    if (fho.KnownProxies.Count > 0 || fho.KnownIPNetworks.Count > 0)
        app.Logger.LogInformation(
            "ForwardedHeaders enabled — trusting X-Forwarded-For/Proto/Host from {Count} configured proxy address(es)/range(s).",
            fho.KnownProxies.Count + fho.KnownIPNetworks.Count);
    else
        app.Logger.LogWarning(
            "ForwardedHeaders enabled with no KnockBox:KnownProxies — X-Forwarded-For is trusted from ANY caller, so a " +
            "client can pick the IP the per-IP connection cap and the admin login throttle key on. Set " +
            "KnockBox:KnownProxies to your proxy's address or range.");
}

// Host-routing the admin portal means a proxy fronts it, and a proxy that isn't trusted to speak for the
// client degrades two things quietly: Request.IsHttps reads false, so the session cookie is issued without
// Secure unless the configured origin is explicitly https, and every login attempt buckets under the
// proxy's single address instead of the caller's.
if (!forwardedHeaders && (!string.IsNullOrWhiteSpace(adminHost) || !string.IsNullOrWhiteSpace(adminOrigin)))
    app.Logger.LogWarning(
        "The admin portal is host-routed but KnockBox:ForwardedHeaders is off, so the request scheme and client IP " +
        "seen here are the proxy's connection, not the browser's. Enable it (with KnockBox:KnownProxies naming your " +
        "proxy) so the session cookie is marked Secure behind TLS and the login throttle can tell callers apart.");

// Discover games at startup, then watch the folder so dropping in (or removing) a game needs no
// restart — server managers add games with no code and no downtime.
var catalog = app.Services.GetRequiredService<GameCatalog>();
// Surface the games folder's live read state on the warning page: an unreadable mount no longer
// crashes Discover() (below), it sets ScanError, which clears once a rescan succeeds.
diagnostics.AddProbe("Games folder is not accessible", () => catalog.ScanError, blocking: true);

// Saved operator policy (disabled games, maintenance mode) that couldn't be read is the sort of failure
// that looks like nothing: the platform runs fine, but a game the operator disabled is serving players
// again. A probe rather than Report() so it clears as soon as a successful save replaces the bad file.
var adminSettings = app.Services.GetRequiredService<AdminSettingsStore>();
diagnostics.AddProbe("Admin settings could not be read", () => adminSettings.LoadError);

// Put the operator's saved limit overrides into force. An object that can't be honoured is dropped on its
// own with a warning rather than taken or made fatal — the same treatment a marketplace row missing its
// URL gets, and for the same reason: one bad value must not cost the operator the rest of their policy,
// and silently running with limits nobody chose is worse than running with the configured ones.
var limitsProvider = app.Services.GetRequiredService<LimitsProvider>();
if (adminSettings.Limits.Validate(limitsProvider.Configured) is { } limitsError)
    app.Logger.LogError(
        "Ignoring the saved runtime limit overrides in '{Path}' and using the configured limits: {Reason}",
        adminSettings.FilePath, limitsError);
else
    limitsProvider.Apply(adminSettings.Limits);

// Same treatment for the saved server-authority overrides, for the reasons above.
var authorityLimitsProvider = app.Services.GetRequiredService<AuthorityOptionsProvider>();
if (adminSettings.AuthorityLimits.Validate() is { } authorityLimitsError)
    app.Logger.LogError(
        "Ignoring the saved server-authority overrides in '{Path}' and using the configured values: {Reason}",
        adminSettings.FilePath, authorityLimitsError);
else
    authorityLimitsProvider.Apply(adminSettings.AuthorityLimits);

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

// Closing a live lobby has to stop its authority actor too, or a server-authority lobby leaks its Jint
// engine for the process lifetime. The dependency runs closer <- manager (the manager's fatal path uses
// the closer), so the manager can't also be a constructor argument to it — hence this one-time hook,
// the same shape as the manager's own gameDirectory resolver. Set before any request is served.
app.Services.GetRequiredService<LobbyCloser>().OnClosing = authorityManager.Stop;

// Drop relay counters for games that no longer exist, so an uninstalled game doesn't hold a row in the
// metrics view forever. Inline like the two prunes above, and guarded for the same reason.
var relayMetrics = app.Services.GetRequiredService<RelayMetrics>();
catalog.Discovered += games =>
{
    try { relayMetrics.Prune(games.Keys); }
    catch (Exception ex) { app.Logger.LogError(ex, "Relay-metrics prune failed."); }
};

// Same for per-game authority cost, and for the same reason: an uninstalled game must not keep a row in the
// dashboard forever.
var authorityMetricsSingleton = app.Services.GetRequiredService<AuthorityMetrics>();
catalog.Discovered += games =>
{
    try { authorityMetricsSingleton.Prune(games.Keys); }
    catch (Exception ex) { app.Logger.LogError(ex, "Authority-metrics prune failed."); }
};

// Install .kbg packages off the same signal, for the same reason: it rides the catalog's watcher and
// polling instead of adding a second watcher, and extraction (potentially hundreds of megabytes) is
// offloaded so it never blocks a discovery running on a timer callback. Having changed something, it
// asks for a rediscovery through ScheduleRescan rather than Discover(): this fires on every pass, and
// the debounce is what collapses a burst of them into one walk.
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
        installer.PackagesObserved > 0 && catalog.Count == 0 && catalog.ScanError is null
            ? installer.InstallFailure
                ?? $"{installer.PackagesObserved} .kbg package(s) are in '{gamesRoot}' but no games were installed. " +
                   $"Check that '{gamesUnpackedRoot}' is writable by the server — in Docker the container runs as UID 1654."
            : null,
        blocking: true);
    // Malformed packages while other games work: worth reporting, but not worth blanking the site.
    diagnostics.AddProbe("A game package could not be installed", () => installer.InstallFailure);
}

// A download or upload the process died in the middle of leaves a .part file behind. Swept once at
// startup, which is the only moment nothing can be using one.
if (managedPackagesEnabled) app.Services.GetRequiredService<PackageManager>().SweepStaging();

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

// Scheduled marketplace check: fetch the registered catalogs and start an update for each game the
// operator ENROLLED (nothing is enrolled by default, and a pass with an empty enrolment makes no request
// at all). The schedule itself — daily at 03:00 UTC unless configured or changed in the portal — belongs
// to UpdateScheduler, which owns a re-armable timer so an edit applies to this process rather than the
// next one. Present only when the marketplace is enabled. Disposed on shutdown.
if (app.Services.GetService<UpdateScheduler>() is { } updateScheduler)
{
    updateScheduler.Start(app.Lifetime.ApplicationStopping);
    app.Lifetime.ApplicationStopping.Register(updateScheduler.Dispose);
}

// Idle-module sweep: stop holding the parsed AST of an authority game nobody is playing. Armed
// unconditionally — the window is editable from the portal, so a deployment that starts at 0 must still be
// able to switch eviction on without a restart, and a tick with a window of 0 does nothing. The cadence is
// fixed inside the manager while the window is read live; see the comment there for why deriving one from
// the other is the trap DisconnectGraceSeconds already fell into. Disposed on shutdown by the manager.
authorityManager.StartModuleCacheSweep(app.Lifetime.ApplicationStopping);

// The webhook drain: one long-lived task posting whatever lands in the queue. Not a timer — the queue is
// the schedule. Started here rather than as a hosted service to match the rest of this file's background
// work, and completed on shutdown so the backlog is flushed rather than abandoned mid-alert.
var webhookDispatcher = app.Services.GetService<WebhookDispatcher>();
if (webhookDispatcher is not null && webhookQueue is not null)
{
    // Install/update outcomes. The registry's terminal transition is the one chokepoint every path funnels
    // through (marketplace install, upload, rollback, uninstall, scheduled update), and it is idempotent —
    // so this fires exactly once per job, without any of those paths knowing webhooks exist.
    app.Services.GetRequiredService<PackageJobRegistry>().OnFinished = job =>
    {
        var name = job.GameName ?? job.GameId;
        var version = job.ToVersion is null ? "" : $" to {job.ToVersion}";
        webhookDispatcher.Publish(job.Status == PackageJobStatus.Succeeded
            ? WebhookDispatcher.Payload(WebhookEvent.UpdateApplied,
                $"{job.Kind} of '{name}'{version} succeeded.", job.EndedAt ?? DateTimeOffset.UtcNow,
                title: $"{job.Kind} succeeded", gameId: job.GameId, detail: job.Warning)
            : WebhookDispatcher.Payload(WebhookEvent.UpdateFailed,
                $"{job.Kind} of '{name}'{version} ended {job.Status}: {job.Error ?? job.Phase}",
                job.EndedAt ?? DateTimeOffset.UtcNow,
                title: $"{job.Kind} {job.Status}", gameId: job.GameId, detail: job.Error));
    };

    // The drain runs on its own token, NOT ApplicationStopping. Handing it ApplicationStopping cancelled
    // ReadAllAsync the instant shutdown began, so the Complete() below — whose whole purpose is to let the
    // loop finish the queue and exit — could never flush anything; the alerts explaining why the server is
    // going down were exactly the ones dropped. Completing the channel ends the loop on its own, and the
    // deadline is the backstop so a dead endpoint (bounded by WebhookTimeoutSeconds per attempt, one
    // attempt each) still cannot hold shutdown open.
    var drainGrace = TimeSpan.FromSeconds(5);
    var drainCts = new CancellationTokenSource();
    var draining = Task.Run(() => webhookDispatcher.RunAsync(drainCts.Token));
    app.Lifetime.ApplicationStopping.Register(() =>
    {
        webhookQueue.Complete();
        drainCts.CancelAfter(drainGrace);
    });
    // Referenced so the task isn't collected, and so a fault surfaces in the log rather than as silence.
    _ = draining.ContinueWith(t =>
        app.Logger.LogError(t.Exception, "The webhook drain task faulted."),
        TaskContinuationOptions.OnlyOnFaulted);
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

// Metric history sampler: the time series behind the dashboard's graphs. The counters it reads are all
// cumulative and lock-free, so a tick is a handful of reads and one array write — the same shape as the
// memory diagnostics above, which is the closest existing analogue. 0 = off (no history, no graphs).
Timer? metricSampleTimer = null;
if (metricSampleSeconds > 0)
{
    var history = app.Services.GetRequiredService<MetricHistory>();
    var lobbies = app.Services.GetRequiredService<LobbyManager>();
    var connections = app.Services.GetRequiredService<ConnectionManager>();
    var authorityMetrics = app.Services.GetRequiredService<AuthorityMetrics>();
    var interval = TimeSpan.FromSeconds(metricSampleSeconds);
    // Threshold alerts are EDGE-triggered: crossing fires once, and coming back under fires once. Sampling
    // every 15s and alerting per sample would post four times a minute for as long as the breach lasted,
    // which is how an alert channel becomes something people mute.
    var breaching = false;

    metricSampleTimer = new Timer(_ =>
    {
        try
        {
            var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            var relay = relayMetrics.Snapshot();
            var authority = authorityMetrics.Snapshot()
                .ToDictionary(a => a.GameId, StringComparer.OrdinalIgnoreCase);
            var lobbySnapshot = lobbies.Snapshot();

            var games = new List<MetricHistory.GameSample>(relay.Count);
            foreach (var game in relay)
            {
                authority.TryGetValue(game.GameId, out var cost);
                games.Add(new MetricHistory.GameSample(
                    game.GameId,
                    lobbySnapshot.Count(l => string.Equals(l.GameId, game.GameId, StringComparison.OrdinalIgnoreCase)),
                    game.FramesOut, game.BytesOut, game.FramesDropped, cost.CpuSeconds));
            }

            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var workingSetMb = Environment.WorkingSet / (1024 * 1024);
            history.Add(new MetricHistory.Sample(
                Sequence: 0, // stamped by Add
                At: now,
                CpuSeconds: Math.Round(self.TotalProcessorTime.TotalSeconds, 3),
                WorkingSetMb: workingSetMb,
                ManagedHeapMb: GC.GetTotalMemory(false) / (1024 * 1024),
                Lobbies: lobbies.Count,
                Players: connections.ControlCount,
                GameSockets: connections.GameCount,
                AuthorityLobbies: authorityManager.ActorCount,
                Games: games));

            // CPU here is the RATE between the last two samples, which is only knowable once there are two —
            // the same reason the portal shows "--" for CPU on its first poll.
            var samples = history.Read(Math.Max(0, history.LastSequence - 2));
            var cpuPercent = samples.Count >= 2 ? CpuPercentBetween(samples[^2], samples[^1]) : 0;
            var overMemory = webhookOptions.MemoryThresholdMb > 0 && workingSetMb > webhookOptions.MemoryThresholdMb;
            var overCpu = webhookOptions.CpuPercentThreshold > 0 && cpuPercent > webhookOptions.CpuPercentThreshold;
            var over = overMemory || overCpu;
            if (over != breaching)
            {
                breaching = over;
                webhookDispatcher?.Publish(WebhookDispatcher.Payload(
                    WebhookEvent.ResourceThreshold,
                    over
                        ? $"Resource threshold crossed: {workingSetMb} MB working set, {cpuPercent:F0}% CPU."
                        : $"Resource use back under threshold: {workingSetMb} MB working set, {cpuPercent:F0}% CPU.",
                    now,
                    title: over ? "Threshold breached" : "Threshold cleared"));
            }
        }
        catch (Exception ex) { app.Logger.LogError(ex, "Metric history sample failed."); }
    }, null, interval, interval);
    app.Lifetime.ApplicationStopping.Register(() => metricSampleTimer.Dispose());
}

// Percent of one core-equivalent between two samples. Mirrors admin-core.js cpuPercentBetween — including
// answering 0 rather than a negative when the counter goes backwards, which can only mean a restart.
static double CpuPercentBetween(MetricHistory.Sample previous, MetricHistory.Sample current)
{
    var seconds = (current.At - previous.At).TotalSeconds;
    var cpu = current.CpuSeconds - previous.CpuSeconds;
    if (seconds <= 0 || cpu < 0) return 0;
    return cpu / seconds / Environment.ProcessorCount * 100;
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
IFileProvider adminWebFiles = Directory.Exists(adminWebRoot) ? new PhysicalFileProvider(adminWebRoot) : new NullFileProvider();
var faviconsWebRoot = Path.Combine(webRoot, "favicons");
IFileProvider faviconsWebFiles = Directory.Exists(faviconsWebRoot) ? new PhysicalFileProvider(faviconsWebRoot) : new NullFileProvider();
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

static void ApplyGameCacheHeaders(IHeaderDictionary headers, string path)
{
    // HTML entry points and metadata manifests must never be cached by browsers or CDNs; an update
    // changes bundle hashes in index.html, and a stale cached HTML would try to load deleted chunks (404).
    if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
    {
        headers.CacheControl = "no-cache, no-store, must-revalidate";
        headers.Pragma = "no-cache";
    }
    // Bundled assets with content-hashed filenames (/assets/...) are immutable and safe to cache long-term.
    else if (path.Contains("/assets/", StringComparison.OrdinalIgnoreCase))
    {
        headers.CacheControl = "public, max-age=31536000, immutable";
    }
    // Other assets (audio, wasm, unhashed textures, thumbnails): revalidate with ETag.
    else
    {
        headers.CacheControl = "public, max-age=0, must-revalidate";
    }
}

// Shared options for serving game folders (used on both the game origin and the shell origin):
//   • ServeUnknownFileTypes + octet-stream default → no future engine asset 404s (zero-edit hosting).
//   • Differentiated Cache-Control: no-store for HTML/manifests (instant updates), immutable for
//     content-hashed assets under /assets/, and ETag-revalidated for everything else.
StaticFileOptions GamesStaticOptions() => new()
{
    FileProvider = gamesFiles,
    RequestPath = "/games",
    ContentTypeProvider = gameContentTypes,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    OnPrepareResponse = ctx =>
        ApplyGameCacheHeaders(ctx.Context.Response.Headers, ctx.Context.Request.Path.Value ?? ""),
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
        ApplyGameCacheHeaders(headers, ctx.Context.Request.Path.Value ?? "");
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
// The cap is read live off the limits provider, not captured: this gate is created once for the process,
// so a captured value would make the portal's control apply only after a restart — by which time the
// flood it was meant to stop has ended on its own.
var ipGate = new IpConnectionGate(() => limitsProvider.Current.MaxConnectionsPerIp);
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
        // The API itself lives in Hosting/AdminApi.cs — an HTTP API in the composition root is how its
        // fifth endpoint comes to disagree with its first four. `catalog` is already resolved above.
        adminApp.MapAdminApi(new AdminApi.Options(
            app.Services.GetRequiredService<AdminAuthService>(),
            app.Services.GetRequiredService<LobbyManager>(),
            app.Services.GetRequiredService<LobbyCloser>(),
            catalog,
            adminSettings,
            app.Services.GetRequiredService<GameLifecycleGate>(),
            app.Services.GetRequiredService<AdminOperations>(),
            app.Services.GetRequiredService<PackageManager>(),
            packageManagerOptions,
            packageLimits,
            app.Services.GetService<MarketplaceSourceRegistry>(),
            app.Services.GetService<GameUpdateCoordinator>(),
            app.Services.GetService<UpdateScheduler>(),
            adminLogBuffer,
            app.Services.GetRequiredService<DiskUsageReporter>(),
            relayMetrics,
            app.Services.GetRequiredService<AuthorityMetrics>(),
            app.Services.GetRequiredService<MetricHistory>(),
            metricSampleSeconds,
            limitsProvider,
            authorityLimitsProvider,
            app.Services.GetService<WebhookDispatcher>(),
            app.Services.GetService<WebhookLogSink>(),
            webhookOptions,
            app.Services.GetRequiredService<ConnectionManager>(),
            // Optional: with AuthorityEnabled=false the manager still exists, but keeping this nullable is
            // what lets the portal report "0 authority lobbies" instead of the origin failing to build if
            // the registration ever becomes conditional like the marketplace's.
            app.Services.GetService<ServerAuthorityManager>(),
            contentPaths,
            diagnostics,
            app.Services.GetRequiredService<TimeProvider>(),
            app.Logger,
            limits.AdminLoginAttemptsPerMinute,
            limits.AdminLoginAttemptsPerMinuteGlobal,
            // An https admin origin means a proxy terminates TLS in front of us, so the session cookie
            // must be Secure even though the request reaching Kestrel is plain HTTP.
            CookieAlwaysSecure: adminOrigin?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true,
            StaleAfter: adminStaleAfter));

        // No web/admin in the web root ⇒ the portal's files aren't there. Say so at the origin itself:
        // this is reported through DeploymentDiagnostics too, but the warning PAGE only replaces the
        // SHELL home page, so an operator staring at the admin origin would otherwise get an empty 404
        // from the static-file middleware and no explanation anywhere.
        if (adminWebFiles is NullFileProvider)
        {
            adminApp.Run(async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(
                    $"The admin portal's files are missing: no 'admin' folder under the web root '{webRoot}'.\n" +
                    "Verify the install/publish output, or point KnockBox:WebRoot at the folder containing the shell.\n");
            });
        }
        else
        {
            adminApp.UseDefaultFiles(new DefaultFilesOptions { FileProvider = adminWebFiles });
            adminApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = adminWebFiles,
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
            });
            adminApp.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = faviconsWebFiles,
                RequestPath = "/favicons",
                OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, must-revalidate"
            });
        }
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
        gameApp.UseStaticFiles(WebStaticOptions());   // /knockbox.js + /kb-protocol.js
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
    if (GameAssetPath.GameId(ctx.Request.Path.Value) is not { } id) return;
    if (!catalog.TryGet(id, out var manifest) || !manifest.CrossOriginIsolated) return;

    ctx.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
    ctx.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
    ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
}

// True only for "/games/{id}/{thumb}" where {thumb} names game {id}'s declared thumbnail. Both sides
// are canonicalized (GameAssetPath) so a non-canonical spelling of the SAME file still matches, while
// the comparison itself stays ordinal — this is an allowlist, and a case-insensitive one would admit
// more than the manifest declared. PhysicalFileProvider also blocks any traversal.
static bool IsAllowedThumbnail(string path, GameCatalog catalog)
{
    if (!GameAssetPath.TryParse(path, out var id, out var file)) return false;
    return catalog.TryGet(id, out var manifest)
        && GameAssetPath.Canonicalize(manifest.Thumbnail) is { } thumbnail
        && string.Equals(file, thumbnail, StringComparison.Ordinal);
}

// For a GET/HEAD of /games/{id}/…, if a pre-compressed variant the client accepts exists in the cache,
// rewrite the request to it and stash the negotiated encoding + the original (decompressed) content-type
// so GamesCompressedStaticOptions can set the right headers. A miss leaves the request untouched.

