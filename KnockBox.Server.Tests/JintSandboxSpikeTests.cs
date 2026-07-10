using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Phase 0 spike for the server-authority design (docs/SERVER_AUTHORITY_DESIGN.md §3b, §14.2, §14.3):
/// pins the Jint sandbox facts the design depends on, so a Jint upgrade that changes any of them
/// fails loudly here instead of silently weakening a security boundary.
/// </summary>
public class JintSandboxSpikeTests
{
    private static Engine NewEngine(Action<Options>? extra = null) => new(o =>
    {
        o.Strict();
        extra?.Invoke(o);
    });

    private static JsValue ImportFunction(Engine engine, string moduleSource, string exportName)
    {
        engine.Modules.Add("authority", moduleSource);
        var ns = engine.Modules.Import("authority");
        return ns.Get(exportName);
    }

    // ── Constraint re-arming (§14.3) ─────────────────────────────────────────
    // There is no public whole-engine constraint reset; Engine.ExecuteWithConstraints (which
    // Call/Invoke/Evaluate route through) resets every constraint at the start of each invocation.
    // If a Jint upgrade ever changes that, the fallback is an explicit per-constraint loop:
    //   foreach via engine.Constraints.Find<T>()?.Reset() for each constraint type.

    [Fact]
    public void MaxStatements_budget_rearms_per_invocation()
    {
        var engine = NewEngine(o => o.MaxStatements(10_000));
        var spin = ImportFunction(engine, "export function spin(n) { for (let i = 0; i < n; i++) {} return n; }", "spin");

        // One over-budget call trips the constraint…
        Assert.Throws<StatementsCountOverflowException>(() => engine.Call(spin, 100_000d));

        // …but many under-budget calls whose *cumulative* statement count far exceeds the budget
        // all succeed, proving the budget is per-invocation, not per-engine-lifetime.
        for (var i = 0; i < 20; i++)
            Assert.Equal(2_000d, engine.Call(spin, 2_000d).AsNumber());
    }

    [Fact]
    public void Timeout_budget_rearms_per_invocation()
    {
        var engine = NewEngine(o => o.TimeoutInterval(TimeSpan.FromMilliseconds(200)));
        var fn = ImportFunction(engine, "export function f() { return 1; }", "f");

        Assert.Equal(1d, engine.Call(fn).AsNumber());
        // If the 200 ms window armed once at first use and never re-armed, a call made after the
        // window elapsed would throw. It must not.
        Thread.Sleep(500);
        Assert.Equal(1d, engine.Call(fn).AsNumber());
    }

    // ── Ambient time removal (§3b) ───────────────────────────────────────────

    [Fact]
    public void Date_global_can_be_deleted_and_stays_gone()
    {
        var engine = NewEngine();
        // Engine.Realm is not public in Jint 4.11, so the global is scrubbed from script: deleting a
        // *property* of globalThis is legal even in strict mode (only unqualified `delete Date` isn't).
        Assert.True(engine.Evaluate("delete globalThis.Date").AsBoolean());

        Assert.Equal("undefined", engine.Evaluate("typeof Date").AsString());

        var fn = ImportFunction(engine, "export function stamp() { return new Date().getTime(); }", "stamp");
        var ex = Assert.Throws<JavaScriptException>(() => engine.Call(fn));
        Assert.Contains("Date", ex.Message);
    }

    // ── Module isolation (§14.2): single-file only, no loader configured ─────

    [Fact]
    public void Relative_import_fails_without_a_module_loader()
    {
        var engine = NewEngine();
        engine.Modules.Add("authority", "import './other.js';\nexport const x = 1;");
        var ex = Record.Exception(() => engine.Modules.Import("authority"));
        Assert.NotNull(ex);
        // Jint 4.11 refuses outright: module *loading* is disabled unless a loader is configured
        // (EnableModules), so the relative import can't resolve to anything. Pin type + message so
        // an upgrade that starts resolving relative imports fails loudly.
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("Module loading has been disabled", ex.Message);
    }

    // ── Distinct, catchable failure taxonomy (§7) ────────────────────────────
    // Contained (engine unwound one call, still usable) vs fatal (constraint tripped) must be
    // distinguishable by catch clauses.

    [Fact]
    public void Script_throw_is_JavaScriptException_and_engine_stays_usable()
    {
        var engine = NewEngine(o => o.MaxStatements(100_000));
        var mod = """
            export function boom() { throw new Error('nope'); }
            export function ok() { return 42; }
            """;
        engine.Modules.Add("authority", mod);
        var ns = engine.Modules.Import("authority");

        var ex = Assert.Throws<JavaScriptException>(() => engine.Call(ns.Get("boom")));
        Assert.Contains("nope", ex.Message);

        // A contained throw leaves the engine consistent for the next invocation.
        Assert.Equal(42d, engine.Call(ns.Get("ok")).AsNumber());
    }

    [Fact]
    public void Infinite_loop_trips_the_timeout()
    {
        var engine = NewEngine(o => o.TimeoutInterval(TimeSpan.FromMilliseconds(150)));
        var fn = ImportFunction(engine, "export function loop() { for (;;) {} }", "loop");
        Assert.Throws<TimeoutException>(() => engine.Call(fn));
    }

    [Fact]
    public void Memory_bomb_trips_the_memory_limit()
    {
        var engine = NewEngine(o => o.LimitMemory(4 * 1024 * 1024));
        var fn = ImportFunction(engine,
            "export function bomb() { const a = []; for (;;) a.push(new Array(4096).fill(0)); }", "bomb");
        Assert.Throws<MemoryLimitExceededException>(() => engine.Call(fn));
    }

    [Fact]
    public void Runaway_recursion_trips_the_recursion_limit()
    {
        var engine = NewEngine(o => o.LimitRecursion(64));
        var fn = ImportFunction(engine, "export function rec(n) { return rec(n + 1); }", "rec");
        Assert.Throws<RecursionDepthOverflowException>(() => engine.Call(fn, 0d));
    }

    // ── Interop canary (§3b): the exact surface JsAuthorityRuntime will use ──
    // kb is a JsObject with ClrFunction members (Jint's no-reflection-marshaling path — never
    // typed-delegate SetValue overloads); args cross as JSON via Jint's own JsonParser, results
    // come back through its JsonSerializer. This is also the AOT canary: the aot CI job publishes
    // with /warnaserror, so any trim-unsafe path in this surface fails the build.

    [Fact]
    public void CreateAuthority_roundtrip_through_json_boundary()
    {
        var engine = NewEngine(o => o.MaxStatements(100_000));
        var logs = new List<string>();

        var kb = new JsObject(engine);
        kb.Set("now", new ClrFunction(engine, "now", (_, _) => 12_345d));
        kb.Set("log", new ClrFunction(engine, "log", (_, args) =>
        {
            logs.Add(args.Length > 0 ? args[0].ToString() : "");
            return JsValue.Undefined;
        }));

        var mod = """
            export function createAuthority(kb) {
              let state = null;
              return {
                init(players) { state = { count: 0, ids: players.map(p => p.id), at: kb.now() }; },
                applyIntent(fromId, action) {
                  if (action.kind !== 'inc') return null;
                  state.count += 1;
                  kb.log('count is ' + state.count);
                  return { count: state.count, by: fromId };
                },
                snapshot() { return state; },
              };
            }
            export const config = { tickHz: 0 };
            """;
        engine.Modules.Add("authority", mod);
        var ns = engine.Modules.Import("authority");

        var instance = engine.Call(ns.Get("createAuthority"), kb).AsObject();
        var parser = new JsonParser(engine);
        var serializer = new JsonSerializer(engine);

        engine.Call(instance.Get("init"), instance, [parser.Parse("""[{"id":"p1","displayName":"A"}]""")]);

        var rejected = engine.Call(instance.Get("applyIntent"), instance,
            [parser.Parse("\"p1\""), parser.Parse("""{"kind":"nope"}""")]);
        Assert.True(rejected.IsNull());

        var patch = engine.Call(instance.Get("applyIntent"), instance,
            [parser.Parse("\"p1\""), parser.Parse("""{"kind":"inc"}""")]);
        Assert.Equal("""{"count":1,"by":"p1"}""", serializer.Serialize(patch).AsString());
        Assert.Equal("count is 1", Assert.Single(logs));

        var snapshot = engine.Call(instance.Get("snapshot"), instance, []);
        Assert.Equal("""{"count":1,"ids":["p1"],"at":12345}""", serializer.Serialize(snapshot).AsString());

        // config is a plain module export.
        Assert.Equal(0d, ns.Get("config").AsObject().Get("tickHz").AsNumber());
    }
}
