using KnockBox.Server.Games;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KnockBox.Server.Tests;

public class JsAuthorityRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-authority-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsAuthorityRuntime> _runtimes = [];

    public JsAuthorityRuntimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var r in _runtimes) r.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static AuthorityOptions Opts(int timeoutMs = 2_000, long maxScriptBytes = AuthorityOptions.DefaultMaxScriptBytes) => new(
        Enabled: true,
        MaxMemoryBytes: 32 * 1024 * 1024,
        CallTimeout: TimeSpan.FromMilliseconds(timeoutMs),
        MaxStatements: 1_000_000,
        RecursionLimit: 64,
        TickHzMax: 20,
        MaxScriptBytes: maxScriptBytes,
        QueueCapacity: 256,
        MaxLobbies: 100);

    private JsAuthorityRuntime Load(string moduleSource, AuthorityOptions? opts = null,
        TimeProvider? time = null, string playersJson = """[{"id":"p1","displayName":"Ann"}]""")
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, moduleSource);
        var runtime = new JsAuthorityRuntime(path, opts ?? Opts(), time ?? TimeProvider.System);
        _runtimes.Add(runtime);
        runtime.Initialize(playersJson);
        return runtime;
    }

    private const string CounterModule = """
        export function createAuthority(kb) {
          let state = null;
          return {
            init(players) { state = { count: 0, ids: players.map(p => p.id) }; },
            applyIntent(fromId, action) {
              if (action.kind !== 'inc') return null;
              state.count += 1;
              return { count: state.count };
            },
            snapshot() { return state; },
            onPlayerJoined(p) { state.ids.push(p.id); return null; },
          };
        }
        export const config = { perRecipient: false, tickHz: 5 };
        """;

    [Fact]
    public void Initialize_exposes_hooks_and_config()
    {
        var runtime = Load(CounterModule);

        Assert.Superset(new HashSet<string> { "init", "applyIntent", "snapshot", "onPlayerJoined" },
            runtime.Exports.ToHashSet());
        Assert.DoesNotContain("tick", runtime.Exports);
        Assert.False(runtime.Config.PerRecipient);
        Assert.Equal(5, runtime.Config.TickHz);
    }

    [Fact]
    public void Init_receives_the_initial_roster()
    {
        var runtime = Load(CounterModule);
        Assert.Equal("""{"count":0,"ids":["p1"]}""", runtime.Invoke("snapshot"));
    }

    [Fact]
    public void ApplyIntent_returns_a_patch_or_null_as_json()
    {
        var runtime = Load(CounterModule);

        Assert.Equal("""{"count":1}""", runtime.Invoke("applyIntent", "\"p1\"", """{"kind":"inc"}"""));
        Assert.Equal("null", runtime.Invoke("applyIntent", "\"p1\"", """{"kind":"bogus"}"""));
    }

    [Fact]
    public void Missing_createAuthority_fails_the_load()
    {
        var ex = Assert.Throws<AuthorityLoadException>(() => Load("export const config = {};"));
        Assert.Contains("createAuthority", ex.Message);
    }

    [Fact]
    public void Missing_required_hook_fails_the_load()
    {
        var ex = Assert.Throws<AuthorityLoadException>(() => Load("""
            export function createAuthority(kb) {
              return { init() {}, applyIntent() { return null; } };  // no snapshot
            }
            """));
        Assert.Contains("snapshot", ex.Message);
    }

    [Fact]
    public void Relative_import_fails_the_load()
    {
        Assert.Throws<AuthorityLoadException>(() => Load(
            "import './helpers.js';\n" + CounterModule));
    }

    [Fact]
    public void Throwing_init_fails_the_load()
    {
        Assert.Throws<AuthorityLoadException>(() => Load("""
            export function createAuthority(kb) {
              return { init() { throw new Error('bad seed'); }, applyIntent() { return null; }, snapshot() { return {}; } };
            }
            """));
    }

    [Fact]
    public void Oversize_module_fails_the_load()
    {
        var padded = CounterModule + "\n//" + new string('x', 4096);
        Assert.Throws<AuthorityLoadException>(() => Load(padded, Opts(maxScriptBytes: 1024)));
    }

    [Fact]
    public void Malformed_config_fails_the_load()
    {
        Assert.Throws<AuthorityLoadException>(() => Load("""
            export function createAuthority(kb) {
              return { init() {}, applyIntent() { return null; }, snapshot() { return {}; } };
            }
            export const config = { tickHz: "fast" };
            """));
    }

    [Fact]
    public void Module_throw_is_contained_and_the_engine_stays_usable()
    {
        var runtime = Load("""
            export function createAuthority(kb) {
              let state = { ok: true };
              return {
                init() {},
                applyIntent(fromId, action) {
                  if (action.kind === 'boom') throw new Error('nope');
                  return { ok: true };
                },
                snapshot() { return state; },
              };
            }
            """);

        var ex = Assert.Throws<AuthorityScriptException>(() =>
            runtime.Invoke("applyIntent", "\"p1\"", """{"kind":"boom"}"""));
        Assert.Contains("nope", ex.Message);

        // Contained: the next invocation still works.
        Assert.Equal("""{"ok":true}""", runtime.Invoke("applyIntent", "\"p1\"", """{"kind":"fine"}"""));
    }

    [Fact]
    public void Infinite_loop_is_a_fatal_constraint_violation()
    {
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { for (;;) {} },
                snapshot() { return {}; },
              };
            }
            """, Opts(timeoutMs: 150));

        Assert.Throws<AuthorityConstraintException>(() =>
            runtime.Invoke("applyIntent", "\"p1\"", "{}"));
    }

    [Fact]
    public void Date_is_unavailable_and_kb_now_uses_the_server_clock()
    {
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent(fromId, action) {
                  if (action.kind === 'date') return { t: new Date().getTime() };
                  return { now: kb.now() };
                },
                snapshot() { return {}; },
              };
            }
            """, time: time);

        Assert.Throws<AuthorityScriptException>(() =>
            runtime.Invoke("applyIntent", "\"p1\"", """{"kind":"date"}"""));
        Assert.Equal("""{"now":1700000000000}""", runtime.Invoke("applyIntent", "\"p1\"", """{"kind":"now"}"""));
    }

    [Fact]
    public void Effects_are_buffered_and_drained_once()
    {
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent(fromId, action) {
                  kb.setOwner('p2');
                  kb.setLobbyOpen(false);
                  kb.log.info('round started');
                  kb.log.warn('low time');
                  return { ok: true };
                },
                snapshot() { return {}; },
              };
            }
            """);
        runtime.DrainEffects(); // clear anything from init

        runtime.Invoke("applyIntent", "\"p1\"", "{}");
        var effects = runtime.DrainEffects();

        Assert.Equal("p2", effects.SetOwner);
        Assert.False(effects.SetLobbyOpen);
        Assert.Collection(effects.Logs,
            l => Assert.Equal((LogLevel.Information, "round started"), l),
            l => Assert.Equal((LogLevel.Warning, "low time"), l));

        // Drained: a second drain is empty.
        Assert.Same(AuthorityEffects.None, runtime.DrainEffects());
    }

    [Fact]
    public void Kb_is_frozen_against_capability_swaps()
    {
        // In strict mode, assigning to a frozen object's property throws — so a buggy or hostile
        // module can't repoint kb.setOwner and have later calls silently do something else.
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { kb.setOwner = function () {}; return null; },
                snapshot() { return {}; },
              };
            }
            """);

        Assert.Throws<AuthorityScriptException>(() => runtime.Invoke("applyIntent", "\"p1\"", "{}"));
    }
}
