using System.Collections.Concurrent;

namespace KnockBox.Server.Games.Words;

/// <summary>
/// Default <see cref="IAuthorityWordService"/> — a DI singleton. Holds two maps:
/// <list type="bullet">
/// <item>a fingerprint cache <c>(path|mtime|length|caseInsensitive) → WordPoolSet</c>, so two games
/// shipping the same dictionary file build and store it once;</item>
/// <item>a fast handle map <c>(gameId, dictKey) → IWordPool</c> the <c>kb.words</c> bridge resolves
/// against.</item>
/// </list>
/// Mirrors the intent of <c>WordListService.RegisterCustomPool</c> from the sibling repo, but sourced
/// from per-game files rather than baked-in dictionaries.
/// </summary>
public sealed class AuthorityWordService(ILogger<AuthorityWordService> logger) : IAuthorityWordService
{
    private readonly ConcurrentDictionary<(string GameId, string DictKey), IWordPool> _pools = new();
    private readonly ConcurrentDictionary<string, IWordPool> _byFingerprint = new(StringComparer.Ordinal);

    public void Load(string gameId, string dictKey, string absolutePath, bool caseInsensitive)
    {
        var info = new FileInfo(absolutePath);
        if (!info.Exists)
            throw new FileNotFoundException($"Word dictionary not found: {absolutePath}", absolutePath);

        // The flag is part of the identity: the same file built case-sensitive vs -insensitive is a
        // different structure, so it must not share a cache slot.
        var fingerprint = $"{absolutePath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{caseInsensitive}";
        var pool = _byFingerprint.GetOrAdd(fingerprint, _ =>
        {
            var built = WordPoolSet.Build(File.ReadLines(absolutePath), caseInsensitive);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                    "Loaded word dictionary '{Dict}' for game '{Game}' ({Words} words) from {Path}",
                    dictKey, gameId, built.TotalWordCount, absolutePath);
            return built;
        });

        _pools[Key(gameId, dictKey)] = pool;
    }

    public IWordPool? Get(string gameId, string dictKey)
        => _pools.TryGetValue(Key(gameId, dictKey), out var pool) ? pool : null;

    // Game ids are matched case-insensitively everywhere (GameCatalog uses OrdinalIgnoreCase); the
    // dictionary key is the author's literal, matched exactly against what the module passes to
    // kb.words.*.
    private static (string, string) Key(string gameId, string dictKey)
        => (gameId.ToLowerInvariant(), dictKey);
}
