using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using KnockBox.Contracts;

namespace KnockBox.Server.Games.Words;

/// <summary>
/// Default <see cref="IAuthorityWordService"/> — a DI singleton. Holds three maps:
/// <list type="bullet">
/// <item>a stat memo <c>(path|mtime|length) → contentHash</c> so an unchanged file is not re-read and
/// re-hashed on every lobby start;</item>
/// <item>the memory-dedup point <c>(contentHash|caseInsensitive) → WordPoolSet</c> — <b>keyed purely on
/// file CONTENT, not name/path</b>, so different games shipping byte-identical dictionaries (any file
/// name) build and store the structure exactly once. Immutable data, one copy;</item>
/// <item>a fast handle map <c>(gameId, dictKey) → PoolEntry</c> the <c>kb.words</c> bridge resolves
/// against; the entry also records the content/stat keys so <see cref="Prune"/> can reclaim the other
/// two maps.</item>
/// </list>
/// Mirrors the intent of <c>WordListService.RegisterCustomPool</c> from the sibling repo, but sourced
/// from per-game files and content-deduped.
/// </summary>
public sealed class AuthorityWordService(ILogger<AuthorityWordService> logger) : IAuthorityWordService
{
    // A resolved handle plus the keys into the two backing maps, so Prune can drop the pool/stat entry
    // once no handle references it.
    private readonly record struct PoolEntry(IWordPool Pool, string ContentKey, string StatKey);

    private readonly ConcurrentDictionary<(string GameId, string DictKey), PoolEntry> _pools = new();
    private readonly ConcurrentDictionary<string, string> _contentHashByPath = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<IWordPool>> _poolByContent = new(StringComparer.Ordinal);

    public void Load(string gameId, string dictKey, string absolutePath, bool caseInsensitive)
    {
        var info = new FileInfo(absolutePath);
        if (!info.Exists)
            throw new FileNotFoundException($"Word dictionary not found: {absolutePath}", absolutePath);

        // Stat memo: for an unchanged physical file we already hashed, skip the read + hash entirely.
        var statKey = $"{absolutePath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}";
        byte[]? bytes = null;
        if (!_contentHashByPath.TryGetValue(statKey, out var contentHash))
        {
            bytes = File.ReadAllBytes(absolutePath);
            contentHash = Convert.ToHexString(SHA256.HashData(bytes));
            _contentHashByPath[statKey] = contentHash;
        }

        // Dedup on CONTENT (+ the case flag, which changes the built structure). The file's name/path
        // is deliberately NOT part of this key: identical bytes under any name share one pool. The Lazy
        // guarantees the (potentially multi-MB) structure is built exactly once even when two lobbies of
        // the same cold game start concurrently.
        var contentKey = $"{contentHash}|{caseInsensitive}";
        var lazy = _poolByContent.GetOrAdd(contentKey, _ => new Lazy<IWordPool>(() =>
        {
            var data = bytes ?? File.ReadAllBytes(absolutePath); // memo hit but not yet built for this flag
            var built = WordPoolSet.Build(SplitLines(data), caseInsensitive);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "Loaded word dictionary '{Dict}' for game '{Game}' ({Words} words) from {Path}",
                    dictKey, gameId, built.TotalWordCount, absolutePath);
            return built;
        }));

        _pools[Key(gameId, dictKey)] = new PoolEntry(lazy.Value, contentKey, statKey);
    }

    public IWordPool? Get(string gameId, string dictKey)
        => _pools.TryGetValue(Key(gameId, dictKey), out var entry) ? entry.Pool : null;

    public void Prune(IReadOnlyDictionary<string, GameCatalog.GameLocation> games)
    {
        // The (gameId, dictKey) handles still declared by the live catalog.
        var live = new HashSet<(string, string)>();
        foreach (var location in games.Values)
            if (location.Manifest.AuthorityWords is { Count: > 0 } decls)
                foreach (var dictKey in decls.Keys)
                    live.Add(Key(location.Manifest.Id, dictKey));

        // Drop handles for games/dicts no longer declared.
        foreach (var handleKey in _pools.Keys)
            if (!live.Contains(handleKey))
                _pools.TryRemove(handleKey, out _);

        // Reclaim content/stat entries no longer referenced by any surviving handle. This is
        // self-healing under a concurrent Load: a handle re-added after this snapshot just rebuilds its
        // pool on the next GetOrAdd miss. An edited-in-place file's stale version is reclaimed on the
        // first Discovered after a lobby has reloaded the new version (which repoints the handle's
        // Content/Stat keys).
        var liveContent = new HashSet<string>(StringComparer.Ordinal);
        var liveStats = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _pools.Values)
        {
            liveContent.Add(entry.ContentKey);
            liveStats.Add(entry.StatKey);
        }
        foreach (var contentKey in _poolByContent.Keys)
            if (!liveContent.Contains(contentKey))
                _poolByContent.TryRemove(contentKey, out _);
        foreach (var statKey in _contentHashByPath.Keys)
            if (!liveStats.Contains(statKey))
                _contentHashByPath.TryRemove(statKey, out _);
    }

    // Decode the file (ASCII words; UTF-8 is a superset so BOM/non-ASCII survive to be skipped by
    // WordPool.Build) and split into lines. Build trims each line and drops blanks, so trailing \r and
    // whitespace are handled there.
    private static IEnumerable<string> SplitLines(byte[] data)
        => Encoding.UTF8.GetString(data).Split('\n');

    // Game ids are matched case-insensitively everywhere (GameCatalog uses OrdinalIgnoreCase); the
    // dictionary key is the author's literal, matched exactly against what the module passes to
    // kb.words.*.
    private static (string, string) Key(string gameId, string dictKey)
        => (gameId.ToLowerInvariant(), dictKey);
}
