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
    public void Same_file_returns_the_same_prepared_module_and_reparses_on_change()
    {
        var path = Path.Combine(_dir, "authority.js");
        File.WriteAllText(path, "export function createAuthority() { return { value: () => 'v1' }; }");

        var cache = new AuthorityModuleCache();
        var first = cache.Get(path);
        var second = cache.Get(path);

        // Cache hit: byte-identical file yields the same prepared module (no re-parse).
        Assert.Equal(first, second);
        Assert.Equal("v1", Run(first));

        // Freshness: a changed file (different length + a later write time) is re-parsed.
        File.WriteAllText(path, "export function createAuthority() { return { value: () => 'v2-longer' }; }");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        var third = cache.Get(path);
        Assert.Equal("v2-longer", Run(third));
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
