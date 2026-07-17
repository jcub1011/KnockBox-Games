using System.Collections.Concurrent;
using Acornima.Ast;
using Jint;

namespace KnockBox.Server.Games;

/// <summary>
/// Shares one parsed authority module across every lobby engine of a game, keyed by file path.
/// <see cref="Engine.PrepareModule(string, string, ModulePreparationOptions)"/> parses and
/// statically analyses the source once and returns a <see cref="Prepared{T}"/> that Jint documents
/// as reusable and thread-safe ("prepare only once and then reuse"); a per-lobby engine registers it
/// with a cheap <c>ModuleBuilder.AddModule</c> instead of re-reading and re-parsing the file. Without
/// this, N concurrent lobbies of the same game each held their own copy of the parsed AST on top of
/// the (unavoidable) per-engine realm.
///
/// Freshness is checked by last-write time + length (the same fingerprint the asset precompressor
/// uses), so a hot-reloaded module is re-parsed on the next lobby; running engines keep the copy they
/// were built with (design §11). Owned as a private field of the singleton
/// <see cref="ServerAuthorityManager"/>, so it lives for the process and is shared by every lobby.
/// The caller still enforces the module size cap before calling <see cref="Get"/> — this only
/// parses/caches.
///
/// Kept in lock-step with the catalog via <see cref="Prune"/> (wired to <c>GameCatalog.Discovered</c>
/// in Program.cs, exactly like <see cref="Words.AuthorityWordService"/>), so a removed game doesn't
/// leave its parsed AST cached for the process lifetime.
/// </summary>
public sealed class AuthorityModuleCache
{
    private sealed record Entry(long MTimeTicks, long Length, Prepared<Module> Prepared);

    // Keyed by full module path. Ordinal (not OrdinalIgnoreCase): the deployment target is Linux,
    // where paths are case-sensitive, and the sibling AuthorityWordService keys its path caches the
    // same way.
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Returns the shared prepared module for <paramref name="modulePath"/>, parsing it once
    /// and re-parsing only when the file's mtime/length changes. Parse errors propagate (the caller
    /// wraps them as <see cref="AuthorityLoadException"/>).</summary>
    public Prepared<Module> Get(string modulePath)
    {
        var info = new FileInfo(modulePath);
        var mtime = info.LastWriteTimeUtc.Ticks;
        var length = info.Length;

        if (_cache.TryGetValue(modulePath, out var hit) && hit.MTimeTicks == mtime && hit.Length == length)
            return hit.Prepared;

        // Parse under a lock so concurrent lobby creations of a not-yet-cached game parse once, not once
        // per racing thread. Parsing is infrequent (lobby creation) so a single lock is fine.
        lock (_lock)
        {
            if (_cache.TryGetValue(modulePath, out hit) && hit.MTimeTicks == mtime && hit.Length == length)
                return hit.Prepared;

            var prepared = Engine.PrepareModule(File.ReadAllText(modulePath), modulePath);
            _cache[modulePath] = new Entry(mtime, length, prepared);
            return prepared;
        }
    }

    /// <summary>Drops cached entries whose path is not in <paramref name="livePaths"/> (games removed
    /// since the last discovery), so a removed game's parsed AST doesn't linger for the process
    /// lifetime. Self-healing like <see cref="Words.AuthorityWordService"/>: a running engine already
    /// holds its own reference to the prepared module, and a path re-added after this snapshot just
    /// re-parses on the next <see cref="Get"/> miss. <paramref name="livePaths"/> must use the same
    /// comparer as the cache (<see cref="StringComparer.Ordinal"/>).</summary>
    public void Prune(IReadOnlySet<string> livePaths)
    {
        foreach (var path in _cache.Keys)
            if (!livePaths.Contains(path))
                _cache.TryRemove(path, out _);
    }
}
