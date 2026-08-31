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
/// Two things drop entries, for two different reasons, and they stay separate methods:
/// <list type="bullet">
/// <item><see cref="Prune"/> — the game left the catalog. Wired to <c>GameCatalog.Discovered</c> in
/// Program.cs, exactly like <see cref="Words.AuthorityWordService"/>, so a removed game doesn't leave
/// its parsed AST cached for the process lifetime.</item>
/// <item><see cref="EvictIdle"/> — the game is still installed but nobody is playing it. Driven by
/// <c>ServerAuthorityManager.SweepModuleCache</c> off a timer, with the window an operator-editable
/// setting.</item>
/// </list>
///
/// <b>Neither one disposes anything, and the distinction matters when reading a log line.</b>
/// <see cref="Prepared{T}"/> is not <see cref="IDisposable"/>: eviction removes a dictionary entry and
/// nothing more. The AST becomes GC-<em>eligible</em> only once no live <c>JsAuthorityRuntime</c> still
/// references it — and each engine holds its own reference from <c>AddModule</c>, so evicting a module
/// that lobbies are currently running would reclaim nothing at all while costing the next lobby a
/// re-parse. That is exactly why <see cref="EvictIdle"/> takes an in-use set rather than trusting a
/// timestamp. Even for a genuinely idle module the honest claim is "stopped holding the parsed AST",
/// never "returned N bytes at time T": with Server GC, DATAS and <c>System.GC.ConserveMemory=5</c> the
/// bytes come back after a gen2 collection and RSS falls behind that.
/// </summary>
public sealed class AuthorityModuleCache(TimeProvider time)
{
    // A class rather than a record because LastUsedTicks is mutated in place: a record would allocate a
    // replacement entry on every cache hit and every sweep touch. Volatile because Get (any lobby-creating
    // thread) and EvictIdle (the sweep timer) both write it without holding _lock; the only race is two
    // writers stamping near-identical values, and either of them is correct.
    private sealed class Entry(long mtimeTicks, long length, Prepared<Module> prepared, long lastUsedTicks)
    {
        public long MTimeTicks { get; } = mtimeTicks;
        public long Length { get; } = length;
        public Prepared<Module> Prepared { get; } = prepared;

        private long _lastUsedTicks = lastUsedTicks;
        public long LastUsedTicks
        {
            get => Volatile.Read(ref _lastUsedTicks);
            set => Volatile.Write(ref _lastUsedTicks, value);
        }
    }

    // Keyed by full module path. Ordinal (not OrdinalIgnoreCase): the deployment target is Linux,
    // where paths are case-sensitive, and the sibling AuthorityWordService keys its path caches the
    // same way.
    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _evicted;

    /// <summary>Modules currently held. Reported by the admin portal beside the idle-window setting, which
    /// is the only place the question "is my window doing anything?" gets asked.</summary>
    public int Count => _cache.Count;

    /// <summary>Modules dropped by <see cref="EvictIdle"/> since the server started. Cumulative and never
    /// reset — the <c>AuthorityMetrics</c> / <c>RelayMetrics</c> convention: a rate needs two samples and
    /// that is the reader's job.</summary>
    public long Evicted => Interlocked.Read(ref _evicted);

    /// <summary>Returns the shared prepared module for <paramref name="modulePath"/>, parsing it once
    /// and re-parsing only when the file's mtime/length changes. Parse errors propagate (the caller
    /// wraps them as <see cref="AuthorityLoadException"/>).</summary>
    /// <param name="regexTimeout">
    /// Budget for a single regex match, baked into every regex literal in the module.
    /// <b>This has to be a preparation option, not an engine one.</b> A literal like <c>/(a+)+b/</c> is
    /// compiled to a <see cref="System.Text.RegularExpressions.Regex"/> here, at parse time, carrying
    /// whatever timeout it was given — so the engine's own <c>Constraints.RegexTimeout</c> never gets a say
    /// over it, and Acornima's default leaves it at ten seconds. Jint checks <c>TimeoutInterval</c> between
    /// statements and so cannot interrupt a match in progress, which makes an unbounded literal a way for
    /// one module to hold its lobby's drain task far past the call budget. The engine-level interval is set
    /// too, for regexes the module builds at runtime with <c>new RegExp(...)</c>.
    /// Not part of the cache key: it comes from <c>AuthorityCallTimeout</c>, which is server-wide and
    /// startup-only, so every caller passes the same value for the life of the process.
    /// </param>
    public Prepared<Module> Get(string modulePath, TimeSpan regexTimeout)
    {
        var info = new FileInfo(modulePath);
        var mtime = info.LastWriteTimeUtc.Ticks;
        var length = info.Length;
        var now = time.GetUtcNow().UtcTicks;

        if (_cache.TryGetValue(modulePath, out var hit) && hit.MTimeTicks == mtime && hit.Length == length)
        {
            hit.LastUsedTicks = now;
            return hit.Prepared;
        }

        // Parse under a lock so concurrent lobby creations of a not-yet-cached game parse once, not once
        // per racing thread. Parsing is infrequent (lobby creation) so a single lock is fine.
        lock (_lock)
        {
            if (_cache.TryGetValue(modulePath, out hit) && hit.MTimeTicks == mtime && hit.Length == length)
            {
                hit.LastUsedTicks = now;
                return hit.Prepared;
            }

            var preparation = ModulePreparationOptions.Default with
            {
                ParsingOptions = ModulePreparationOptions.Default.ParsingOptions with
                {
                    RegexTimeout = regexTimeout,
                },
            };

            var prepared = Engine.PrepareModule(File.ReadAllText(modulePath), modulePath, preparation);
            _cache[modulePath] = new Entry(mtime, length, prepared, now);
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

    /// <summary>
    /// Drops modules that no lobby has used for <paramref name="idleAfter"/>, and returns the paths dropped
    /// so the caller can log them. An <paramref name="idleAfter"/> of zero or less means "keep for the
    /// process lifetime" and does nothing.
    /// </summary>
    /// <param name="inUse">Module paths of the lobbies running right now, from the authority manager's
    /// actor map. These have their idle clock <b>refreshed</b> rather than merely skipped, which is the
    /// whole reason this method takes the set: <see cref="Get"/> runs once per lobby at startup, so a game
    /// with fifty live lobbies carries exactly the same last-used stamp as one nobody has touched since.
    /// Without the refresh, the busiest game on the server would be the first evicted.</param>
    /// <remarks>Keyed on the same comparer as the cache. Removal is <b>value-comparing</b>: a
    /// <see cref="Get"/> racing this sweep may already have replaced the entry with a freshly parsed one,
    /// and dropping <em>that</em> would charge the lobby which just paid for the parse a second one.</remarks>
    public IReadOnlyList<string> EvictIdle(IReadOnlySet<string> inUse, TimeSpan idleAfter)
    {
        if (idleAfter <= TimeSpan.Zero) return [];

        var now = time.GetUtcNow().UtcTicks;
        var cutoff = now - idleAfter.Ticks;
        List<string>? dropped = null;

        foreach (var (path, entry) in _cache)
        {
            if (inUse.Contains(path)) { entry.LastUsedTicks = now; continue; }
            if (entry.LastUsedTicks > cutoff) continue;
            if (!_cache.TryRemove(new KeyValuePair<string, Entry>(path, entry))) continue;
            Interlocked.Increment(ref _evicted);
            (dropped ??= []).Add(path);
        }

        return dropped is null ? [] : dropped;
    }
}
