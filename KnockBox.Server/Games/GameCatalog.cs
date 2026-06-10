using System.Text.Json;
using KnockBox.Contracts;

namespace KnockBox.Server.Games;

/// <summary>
/// Discovers HTML5 games by scanning <c>games/*/GAME.json</c>. The server never runs game logic —
/// it only needs the manifest (id, entry, player counts) to list games and create lobbies.
/// In-memory only. A <see cref="FileSystemWatcher"/> re-discovers on change so a server manager can
/// drop in (or remove) a game folder with no restart and no code.
/// </summary>
public sealed class GameCatalog(string gamesRoot, ILogger<GameCatalog> logger) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Swapped atomically by Discover(). Readers take the reference once and enumerate a stable
    // snapshot, so a concurrent rebuild can never expose a half-built catalog (no lock needed).
    private volatile IReadOnlyDictionary<string, GameManifest> _games =
        new Dictionary<string, GameManifest>(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly object _debounceGate = new();

    public IReadOnlyCollection<GameManifest> Games => _games.Values.ToArray();

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
                var manifest = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    logger.LogWarning("Skipping {Path}: empty or invalid manifest.", manifestPath);
                    continue;
                }

                if (!File.Exists(Path.Combine(dir, manifest.Entry)))
                {
                    logger.LogWarning("Skipping game '{Id}': entry file '{Entry}' not found.", manifest.Id, manifest.Entry);
                    continue;
                }

                next[manifest.Id] = manifest;
                logger.LogInformation("Discovered game '{Id}' ({Name}) from {Dir}", manifest.Id, manifest.Name, dir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load manifest at {Path}", manifestPath);
            }
        }

        _games = next; // atomic publish
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

        FileSystemEventHandler onChange = (_, _) => ScheduleRescan();
        _watcher.Created += onChange;
        _watcher.Changed += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, _) => ScheduleRescan();
        logger.LogInformation("Watching {Path} for game changes (hot-reload enabled).", gamesRoot);
    }

    private void ScheduleRescan()
    {
        lock (_debounceGate)
        {
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
        _debounce?.Dispose();
    }
}
