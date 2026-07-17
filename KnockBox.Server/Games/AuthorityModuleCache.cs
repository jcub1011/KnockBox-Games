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
/// </summary>
public sealed class AuthorityModuleCache
{
    private sealed record Entry(long MTimeTicks, long Length, Prepared<Module> Prepared);

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
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
}
