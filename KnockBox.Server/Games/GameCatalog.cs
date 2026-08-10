using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// Discovers HTML5 games by scanning <c>&lt;root&gt;/*/GAME.json</c> across one or more roots. The
/// server never runs game logic — it only needs the manifest (id, entry, player counts) to list
/// games and create lobbies. In-memory only. A <see cref="FileSystemWatcher"/> re-discovers on change
/// so a server manager can drop in (or remove) a game folder with no restart and no code.
///
/// Roots are searched IN ORDER and the first one to supply an id wins, so a plain folder an
/// administrator placed in the games directory always beats the same id extracted from a
/// <c>.kbg</c> package. Only the FIRST root is watched/polled: later roots are derived caches
/// written by the server itself (see <c>GamePackageInstaller</c>), which trigger rediscovery
/// directly rather than through a second watcher.
/// </summary>
public sealed class GameCatalog : IDisposable
{
    /// <summary>A discovered game: its manifest plus the directory its files are served from.</summary>
    /// <remarks>
    /// The directory is kept HERE rather than on <see cref="GameManifest"/> because the manifest is a
    /// wire DTO sent to clients — a server-side filesystem path must never leak into it.
    /// </remarks>
    private sealed record GameEntry(GameManifest Manifest, string Directory);

    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger<GameCatalog> _logger;

    /// <param name="roots">
    /// Search order, most-authoritative first. Must contain at least one root; <c>roots[0]</c> is the
    /// administrator's games directory and the only one watched for changes.
    /// </param>
    public GameCatalog(IReadOnlyList<string> roots, ILogger<GameCatalog> logger)
    {
        if (roots.Count == 0) throw new ArgumentException("At least one games root is required.", nameof(roots));
        _roots = roots;
        _logger = logger;
    }

    /// <summary>Convenience overload for the common single-root case (tests, simple hosts).</summary>
    public GameCatalog(string gamesRoot, ILogger<GameCatalog> logger) : this([gamesRoot], logger) { }

    /// <summary>The administrator-owned games directory: the root that is watched and polled.</summary>
    public string PrimaryRoot => _roots[0];

    // Swapped atomically by Discover(). Readers take the reference once and enumerate a stable
    // snapshot, so a concurrent rebuild can never expose a half-built catalog (no lock needed).
    // ONE dictionary holds both the manifest and its directory: two parallel dictionaries could not
    // be swapped atomically together, letting a reader pair a pre-swap manifest with a post-swap path.
    private volatile IReadOnlyDictionary<string, GameEntry> _games =
        new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly Lock _debounceGate = new();
    private bool _disposed;
    private Timer? _poll;
    // Written by the poll timer thread and (once) by the startup thread; volatile guarantees the
    // timer callback sees the seeded value on weakly-ordered architectures (ARM).
    private volatile string _pollFingerprint = "";

    // Set when a scan fails because the games folder exists but can't be READ (e.g. a Docker mount
    // the server's user has no read/execute on) — as opposed to simply missing or empty, which are
    // benign. Surfaced to the deployment-warning home page; cleared on the next clean scan, so it
    // reflects the live state and disappears once permissions are fixed. Tracks the PRIMARY root
    // only: a derived cache root that can't be read degrades .kbg installs but never blocks the
    // plain folders in the games directory, so it must not blank a working site.
    private volatile string? _scanError;
    public string? ScanError => _scanError;

    /// <summary>
    /// Raised at the end of every successful <see cref="Discover"/>, after the atomic swap, with the
    /// freshly-published catalog as an id → directory map. Lets derived state (the pre-compressed
    /// asset cache, the .kbg installer) rebuild on the same add/remove/edit signal the watcher and
    /// poll already drive — no second watcher needed. Handlers must not throw and should offload
    /// heavy work to a background task.
    /// </summary>
    public event Action<IReadOnlyDictionary<string, string>>? Discovered;

    public IReadOnlyCollection<GameManifest> Games => [.. _games.Values.Select(e => e.Manifest)];

    public bool TryGet(string id, out GameManifest manifest)
    {
        if (_games.TryGetValue(id, out var entry)) { manifest = entry.Manifest; return true; }
        manifest = null!;
        return false;
    }

    /// <summary>
    /// The directory a discovered game's files live in. Needed by anything that reads a game's assets
    /// (the pre-compressed cache), because with multiple roots the path is no longer
    /// <c>gamesRoot/&lt;id&gt;</c>.
    /// </summary>
    public bool TryGetDirectory(string id, out string directory)
    {
        if (_games.TryGetValue(id, out var entry)) { directory = entry.Directory; return true; }
        directory = "";
        return false;
    }

    /// <summary>Snapshot of id → serving directory for every discovered game.</summary>
    public IReadOnlyDictionary<string, string> GameDirectories =>
        _games.ToDictionary(kv => kv.Key, kv => kv.Value.Directory, StringComparer.OrdinalIgnoreCase);

    /// <summary>Scans every games root and atomically swaps in the rebuilt catalog.</summary>
    public void Discover()
    {
        var next = new Dictionary<string, GameEntry>(StringComparer.OrdinalIgnoreCase);
        string? primaryError = null;

        for (var i = 0; i < _roots.Count; i++)
        {
            var root = _roots[i];
            var isPrimary = i == 0;

            if (!Directory.Exists(root))
            {
                // Only the administrator's games directory is worth mentioning; a derived cache root
                // that hasn't been created yet is entirely normal.
                if (isPrimary) _logger.LogWarning("Games folder not found at {Path}; no games discovered.", root);
                continue;
            }

            // Materialize eagerly so an access failure throws HERE (and is caught) rather than
            // mid-iteration. The folder exists but can't be read — e.g. a Docker mount the server's
            // user (UID 1654) has no read/execute on. This must NOT crash startup (Discover() runs
            // from Program.Main and from timer callbacks): skip the root, surface it for the warning
            // page, and recover on the next rescan.
            List<string> dirs;
            try
            {
                dirs = [.. Directory.EnumerateDirectories(root)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Cannot read games folder {Path}; skipping it until access is restored.", root);
                if (isPrimary)
                {
                    primaryError = $"The games folder '{root}' exists but could not be read ({ex.Message}). " +
                        "Ensure it is readable by the server — in Docker the container runs as UID 1654, so the " +
                        "mounted folder must grant that user read and execute.";
                }
                continue;
            }

            foreach (var dir in dirs) TryAddGame(dir, root, next);
        }

        _scanError = primaryError;
        _games = next; // atomic publish
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Game catalog ready: {Count} game(s) [{Ids}]", next.Count, string.Join(", ", next.Keys));

        // Notify after the swap so handlers see the published catalog. A misbehaving handler must not
        // break hot-reload, so swallow and log — Discover() is itself called from timer callbacks.
        try { Discovered?.Invoke(GameDirectories); }
        catch (Exception ex) { _logger.LogError(ex, "A Discovered handler threw; continuing."); }
    }

    /// <summary>Validates one candidate game directory and adds it to <paramref name="next"/>.</summary>
    private void TryAddGame(string dir, string root, Dictionary<string, GameEntry> next)
    {
        var manifestPath = Path.Combine(dir, "GAME.json");
        if (!File.Exists(manifestPath)) return;

        try
        {
            var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), KnockBoxProtocolContext.Default.GameManifest);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            {
                _logger.LogWarning("Skipping {Path}: empty or invalid manifest.", manifestPath);
                return;
            }

            // Assets are served at /games/{id}/…, so the folder name must equal the id or loads 404.
            var folderName = new DirectoryInfo(dir).Name;
            if (!string.Equals(folderName, manifest.Id, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping game '{Id}': folder name '{Folder}' must match the manifest id.", manifest.Id, folderName);
                return;
            }

            if (string.IsNullOrWhiteSpace(manifest.Entry))
            {
                _logger.LogWarning("Skipping game '{Id}': manifest has no entry.", manifest.Id);
                return;
            }

            // The entry must resolve to a file inside the game folder — never escape it (path traversal).
            var dirFull = Path.GetFullPath(dir);
            var entryFull = Path.GetFullPath(Path.Combine(dir, manifest.Entry));
            var dirPrefix = dirFull.EndsWith(Path.DirectorySeparatorChar) ? dirFull : dirFull + Path.DirectorySeparatorChar;
            if (!entryFull.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping game '{Id}': entry '{Entry}' escapes the game folder.", manifest.Id, manifest.Entry);
                return;
            }
            if (!File.Exists(entryFull))
            {
                _logger.LogWarning("Skipping game '{Id}': entry file '{Entry}' not found.", manifest.Id, manifest.Entry);
                return;
            }

            // First root to claim an id wins. Without this the loser would still have its ASSETS
            // served (static files resolve by path, not through the catalog), so a request could pair
            // one folder's manifest with another folder's files — a baffling thing to debug. Say so.
            if (next.TryGetValue(manifest.Id, out var existing))
            {
                _logger.LogWarning(
                    "Duplicate game id '{Id}': keeping {Kept} and ignoring {Ignored}. Two games cannot share an id — " +
                    "rename one, or remove the stale copy.", manifest.Id, existing.Directory, dir);
                return;
            }

            next[manifest.Id] = new GameEntry(manifest, dir);
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Discovered game '{Id}' ({Name}) from {Dir}", manifest.Id, manifest.Name, dir);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load manifest at {Path}", manifestPath);
        }
    }

    /// <summary>
    /// Watches the games folder and re-runs <see cref="Discover"/> ~500 ms after the last change,
    /// so a burst of file events (a folder being copied in) triggers a single rebuild. Only the
    /// primary root is watched — see the class remarks.
    /// </summary>
    public void StartWatching()
    {
        if (_watcher is not null || !Directory.Exists(PrimaryRoot)) return;

        _watcher = new FileSystemWatcher(PrimaryRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };

        void onChange(object _, FileSystemEventArgs __) => ScheduleRescan();
        _watcher.Created += onChange;
        _watcher.Changed += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => ScheduleRescan();
        // On buffer overflow the OS drops events and the watcher stops raising them; without this,
        // hot-reload would silently die. Log it and force a rescan so we recover the current state.
        _watcher.Error += (_, e) =>
        {
            _logger.LogWarning(e.GetException(), "Game folder watcher error; forcing a rescan.");
            ScheduleRescan();
        };

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Watching {Path} for game changes (hot-reload enabled).", PrimaryRoot);
    }

    /// <summary>
    /// Polling safety net for environments where <see cref="FileSystemWatcher"/> is unreliable —
    /// chiefly Docker bind mounts on Docker Desktop, where host file events never reach the
    /// container. Each tick fingerprints the manifests (<c>&lt;root&gt;/*/GAME.json</c> path + mtime +
    /// size) and the <c>.kbg</c> packages sitting in the root, and only triggers the normal debounced
    /// rescan when the fingerprint changed — so an idle folder costs one cheap directory enumeration
    /// per tick and produces no log noise. Runs alongside the watcher, which keeps its sub-second
    /// latency where it does work.
    /// </summary>
    public void StartPolling(TimeSpan interval)
    {
        if (_poll is not null || interval <= TimeSpan.Zero) return;

        _pollFingerprint = ComputeFingerprint();
        _poll = new Timer(_ =>
        {
            try
            {
                var fingerprint = ComputeFingerprint();
                if (fingerprint == _pollFingerprint) return;
                _pollFingerprint = fingerprint;
                ScheduleRescan();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Games folder poll failed.");
            }
        }, null, interval, interval);
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Polling {Path} every {Interval} for game changes (bind-mount-safe hot-reload).",
            PrimaryRoot, interval);
    }

    // Fingerprints the two things that can introduce or change a game in the primary root: a game
    // folder's manifest, and a .kbg package file. Assets are read from disk per request anyway, so
    // only a manifest add/remove/edit needs rediscovery.
    //
    // The .kbg half is load-bearing, not a nicety: dropping a package creates no directory and
    // touches no GAME.json, and on a Docker bind mount this poll is the ONLY signal that ever fires
    // (host file events don't cross the mount). Without it, packages would never install there.
    private string ComputeFingerprint()
    {
        if (!Directory.Exists(PrimaryRoot)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(PrimaryRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = new FileInfo(Path.Combine(dir, "GAME.json"));
            if (!manifest.Exists) continue;
            sb.Append(dir).Append('|').Append(manifest.LastWriteTimeUtc.Ticks).Append('|').Append(manifest.Length).Append('\n');
        }
        foreach (var file in Directory.EnumerateFiles(PrimaryRoot, "*" + GamePackage.Extension)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            sb.Append(file).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.Length).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Queues a debounced rediscovery. This is the ONLY way anything outside the class should ask for
    /// a rescan: <see cref="Discover"/> has no mutual exclusion and publishes by reference, so two
    /// concurrent runs could let the older scan win the swap and hide a just-installed game. Routing
    /// every trigger through the single debounce timer keeps that impossible.
    /// </summary>
    public void ScheduleRescan()
    {
        lock (_debounceGate)
        {
            if (_disposed) return; // don't resurrect the debounce timer during/after shutdown
            _debounce ??= new Timer(_ =>
            {
                try { Discover(); }
                catch (Exception ex) { _logger.LogError(ex, "Hot-reload rescan failed."); }
            });
            _debounce.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        // Dispose the debounce timer under the same gate that creates it, so a rescan scheduled
        // concurrently with shutdown can't leak a freshly-created timer past Dispose.
        lock (_debounceGate)
        {
            _disposed = true;
            _debounce?.Dispose();
        }
        _poll?.Dispose();
    }
}
