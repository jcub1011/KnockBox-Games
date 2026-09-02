using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The per-call budget: how it is configured, what a module can learn about it, and what happens when
/// one is blown.
///
/// <para>Why this suite exists. A server-authority module runs interpreted, inside a wall-clock budget,
/// and overrunning it used to close the lobby on the first occurrence. A game developer cannot see any
/// of that from a browser — solo play runs the same code JIT-compiled over an in-memory dictionary — so
/// the first signal was players being disconnected mid-match. The changes pinned here attack that from
/// three sides: make the interpreter meaningfully faster by not arming constraints that disable its
/// fast paths, let a module ask how much budget it has left, and stop treating one slow tick as a
/// reason to end everybody's game.</para>
/// </summary>
public class AuthorityBudgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-budget-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsAuthorityRuntime> _runtimes = [];
    private readonly AuthorityModuleCache _modules = new(TimeProvider.System);

    public AuthorityBudgetTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var r in _runtimes) r.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private JsAuthorityRuntime Load(string source, AuthorityOptions? options = null, string gameId = "g",
        IReadOnlyDictionary<string, IWordPool>? words = null)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(path, source);
        var runtime = new JsAuthorityRuntime(
            path, _modules, options ?? AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs()),
            TimeProvider.System, words ?? new Dictionary<string, IWordPool>(), gameId);
        _runtimes.Add(runtime);
        runtime.Initialize("""[{"id":"p1","displayName":"Ann"}]""");
        return runtime;
    }

    // ── Configuration ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_statement_and_recursion_limits_are_off_by_default()
    {
        // Not a preference — a measured trade. Jint checks "exact" constraints before every statement and
        // disarms its tight-loop lanes while two are registered; MaxStatements and LimitMemory are both
        // exact. Arming the pair cost a ~4.4x interpreter slowdown for every hosted game, to re-guard
        // runaway CPU that the (amortizable, near-free) wall-clock timeout already bounds. If a future
        // edit re-arms them by default, every authority game on the server silently loses that factor.
        var options = AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs());
        Assert.Equal(0, options.MaxStatements);
        Assert.Equal(0, options.RecursionLimit);

        // The bound that replaces them is still there, and so is the one a wall clock is bad at catching.
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.CallTimeout);
        Assert.Equal(33_554_432L, options.MaxMemoryBytes);
        Assert.Equal(AuthorityOptions.DefaultMaxArrayLength, options.MaxArrayLength);
    }

    [Fact]
    public void A_module_that_executes_many_statements_quickly_is_no_longer_refused()
    {
        // The old 1,000,000-statement default was reachable by ordinary work: a loop over a dictionary
        // costs a handful of statements per candidate, so ~100k candidates hit it while finishing well
        // inside the time budget. That is the shape this default change is for.
        //
        // The wall clock is given deliberate slack. This test is about the STATEMENT limit no longer
        // refusing the work, and pinning it against the 250 ms default would silently turn it into a
        // benchmark of whatever else the suite is running on the same box.
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { let n = 0; for (let i = 0; i < 400000; i++) n += i; return { n }; },
                snapshot() { return {}; },
              };
            }
            """, AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityCallTimeoutMs", "30000"))));

        var result = runtime.Invoke("applyIntent", "\"p1\"", "{}");
        Assert.Contains("\"n\":", result);
    }

    [Fact]
    public void An_operator_can_still_arm_the_statement_limit()
    {
        // Off by default is not the same as gone: the deterministic guard is one config key away for an
        // operator who wants a runaway bounded by something a GC pause cannot move.
        var options = AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityMaxStatements", "5000")));
        Assert.Equal(5000, options.MaxStatements);

        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { let n = 0; for (let i = 0; i < 100000; i++) n += i; return { n }; },
                snapshot() { return {}; },
              };
            }
            """, options);

        var ex = Assert.Throws<AuthorityConstraintException>(() => runtime.Invoke("applyIntent", "\"p1\"", "{}"));
        Assert.Equal(AuthorityConstraintKind.Statements, ex.Kind);
    }

    [Fact]
    public void Unbounded_recursion_is_a_contained_script_error_rather_than_a_dead_process()
    {
        // StackOverflowGuard replaces LimitRecursion, and the difference matters beyond speed: without
        // either, runaway recursion ends the PROCESS with a native stack overflow that no catch sees and
        // nothing logs. With the guard it surfaces as an ordinary catchable JavaScript RangeError, which
        // reaches the actor as a contained failure — the lobby survives, and so does the server.
        //
        // `1 + down(...)` deliberately, not `return down(...)`: the latter is a proper tail call, and a
        // strict-mode tail call replaces its caller's frame instead of stacking one, so it consumes no
        // native stack for the guard to measure. The test below covers that lane.
        var runtime = Load("""
            export function createAuthority(kb) {
              function down(n) { return 1 + down(n + 1); }
              return {
                init() {},
                applyIntent() { return { n: down(0) }; },
                snapshot() { return {}; },
              };
            }
            """);

        Assert.Throws<AuthorityScriptException>(() => runtime.Invoke("applyIntent", "\"p1\"", "{}"));
        // ...and the engine is still usable, which is the whole claim of "contained".
        Assert.Equal("{}", runtime.Invoke("snapshot"));
    }

    [Fact]
    public void Endless_tail_recursion_is_caught_by_the_clock_rather_than_the_stack()
    {
        // The other half of the recursion story, and the reason dropping LimitRecursion leaves no hole.
        // A strict proper tail call runs on a trampoline: it never grows the native stack, so no stack
        // probe and no frame counter can ever see it — under the old LimitRecursion(64) this was equally
        // invisible. What actually bounds it is the wall clock, which is armed by default.
        var runtime = Load("""
            export function createAuthority(kb) {
              function forever(n) { return forever(n + 1); }
              return { init() {}, applyIntent() { return { n: forever(0) }; }, snapshot() { return {}; } };
            }
            """, AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityCallTimeoutMs", "150"))));

        var ex = Assert.Throws<AuthorityConstraintException>(() => runtime.Invoke("applyIntent", "\"p1\"", "{}"));
        Assert.Equal(AuthorityConstraintKind.Timeout, ex.Kind);
    }

    [Fact]
    public void A_per_game_timeout_override_applies_to_that_game_only()
    {
        var options = AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs(
            ("KnockBox:AuthorityCallTimeoutMs", "250"),
            ("KnockBox:AuthorityCallTimeoutMsByGame:heavy-game", "1200")));

        Assert.Equal(TimeSpan.FromMilliseconds(1200), options.CallTimeoutFor("heavy-game"));
        Assert.Equal(TimeSpan.FromMilliseconds(1200), options.CallTimeoutFor("HEAVY-GAME")); // ids are case-insensitive
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.CallTimeoutFor("some-other-game"));
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.CallTimeoutFor(""));
    }

    [Fact]
    public void No_override_section_costs_nothing()
    {
        // The common case carries no dictionary at all rather than an empty one.
        var options = AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs());
        Assert.Null(options.CallTimeoutMsByGame);
        Assert.Equal(options.CallTimeout, options.CallTimeoutFor("anything"));
    }

    // ── kb.budgetRemainingMs ─────────────────────────────────────────────────────────

    [Fact]
    public void A_module_can_read_how_much_of_its_call_budget_is_left()
    {
        var options = AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityCallTimeoutMs", "500")));
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() {
                  const first = kb.budgetRemainingMs();
                  let n = 0;
                  for (let i = 0; i < 200000; i++) n += i;
                  return { first, second: kb.budgetRemainingMs(), n };
                },
                snapshot() { return {}; },
              };
            }
            """, options);

        var json = System.Text.Json.JsonDocument.Parse(runtime.Invoke("applyIntent", "\"p1\"", "{}"));
        var first = json.RootElement.GetProperty("first").GetDouble();
        var second = json.RootElement.GetProperty("second").GetDouble();

        Assert.InRange(first, 0, 500);          // never more than the budget
        Assert.True(first > 0, "budget should be positive at the start of a call");
        Assert.True(second < first, $"budget should count down ({second} should be below {first})");
    }

    [Fact]
    public void The_budget_reported_is_the_games_own_when_it_has_an_override()
    {
        var options = AuthorityOptions.FromConfiguration(ConfigFactory.FromPairs(
            ("KnockBox:AuthorityCallTimeoutMs", "250"),
            ("KnockBox:AuthorityCallTimeoutMsByGame:heavy-game", "2000")));
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() { return { left: kb.budgetRemainingMs() }; },
                snapshot() { return {}; },
              };
            }
            """, options, gameId: "heavy-game");

        var json = System.Text.Json.JsonDocument.Parse(runtime.Invoke("applyIntent", "\"p1\"", "{}"));
        // Comfortably above the server-wide 250 ms, so it can only be the override that was reported.
        Assert.True(json.RootElement.GetProperty("left").GetDouble() > 1000);
    }

    [Fact]
    public void The_capability_is_frozen_along_with_the_rest_of_kb()
    {
        // kb is frozen so a buggy module cannot repoint a capability mid-game; a new member must not be
        // the hole in that. Strict mode makes the assignment throw, which is the observable proof.
        var runtime = Load("""
            export function createAuthority(kb) {
              return {
                init() {},
                applyIntent() {
                  try { kb.budgetRemainingMs = () => 999999; return { frozen: false }; }
                  catch (e) { return { frozen: true }; }
                },
                snapshot() { return {}; },
              };
            }
            """);

        Assert.Equal("""{"frozen":true}""", runtime.Invoke("applyIntent", "\"p1\"", "{}"));
    }

    // ── Constraint classification ────────────────────────────────────────────────────

    [Fact]
    public void A_timeout_is_reported_as_recoverable_and_a_memory_trip_is_not()
    {
        // What the actor keys its survive-or-close decision on. A timeout unwound the call the same way a
        // module throw does, so dropping that one call is defensible; a memory trip means the engine may
        // still be holding the heap that blew the cap, so it is not.
        var timeout = new AuthorityConstraintException("x", AuthorityConstraintKind.Timeout);
        var statements = new AuthorityConstraintException("x", AuthorityConstraintKind.Statements);
        Assert.True(timeout.IsRecoverable);
        Assert.True(statements.IsRecoverable);

        foreach (var kind in new[]
                 {
                     AuthorityConstraintKind.Memory, AuthorityConstraintKind.Recursion,
                     AuthorityConstraintKind.Cancelled, AuthorityConstraintKind.Unclassified,
                 })
            Assert.False(new AuthorityConstraintException("x", kind).IsRecoverable, kind.ToString());
    }

    [Fact]
    public void An_infinite_loop_is_classified_as_a_timeout()
    {
        var runtime = Load("""
            export function createAuthority(kb) {
              return { init() {}, applyIntent() { for (;;) {} }, snapshot() { return {}; } };
            }
            """, AuthorityOptions.FromConfiguration(
            ConfigFactory.FromPairs(("KnockBox:AuthorityCallTimeoutMs", "150"))));

        var ex = Assert.Throws<AuthorityConstraintException>(() => runtime.Invoke("applyIntent", "\"p1\"", "{}"));
        Assert.Equal(AuthorityConstraintKind.Timeout, ex.Kind);
        Assert.True(ex.IsRecoverable);
    }
}
