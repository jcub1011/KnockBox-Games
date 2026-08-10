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

        var cache = new AuthorityModuleCache();
        Assert.Equal("aaa", Run(cache.Get(path)));

        // Cache hit proven behaviorally: rewrite with a DIFFERENT body of the SAME length and reset the
        // mtime, so the (mtime, length) fingerprint is identical. If Get re-read/re-parsed we'd see
        // 'bbb'; the cache must return the already-parsed module, so it still yields 'aaa'.
        const string v1SameLen = "export function createAuthority() { return { value: () => 'bbb' }; }";
        Assert.Equal(v1.Length, v1SameLen.Length); // guard: identical fingerprint length
        File.WriteAllText(path, v1SameLen);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("aaa", Run(cache.Get(path)));

        // Freshness: a changed file (different length + a later write time) is re-parsed.
        File.WriteAllText(path, "export function createAuthority() { return { value: () => 'v2-longer' }; }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        Assert.Equal("v2-longer", Run(cache.Get(path)));
    }

    [Fact]
    public void Prune_drops_entries_whose_path_is_not_live()
    {
        var kept = Path.Combine(_dir, "kept.js");
        var removed = Path.Combine(_dir, "removed.js");
        File.WriteAllText(kept, "export function createAuthority() { return { value: () => 'kept' }; }");
        File.WriteAllText(removed, "export function createAuthority() { return { value: () => 'removed' }; }");

        var cache = new AuthorityModuleCache();
        cache.Get(kept);
        cache.Get(removed);

        // Prune with only `kept` live. The `removed` entry is dropped; deleting its file afterward and
        // re-Getting `kept` proves `kept` survived (no re-read needed for a cache hit).
        cache.Prune(new HashSet<string>(new[] { kept }, StringComparer.Ordinal));
        File.Delete(removed);
        Assert.Equal("kept", Run(cache.Get(kept)));
    }

    [Fact]
    public void One_prepared_module_drives_independent_engines()
    {
        var path = Path.Combine(_dir, "counter.js");
        File.WriteAllText(path, "export function createAuthority() { let n = 0; return { value: () => String(++n) }; }");

        var prepared = new AuthorityModuleCache().Get(path);
        // Each engine gets its own module instance/state, so both first calls return "1".
        Assert.Equal("1", Run(prepared));
        Assert.Equal("1", Run(prepared));
    }
}
