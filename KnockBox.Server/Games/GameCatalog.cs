using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// Discovers HTML5 games by scanning <c>games/*/GAME.json</c>. The server never runs game logic —
/// it only needs the manifest (id, entry, player counts) to list games and create lobbies.
/// In-memory only. A <see cref="FileSystemWatcher"/> re-discovers on change so a server manager can
/// drop in (or remove) a game folder with no restart and no code.
/// </summary>
public sealed class GameCatalog(string gamesRoot, ILogger<GameCatalog> logger) : IDisposable
{
    // Swapped atomically by Discover(). Readers take the reference once and enumerate a stable
    // snapshot, so a concurrent rebuild can never expose a half-built catalog (no lock needed).
    private volatile IReadOnlyDictionary<string, GameManifest> _games =
        new Dictionary<string, GameManifest>(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly Lock _debounceGate = new();
    private bool _disposed;
    private Timer? _poll;
    // Written by the poll timer thread and (once) by the startup thread; volatile guarantees the
    // timer callback sees the seeded value on weakly-ordered architectures (ARM).
    private volatile string _pollFingerprint = "";

    public IReadOnlyCollection<GameManifest> Games => [.. _games.Values];

    public bool TryGet(string id, out GameManifest manifest) => _games.TryGetValue(id, out manifest!);

    /// <summary>Scans the games folder and atomically swaps in the rebuilt catalog.</summary>
    public void Discover()
    {
        var next = new Dictionary<string, GameManifest>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(gamesRoot))
        {
            logger.LogWarning("Games folder not found at {Path}; no games discovered.", gamesRoot);
            _games = next;
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(gamesRoot))
        {
            var manifestPath = Path.Combine(dir, "GAME.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var manifest = JsonSerializer.Deserialize(File.ReadAllText(manifestPath), KnockBoxProtocolContext.Default.GameManifest);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    logger.LogWarning("Skipping {Path}: empty or invalid manifest.", manifestPath);
                    continue;
                }

                // Assets are served at /games/{id}/…, so the folder name must equal the id or loads 404.
                var folderName = new DirectoryInfo(dir).Name;
                if (!string.Equals(folderName, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Skipping game '{Id}': folder name '{Folder}' must match the manifest id.", manifest.Id, folderName);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Entry))
                {
                    logger.LogWarning("Skipping game '{Id}': manifest has no entry.", manifest.Id);
                    continue;
                }

                // The entry must resolve to a file inside the game folder — never escape it (path traversal).
                var dirFull = Path.GetFullPath(dir);
                var entryFull = Path.GetFullPath(Path.Combine(dir, manifest.Entry));
                var dirPrefix = dirFull.EndsWith(Path.DirectorySeparatorChar) ? dirFull : dirFull + Path.DirectorySeparatorChar;
                if (!entryFull.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Skipping game '{Id}': entry '{Entry}' escapes the game folder.", manifest.Id, manifest.Entry);
                    continue;
                }
                if (!File.Exists(entryFull))
                {
                    logger.LogWarning("Skipping game '{Id}': entry file '{Entry}' not found.", manifest.Id, manifest.Entry);
                    continue;
                }

                next[manifest.Id] = manifest;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Discovered game '{Id}' ({Name}) from {Dir}", manifest.Id, manifest.Name, dir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load manifest at {Path}", manifestPath);
            }
        }

        _games = next; // atomic publish
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Game catalog ready: {Count} game(s) [{Ids}]", next.Count, string.Join(", ", next.Keys));
    }

    /// <summary>
    /// Watches the games folder and re-runs <see cref="Discover"/> ~500 ms after the last change,
    /// so a burst of file events (a folder being copied in) triggers a single rebuild.
    /// </summary>
    public void StartWatching()
    {
        if (_watcher is not null || !Directory.Exists(gamesRoot)) return;

        _watcher = new FileSystemWatcher(gamesRoot)
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
            logger.LogWarning(e.GetException(), "Game folder watcher error; forcing a rescan.");
            ScheduleRescan();
        };

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Watching {Path} for game changes (hot-reload enabled).", gamesRoot);
    }

    /// <summary>
    /// Polling safety net for environments where <see cref="FileSystemWatcher"/> is unreliable —
    /// chiefly Docker bind mounts on Docker Desktop, where host file events never reach the
    /// container. Each tick fingerprints the manifests (<c>games/*/GAME.json</c> path + mtime +
    /// size) and only triggers the normal debounced rescan when the fingerprint changed, so an idle
    /// folder costs one cheap directory enumeration per tick and produces no log noise. Runs
    /// alongside the watcher, which keeps its sub-second latency where it does work.
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
                logger.LogError(ex, "Games folder poll failed.");
            }
        }, null, interval, interval);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Polling {Path} every {Interval} for game changes (bind-mount-safe hot-reload).",
            gamesRoot, interval);
    }

    // Only manifests are fingerprinted: assets are read from disk per request anyway, so only a
    // manifest add/remove/edit needs rediscovery.
    private string ComputeFingerprint()
    {
        if (!Directory.Exists(gamesRoot)) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var dir in Directory.EnumerateDirectories(gamesRoot).Order(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = new FileInfo(Path.Combine(dir, "GAME.json"));
            if (!manifest.Exists) continue;
            sb.Append(dir).Append('|').Append(manifest.LastWriteTimeUtc.Ticks).Append('|').Append(manifest.Length).Append('\n');
        }
        return sb.ToString();
    }

    private void ScheduleRescan()
    {
        lock (_debounceGate)
        {
            if (_disposed) return; // don't resurrect the debounce timer during/after shutdown
            _debounce ??= new Timer(_ =>
            {
                try { Discover(); }
                catch (Exception ex) { logger.LogError(ex, "Hot-reload rescan failed."); }
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
