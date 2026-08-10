using KnockBox.Contracts;

namespace KnockBox.Server.Games.Words;

/// <summary>
/// Loads and shares the immutable word dictionaries declared by server-authority games
/// (<c>GAME.json</c> <c>authorityWords</c>). One copy of each dictionary lives on the CLR heap and is
/// shared by every lobby engine of a game — never duplicated into a Jint engine's per-invocation
/// budget. See docs/SERVER_AUTHORITY_DESIGN.md and the plan for the <c>kb.words</c> capability.
/// </summary>
public interface IAuthorityWordService
{
    /// <summary>
    /// Loads the line-delimited dictionary at <paramref name="absolutePath"/> once and registers it
    /// under <c>(gameId, dictKey)</c>. Idempotent and thread-safe; dictionaries with identical
    /// content (same path, mtime, length, and <paramref name="caseInsensitive"/>) share one built
    /// structure across games.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    void Load(string gameId, string dictKey, string absolutePath, bool caseInsensitive);

    /// <summary>The pool registered under <c>(gameId, dictKey)</c>, or null if none.</summary>
    IWordPool? Get(string gameId, string dictKey);

    /// <summary>
    /// Reclaims pools/handles no longer backed by the live catalog. Driven by
    /// <c>GameCatalog.Discovered</c> so the shared structures don't accumulate stale copies as games
    /// are added, removed, or their dictionaries edited in place over a long-running process.
    /// </summary>
    void Prune(IReadOnlyDictionary<string, GameCatalog.GameLocation> games);
}
