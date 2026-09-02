using System.Diagnostics;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using KnockBox.Server.Games.Words;

namespace KnockBox.Server.Games;

/// <summary>
/// The Jint backend of <see cref="IAuthorityRuntime"/> — every line of Jint usage in the server
/// lives here, keeping a clean swap point for the WASM backend. Sandbox properties, by
/// construction (design §3b, pinned by JintSandboxSpikeTests):
/// <list type="bullet">
/// <item>No CLR access — AllowClr is never enabled.</item>
/// <item>No filesystem/module escape — no module loader is configured, so any import inside the
/// module fails at load (single-file rule).</item>
/// <item>No ambient time — the Date global is deleted before the module evaluates; kb.now() is the
/// only clock. (Math.random stays available in v1: nothing replays module calls.)</item>
/// <item>Bounded CPU/memory per call — the four engine constraints, re-armed per invocation.</item>
/// </list>
/// Interop is JsValue-only (ClrFunction / JsonParser / JsonSerializer) — Jint's
/// no-reflection-marshaling path, which is what keeps the Native AOT publish clean.
/// </summary>
public sealed class JsAuthorityRuntime(
    string scriptPath,
    AuthorityModuleCache modules,
    AuthorityOptions options,
    TimeProvider time,
    IReadOnlyDictionary<string, IWordPool> wordPools,
    string gameId = "") : IAuthorityRuntime
{
    private static readonly string[] RequiredHooks = ["init", "applyIntent", "snapshot"];
    private static readonly string[] OptionalHooks =
        ["onPlayerJoined", "onPlayerLeft", "onPlayerDisconnected", "onPlayerConnected", "tick"];

    /// <summary>This engine's per-call wall-clock budget, resolved once (a per-game override, else the
    /// server-wide one). Also what <c>kb.budgetRemainingMs()</c> counts down from.</summary>
    private readonly TimeSpan _callTimeout = options.CallTimeoutFor(gameId);

    /// <summary>Stopwatch timestamp at which the in-flight call runs out of budget, or 0 between calls.
    /// Written by <see cref="Invoke"/> around the one engine entry, read by the kb.budgetRemainingMs
    /// ClrFunction. Single-threaded by the actor contract, so a plain field is enough.</summary>
    private long _callDeadline;

    private Engine? _engine;
    private ObjectInstance? _instance;
    private JsonParser? _parser;
    private JsonSerializer? _serializer;

    // Deferred-effect buffers, appended by the kb callbacks during an invocation and swapped out by
    // DrainEffects. Single drain task only — no synchronization needed.
    private string? _pendingOwner;
    private bool? _pendingLobbyOpen;
    private List<(LogLevel, string)> _logs = [];

    public IReadOnlySet<string> Exports { get; private set; } = new HashSet<string>();
    public AuthorityConfig Config { get; private set; } = new();

    public void Initialize(string playersJson)
    {
        try
        {
            // Re-check size at load (the catalog checked at discovery; a hot-reload race could have
            // swapped the file since).
            var info = new FileInfo(scriptPath);
            if (!info.Exists)
                throw new AuthorityLoadException($"Authority module not found: {scriptPath}");
            if (info.Length > options.MaxScriptBytes)
                throw new AuthorityLoadException(
                    $"Authority module is {info.Length} bytes (max {options.MaxScriptBytes}).");

            // CONSTRAINT SET, and the shape of it is a measured performance decision — see
            // AuthorityOptions.MaxStatements for the numbers. Jint 4.16 partitions constraints into
            // amortizable (checked every N statements) and exact (checked before every single one), and an
            // exact constraint also disarms the interpreter's tight-loop lanes, which costs every loop in
            // every module the server hosts. TimeoutInterval is amortizable; MaxStatements and LimitMemory
            // are not. Arming the pair by default cost a 4.4x interpreter slowdown to re-guard what the
            // wall clock already bounds, so both are now opt-in and off.
            var engine = new Engine(o =>
            {
                o.Strict()
                    .LimitMemory(options.MaxMemoryBytes)
                    .TimeoutInterval(_callTimeout)
                    // Jint's own default here is TEN SECONDS, against a 250ms call budget. TimeoutInterval is
                    // checked BETWEEN statements, so it cannot interrupt a single Regex.IsMatch: without this
                    // line one catastrophically-backtracking regex in a module owns the lobby's drain task for
                    // 40x the budget while its bounded channel backs up behind it. (The resulting
                    // RegexMatchTimeoutException derives from TimeoutException, so Invoke below already
                    // classifies it as a constraint trip — only the duration was wrong.)
                    .RegexTimeoutInterval(_callTimeout);

                // Both off by default. Jint treats a value that cannot express a real limit as "remove the
                // constraint", so guarding on > 0 is also what keeps a configured 0 from registering a
                // constraint that can never fail and yet still costs every statement.
                if (options.MaxStatements > 0) o.MaxStatements(options.MaxStatements);
                if (options.RecursionLimit > 0) o.LimitRecursion(options.RecursionLimit);

                // The wider, cheaper replacement for LimitRecursion: it measures the remaining native stack
                // at every entry into an interpreted function, so `new`, accessors, coercions, Proxy traps
                // and host callbacks are all covered, where LimitRecursion is probed at the call expression
                // alone. Without either, unbounded recursion ends the PROCESS with a native stack overflow
                // that no catch sees. Jint gives MaxRecursionDepth precedence when both are set, so an
                // operator who arms RecursionLimit knowingly narrows this back down.
                o.Constraints.StackOverflowGuard = true;

                // Structural allocation bound that survives MaxStatements being off: one statement can ask
                // for billions of array slots, and a wall clock is poor at catching that.
                o.Constraints.MaxArraySize = options.MaxArrayLength;
            });
            _engine = engine;
            _parser = new JsonParser(engine);
            _serializer = new JsonSerializer(engine);

            // Scrub ambient time BEFORE the module's top-level code can run and capture it. Engine.Realm is
            // not public in Jint 4.11, but Engine.Global is, and it IS the global object — so this is a
            // direct property delete with no source string to parse per lobby.
            engine.Global.Delete("Date");

            // Register the SHARED prepared module (parsed once, reused across every lobby engine of
            // this game) rather than re-reading and re-parsing the file per lobby. The size cap was
            // already enforced above; the cache only parses/caches.
            engine.Modules.Add("authority", b => b.AddModule(modules.Get(scriptPath, _callTimeout)));
            var ns = engine.Modules.Import("authority");

            // JsValueExtensions.IsCallable is exactly `typeof v === 'function'` for everything that can turn
            // up here — plain functions, arrows, class constructors, bound functions and callable proxies all
            // answer true, callable-less objects false — so the nine round trips through a compiled
            // `(v) => typeof v === 'function'` (one here, eight below) buy nothing over a CLR type test.
            var create = ns.Get("createAuthority");
            if (!create.IsCallable())
                throw new AuthorityLoadException("Authority module must export a createAuthority(kb) function.");

            Config = ParseConfig(engine, ns.Get("config"));

            var instanceValue = engine.Call(create, BuildKb(engine));
            if (instanceValue is not ObjectInstance instance)
                throw new AuthorityLoadException("createAuthority(kb) must return the authority object.");
            _instance = instance;

            var exports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hook in RequiredHooks.Concat(OptionalHooks))
                if (instance.Get(hook).IsCallable())
                    exports.Add(hook);
            var missing = RequiredHooks.Where(h => !exports.Contains(h)).ToList();
            if (missing.Count > 0)
                throw new AuthorityLoadException($"Authority object is missing required hook(s): {string.Join(", ", missing)}.");
            Exports = exports;

            Invoke("init", playersJson);
        }
        catch (AuthorityLoadException)
        {
            throw;
        }
        catch (Exception ex) // syntax errors, import resolution, constraint trips during top-level eval, init throws…
        {
            throw new AuthorityLoadException($"Authority module failed to load: {ex.Message}", ex);
        }
    }

    public string Invoke(string export, params string[] jsonArgs)
    {
        if (_engine is not { } engine || _instance is not { } instance)
            throw new InvalidOperationException("Initialize must succeed before Invoke.");

        var fn = instance.Get(export);
        var args = new JsValue[jsonArgs.Length];
        for (var i = 0; i < jsonArgs.Length; i++) args[i] = _parser!.Parse(jsonArgs[i]);

        // Serialization is INSIDE the classifying try, not after it. JSON.stringify semantics throw a
        // JavaScriptException of their own for a cyclic object or a BigInt, and a module returning a
        // structure with a back-reference is an ordinary game-logic bug — exactly the contained,
        // five-strikes case. Left outside, that throw reached the actor unclassified and closed the
        // lobby for everyone on the very first occurrence.
        // Armed around the one engine entry so kb.budgetRemainingMs() can answer during the call and
        // reads zero outside it. Cleared in a finally: a module that is told it has budget left after the
        // call that granted it has already unwound would be worse than having no capability at all.
        _callDeadline = Stopwatch.GetTimestamp() + (long)(_callTimeout.TotalSeconds * Stopwatch.Frequency);
        try
        {
            var result = engine.Call(fn, instance, args);
            return result.IsNull() || result.IsUndefined()
                ? "null"
                : _serializer!.Serialize(result).AsString();
        }
        // Order matters: constraint types are more specific than the JintException base.
        catch (Exception ex) when (ex is MemoryLimitExceededException or StatementsCountOverflowException
            or RecursionDepthOverflowException or ExecutionCanceledException or TimeoutException)
        {
            throw new AuthorityConstraintException($"{export}: {ex.Message}", Classify(ex), ex);
        }
        catch (JavaScriptException ex) // the module threw — contained, engine unwound one call
        {
            throw new AuthorityScriptException($"{export}: {ex.Message}", ex);
        }
        catch (JintException ex) // unclassified engine failure — state untrustworthy, treat as fatal
        {
            throw new AuthorityConstraintException($"{export}: {ex.Message}", AuthorityConstraintKind.Unclassified, ex);
        }
        finally
        {
            _callDeadline = 0;
        }
    }

    /// <summary>Names the budget a constraint trip blew. RegexMatchTimeoutException derives from
    /// TimeoutException and so lands on Timeout, which is correct: it is the same wall-clock budget.</summary>
    private static AuthorityConstraintKind Classify(Exception ex) => ex switch
    {
        MemoryLimitExceededException => AuthorityConstraintKind.Memory,
        StatementsCountOverflowException => AuthorityConstraintKind.Statements,
        RecursionDepthOverflowException => AuthorityConstraintKind.Recursion,
        ExecutionCanceledException => AuthorityConstraintKind.Cancelled,
        TimeoutException => AuthorityConstraintKind.Timeout,
        _ => AuthorityConstraintKind.Unclassified,
    };

    public AuthorityEffects DrainEffects()
    {
        if (_pendingOwner is null && _pendingLobbyOpen is null && _logs.Count == 0)
            return AuthorityEffects.None;
        var effects = new AuthorityEffects(_pendingOwner, _pendingLobbyOpen, _logs);
        (_pendingOwner, _pendingLobbyOpen, _logs) = (null, null, []);
        return effects;
    }

    public void Dispose()
    {
        _engine?.Dispose();
        (_engine, _instance, _parser, _serializer) = (null, null, null, null);
    }

    /// <summary>The frozen capability object handed to createAuthority (design §3). All members are
    /// ClrFunction — Jint's no-reflection path. setOwner/setLobbyOpen/log.* only buffer (deferred
    /// effects); now() is the only inline read.</summary>
    private ObjectInstance BuildKb(Engine engine)
    {
        var kb = new JsObject(engine);

        kb.Set("now", new ClrFunction(engine, "now",
            (_, _) => (double)time.GetUtcNow().ToUnixTimeMilliseconds()));

        kb.Set("setOwner", new ClrFunction(engine, "setOwner", (_, args) =>
        {
            if (args.Length > 0 && args[0].IsString()) _pendingOwner = args[0].AsString();
            else _logs.Add((LogLevel.Error, "kb.setOwner expects a player-id string; call ignored."));
            return JsValue.Undefined;
        }));

        kb.Set("setLobbyOpen", new ClrFunction(engine, "setLobbyOpen", (_, args) =>
        {
            _pendingLobbyOpen = args.Length > 0 && TypeConverter.ToBoolean(args[0]); // JS truthiness
            return JsValue.Undefined;
        }));

        var log = new JsObject(engine);
        AddLog(log, "debug", LogLevel.Debug);
        AddLog(log, "info", LogLevel.Information);
        AddLog(log, "warn", LogLevel.Warning);
        AddLog(log, "error", LogLevel.Error);
        kb.Set("log", log);

        // How much of THIS call's wall-clock budget is left, in milliseconds.
        //
        // The capability exists because the alternative is guessing. A module doing open-ended work — a
        // dictionary scan, a search, a simulation step — has no way to know how expensive it is on this
        // host, so it either hard-codes a budget tuned on someone else's machine or gets killed. With this
        // it can stop cleanly on its own terms and return a partial-but-consistent result, which is always
        // a better outcome for players than the lobby being torn down. Reads 0 outside a call, and never
        // reports more than the budget.
        kb.Set("budgetRemainingMs", new ClrFunction(engine, "budgetRemainingMs", (_, _) =>
        {
            if (_callDeadline == 0) return 0d;
            var remaining = _callDeadline - Stopwatch.GetTimestamp();
            return remaining <= 0 ? 0d : remaining * 1000d / Stopwatch.Frequency;
        }));

        var words = BuildWords(engine);
        kb.Set("words", words);

        // Freeze so a buggy module can't repoint capabilities mid-game. Engine.Intrinsics.Object is the
        // realm's Object constructor, so Object.freeze is reachable as a property read — the previous
        // `engine.Evaluate("Object.freeze")` compiled that one expression afresh for every lobby.
        var freeze = engine.Intrinsics.Object.Get("freeze");
        engine.Call(freeze, log);
        engine.Call(freeze, words);
        engine.Call(freeze, kb);
        return kb;

        void AddLog(JsObject target, string name, LogLevel level) =>
            target.Set(name, new ClrFunction(engine, name, (_, args) =>
            {
                _logs.Add((level, args.Length > 0 ? args[0].ToString() : ""));
                return JsValue.Undefined;
            }));
    }

    /// <summary>Builds the frozen <c>kb.words</c> capability over the lobby's pre-resolved dictionaries
    /// (design: the shared word service). Every member is a ClrFunction closing over the shared
    /// <see cref="IWordPool"/> map, so the dictionary never enters the JS heap — only the
    /// boolean/number/string RESULT of a lookup crosses the boundary. All members are defensively
    /// guarded and return <c>false</c>/<c>0</c>/<c>null</c> for a bad arg, unknown key, or out-of-range
    /// index rather than throwing — a CLR throw out of a ClrFunction would be misclassified as a fatal
    /// module failure (design §7).</summary>
    private ObjectInstance BuildWords(Engine engine)
    {
        var words = new JsObject(engine);

        // has(dictKey, word) -> boolean
        words.Set("has", new ClrFunction(engine, "has", (_, args) =>
        {
            if (args.Length < 2 || !args[0].IsString() || !args[1].IsString()) return false;
            return wordPools.TryGetValue(args[0].AsString(), out var pool) && pool.Contains(args[1].AsString());
        }));

        // count(dictKey) -> number (total across all lengths; the valid index range for pick)
        words.Set("count", new ClrFunction(engine, "count", (_, args) =>
        {
            if (args.Length < 1 || !args[0].IsString()) return 0d;
            return wordPools.TryGetValue(args[0].AsString(), out var pool) ? (double)pool.TotalWordCount : 0d;
        }));

        // pick(dictKey, index) -> string | null   (global index in [0, count))
        words.Set("pick", new ClrFunction(engine, "pick", (_, args) =>
        {
            if (args.Length < 2 || !args[0].IsString() || !args[1].IsNumber()) return JsValue.Null;
            if (!wordPools.TryGetValue(args[0].AsString(), out var pool)) return JsValue.Null;
            var n = args[1].AsNumber();
            if (!double.IsFinite(n)) return JsValue.Null;
            var index = (int)n;
            if (index < 0 || index >= pool.TotalWordCount) return JsValue.Null;
            return Encoding.ASCII.GetString(pool.GetWord(index));
        }));

        // countOfLength(dictKey, length) -> number
        words.Set("countOfLength", new ClrFunction(engine, "countOfLength", (_, args) =>
        {
            if (args.Length < 2 || !args[0].IsString() || !args[1].IsNumber()) return 0d;
            if (!wordPools.TryGetValue(args[0].AsString(), out var pool)) return 0d;
            var len = args[1].AsNumber();
            return double.IsFinite(len) ? (double)pool.GetWordCount((int)len) : 0d;
        }));

        // pickOfLength(dictKey, length, index) -> string | null   (index in [0, countOfLength))
        words.Set("pickOfLength", new ClrFunction(engine, "pickOfLength", (_, args) =>
        {
            if (args.Length < 3 || !args[0].IsString() || !args[1].IsNumber() || !args[2].IsNumber())
                return JsValue.Null;
            if (!wordPools.TryGetValue(args[0].AsString(), out var pool)) return JsValue.Null;
            var lenN = args[1].AsNumber();
            var idxN = args[2].AsNumber();
            if (!double.IsFinite(lenN) || !double.IsFinite(idxN)) return JsValue.Null;
            int len = (int)lenN, index = (int)idxN;
            if (index < 0 || index >= pool.GetWordCount(len)) return JsValue.Null;
            return Encoding.ASCII.GetString(pool.GetWord(len, index));
        }));

        // rangeOfPrefix(dictKey, length, prefix) -> [start, end] | null
        //
        // The bounds of the words of `length` that start with `prefix`. Two binary searches, run on the
        // side of the boundary that holds the bytes. Every word game needs this shape — "words of length
        // L starting with the succession letter" — and without it each one re-implements the search in
        // JavaScript over pickOfLength: an interpreted loop plus a marshalled string per probe. On the
        // shipped Alpha Chain module, resolving all 26 letters across 14 lengths cost 3,298 crossings and
        // 4.4 ms that way, and 364 crossings and 1.0 ms through this.
        //
        // Returns a two-element array so the JS side reads `const [start, end] = ...`; null for an unknown
        // dictionary or a non-string prefix, matching the guarded style of the rest of this surface.
        words.Set("rangeOfPrefix", new ClrFunction(engine, "rangeOfPrefix", (_, args) =>
        {
            if (args.Length < 3 || !args[0].IsString() || !args[1].IsNumber() || !args[2].IsString())
                return JsValue.Null;
            if (!wordPools.TryGetValue(args[0].AsString(), out var pool)) return JsValue.Null;
            var lenN = args[1].AsNumber();
            if (!double.IsFinite(lenN)) return JsValue.Null;
            var (start, end) = pool.RangeOfPrefix((int)lenN, args[2].AsString());
            return engine.Intrinsics.Array.Construct([start, end]);
        }));

        // pickRange(dictKey, length, start, count) -> string[] | null
        //
        // A slice of a length bucket in ONE crossing instead of `count` of them. Measured at ~0.09 us per
        // word against ~0.56 us for a pickOfLength each — but the crossing is not really the point: what
        // it removes is `count` iterations of an interpreted loop, which is where the time actually goes.
        //
        // Clamped to the bucket and capped at MaxWordsPerCall, so a module cannot conjure a 386k-element
        // array (or ask the host to allocate 386k strings) in a single call. A truncated result is not
        // silently wrong for the caller: the array's own length says how many words came back.
        words.Set("pickRange", new ClrFunction(engine, "pickRange", (_, args) =>
        {
            if (args.Length < 4 || !args[0].IsString() || !args[1].IsNumber()
                || !args[2].IsNumber() || !args[3].IsNumber()) return JsValue.Null;
            if (!wordPools.TryGetValue(args[0].AsString(), out var pool)) return JsValue.Null;
            double lenN = args[1].AsNumber(), startN = args[2].AsNumber(), countN = args[3].AsNumber();
            if (!double.IsFinite(lenN) || !double.IsFinite(startN) || !double.IsFinite(countN))
                return JsValue.Null;

            int len = (int)lenN, start = (int)startN, count = (int)countN;
            var available = pool.GetWordCount(len);
            if (start < 0 || start >= available || count <= 0)
                return engine.Intrinsics.Array.Construct([]);
            count = Math.Min(Math.Min(count, options.MaxWordsPerCall), available - start);

            var items = new JsValue[count];
            for (var i = 0; i < count; i++)
                items[i] = Encoding.ASCII.GetString(pool.GetWord(len, start + i));
            return engine.Intrinsics.Array.Construct(items);
        }));

        return words;
    }

    private static AuthorityConfig ParseConfig(Engine engine, JsValue config)
    {
        if (config.IsUndefined() || config.IsNull()) return new AuthorityConfig();
        if (config is not ObjectInstance obj)
            throw new AuthorityLoadException("The config export must be a plain object.");

        var perRecipient = obj.Get("perRecipient");
        var tickHz = obj.Get("tickHz");
        if (!perRecipient.IsUndefined() && !perRecipient.IsBoolean())
            throw new AuthorityLoadException("config.perRecipient must be a boolean.");
        if (!tickHz.IsUndefined() && (!tickHz.IsNumber() || !double.IsFinite(tickHz.AsNumber()) || tickHz.AsNumber() < 0))
            throw new AuthorityLoadException("config.tickHz must be a finite non-negative number.");

        return new AuthorityConfig(
            PerRecipient: !perRecipient.IsUndefined() && perRecipient.AsBoolean(),
            TickHz: tickHz.IsUndefined() ? 0 : tickHz.AsNumber());
    }
}
