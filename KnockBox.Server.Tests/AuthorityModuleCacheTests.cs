using Acornima.Ast;
using Jint;
using KnockBox.Server.Games;
using Xunit;

namespace KnockBox.Server.Tests;

public class AuthorityModuleCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-modcache-" + Guid.NewGuid().ToString("N"));

    public AuthorityModuleCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    // The idle window is measured against the cache's own clock, so the eviction tests drive time rather
    // than sleeping through it. Start well past DateTimeOffset default so "now minus a window" stays sane.
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static readonly IReadOnlySet<string> NothingInUse = new HashSet<string>(StringComparer.Ordinal);
    private static IReadOnlySet<string> InUse(params string[] paths) =>
        new HashSet<string>(paths, StringComparer.Ordinal);

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    // Baked into every regex literal in the module at parse time; the real caller passes the call budget.
    // Nothing here uses a regex, so the value only has to be something Acornima accepts.
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(250);

    // Builds an engine from a prepared module and returns the value createAuthority().value() yields,
    // proving the shared prepared module is usable and its per-engine state is isolated.
    private static string Run(Prepared<Module> prepared)
    {
        var engine = new Engine(o => o.Strict());
        engine.Modules.Add("authority", b => b.AddModule(prepared));
        var ns = engine.Modules.Import("authority");
        var inst = engine.Call(ns.Get("createAuthority")).AsObject();
        return engine.Call(inst.Get("value")).AsString();
    }

    [Fact]
    public void Cache_hit_does_not_reparse_and_change_triggers_reparse()
    {
        var path = Path.Combine(_dir, "authority.js");
        const string v1 = "export function createAuthority() { return { value: () => 'aaa' }; }";
        File.WriteAllText(path, v1);
        var stamp = File.GetLastWriteTimeUtc(path);

        var cache = new AuthorityModuleCache(_clock);
        Assert.Equal("aaa", Run(cache.Get(path, RegexBudget)));

        // Cache hit proven behaviorally: rewrite with a DIFFERENT body of the SAME length and reset the
        // mtime, so the (mtime, length) fingerprint is identical. If Get re-read/re-parsed we'd see
        // 'bbb'; the cache must return the already-parsed module, so it still yields 'aaa'.
        const string v1SameLen = "export function createAuthority() { return { value: () => 'bbb' }; }";
        Assert.Equal(v1.Length, v1SameLen.Length); // guard: identical fingerprint length
        File.WriteAllText(path, v1SameLen);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("aaa", Run(cache.Get(path, RegexBudget)));

        // Freshness: a changed file (different length + a later write time) is re-parsed.
        File.WriteAllText(path, "export function createAuthority() { return { value: () => 'v2-longer' }; }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal("v2-longer", Run(cache.Get(path, RegexBudget)));
    }

    [Fact]
    public void Prune_drops_entries_whose_path_is_not_live()
    {
        var kept = Path.Combine(_dir, "kept.js");
        var removed = Path.Combine(_dir, "removed.js");
        File.WriteAllText(kept, "export function createAuthority() { return { value: () => 'kept' }; }");
        File.WriteAllText(removed, "export function createAuthority() { return { value: () => 'removed' }; }");

        var cache = new AuthorityModuleCache(_clock);
        cache.Get(kept, RegexBudget);
        cache.Get(removed, RegexBudget);

        // Prune with only `kept` live. The `removed` entry is dropped; deleting its file afterward and
        // re-Getting `kept` proves `kept` survived (no re-read needed for a cache hit).
        cache.Prune(new HashSet<string>(new[] { kept }, StringComparer.Ordinal));
        File.Delete(removed);
        Assert.Equal("kept", Run(cache.Get(kept, RegexBudget)));
    }

    [Fact]
    public void One_prepared_module_drives_independent_engines()
    {
        var path = Path.Combine(_dir, "counter.js");
        File.WriteAllText(path, "export function createAuthority() { let n = 0; return { value: () => String(++n) }; }");

        var prepared = new AuthorityModuleCache(_clock).Get(path, RegexBudget);
        // Each engine gets its own module instance/state, so both first calls return "1".
        Assert.Equal("1", Run(prepared));
        Assert.Equal("1", Run(prepared));
    }

    // ── Idle eviction ────────────────────────────────────────────────────────
    // Nothing used to drop a parsed module while its game stayed in the catalog, so a game played once at
    // boot held its AST for the process lifetime. EvictIdle is the answer; these pin the three things that
    // can go wrong with it.

    // Writes a module whose createAuthority().value() returns `body`, and returns its path.
    private string WriteModule(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $"export function createAuthority() {{ return {{ value: () => '{body}' }}; }}");
        return path;
    }

    [Fact]
    public void An_idle_module_is_dropped_once_nothing_has_used_it_for_the_window()
    {
        var path = WriteModule("idle.js", "idle");
        var cache = new AuthorityModuleCache(_clock);
        cache.Get(path, RegexBudget);
        Assert.Equal(1, cache.Count);

        // One tick short of the window keeps it: the boundary is >=, and an off-by-one here would evict a
        // module a lobby is about to be started against.
        _clock.Advance(Window - TimeSpan.FromTicks(1));
        Assert.Empty(cache.EvictIdle(NothingInUse, Window));
        Assert.Equal(1, cache.Count);

        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(new[] { path }, cache.EvictIdle(NothingInUse, Window));
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, cache.Evicted);
    }

    [Fact]
    public void A_module_a_live_lobby_is_using_is_never_dropped_however_long_it_sits()
    {
        var path = WriteModule("busy.js", "busy");
        var cache = new AuthorityModuleCache(_clock);
        cache.Get(path, RegexBudget);

        // Get runs ONCE per lobby, at Initialize — so a game with fifty live lobbies carries exactly the
        // same last-used stamp as one nobody has touched since boot. The in-use set has to REFRESH that
        // stamp, not merely skip the entry, or the busiest game on the server is the first evicted.
        //
        // Two sweeps, deliberately: one proves only that the sweep skipped it. The second, after another
        // full window has passed with no Get in between, can only pass if the first sweep re-stamped it.
        _clock.Advance(Window * 10);
        Assert.Empty(cache.EvictIdle(InUse(path), Window));
        Assert.Equal(1, cache.Count);

        _clock.Advance(Window * 10);
        Assert.Empty(cache.EvictIdle(InUse(path), Window));
        Assert.Equal(1, cache.Count);

        // And once the last lobby goes away it becomes evictable on the normal schedule.
        _clock.Advance(Window);
        Assert.Equal(new[] { path }, cache.EvictIdle(NothingInUse, Window));
    }

    [Fact]
    public void A_zero_window_keeps_a_module_for_the_process_lifetime()
    {
        var path = WriteModule("forever.js", "forever");
        var cache = new AuthorityModuleCache(_clock);
        cache.Get(path, RegexBudget);

        _clock.Advance(TimeSpan.FromDays(365));
        Assert.Empty(cache.EvictIdle(NothingInUse, TimeSpan.Zero));
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.Evicted);
    }

    [Fact]
    public void An_evicted_module_is_reparsed_on_the_next_lobby()
    {
        var path = Path.Combine(_dir, "swap.js");
        const string v1 = "export function createAuthority() { return { value: () => 'aaa' }; }";
        File.WriteAllText(path, v1);
        var stamp = File.GetLastWriteTimeUtc(path);

        var cache = new AuthorityModuleCache(_clock);
        Assert.Equal("aaa", Run(cache.Get(path, RegexBudget)));

        _clock.Advance(Window);
        Assert.Single(cache.EvictIdle(NothingInUse, Window));

        // The same trick Cache_hit_does_not_reparse uses, inverted. Rewrite a DIFFERENT body at an
        // IDENTICAL (mtime, length) fingerprint: a surviving entry would still answer 'aaa', so seeing
        // 'bbb' can only mean the entry really left the dictionary and the file was read again.
        const string v1SameLen = "export function createAuthority() { return { value: () => 'bbb' }; }";
        Assert.Equal(v1.Length, v1SameLen.Length);
        File.WriteAllText(path, v1SameLen);
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal("bbb", Run(cache.Get(path, RegexBudget)));
    }

    [Fact]
    public void A_sweep_evicts_only_the_idle_modules_and_counts_what_it_dropped()
    {
        var idle = WriteModule("a.js", "a");
        var busy = WriteModule("b.js", "b");
        var alsoIdle = WriteModule("c.js", "c");

        var cache = new AuthorityModuleCache(_clock);
        cache.Get(idle, RegexBudget);
        cache.Get(busy, RegexBudget);
        cache.Get(alsoIdle, RegexBudget);
        Assert.Equal(3, cache.Count);

        _clock.Advance(Window);
        var dropped = cache.EvictIdle(InUse(busy), Window);

        Assert.Equal(new[] { idle, alsoIdle }.Order(), dropped.Order());
        Assert.Equal(1, cache.Count);
        Assert.Equal(2, cache.Evicted);

        // Cumulative, never reset — the AuthorityMetrics/RelayMetrics convention.
        _clock.Advance(Window);
        cache.EvictIdle(NothingInUse, Window);
        Assert.Equal(3, cache.Evicted);
        Assert.Equal(0, cache.Count);
    }
}
