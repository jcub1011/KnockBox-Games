using System.Text.Json;
using KnockBox.Contracts;

namespace KnockBox.Server.Games;

/// <summary>
/// Discovers HTML5 games at startup by scanning <c>games/*/GAME.json</c>. The server never
/// runs game logic — it only needs the manifest (id, entry, player counts) to list games and
/// create lobbies. In-memory only; re-discovered each boot.
/// </summary>
public sealed class GameCatalog(string gamesRoot, ILogger<GameCatalog> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, GameManifest> _games = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<GameManifest> Games => _games.Values;

    public bool TryGet(string id, out GameManifest manifest) => _games.TryGetValue(id, out manifest!);

    public void Discover()
    {
        _games.Clear();

        if (!Directory.Exists(gamesRoot))
        {
            logger.LogWarning("Games folder not found at {Path}; no games discovered.", gamesRoot);
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

                _games[manifest.Id] = manifest;
                logger.LogInformation("Discovered game '{Id}' ({Name}) from {Dir}", manifest.Id, manifest.Name, dir);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load manifest at {Path}", manifestPath);
            }
        }

        logger.LogInformation("Game catalog ready: {Count} game(s) [{Ids}]", _games.Count, string.Join(", ", _games.Keys));
    }
}
