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
    IReadOnlyDictionary<string, IWordPool> wordPools) : IAuthorityRuntime
{
    private static readonly string[] RequiredHooks = ["init", "applyIntent", "snapshot"];
    private static readonly string[] OptionalHooks =
        ["onPlayerJoined", "onPlayerLeft", "onPlayerDisconnected", "onPlayerConnected", "tick"];

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

            var engine = new Engine(o => o
                .Strict()
                .LimitMemory(options.MaxMemoryBytes)
                .TimeoutInterval(options.CallTimeout)
                // Jint's own default here is TEN SECONDS, against a 250ms call budget. TimeoutInterval is
                // checked BETWEEN statements, so it cannot interrupt a single Regex.IsMatch: without this
                // line one catastrophically-backtracking regex in a module owns the lobby's drain task for
                // 40x the budget while its bounded channel backs up behind it. (The resulting
                // RegexMatchTimeoutException derives from TimeoutException, so Invoke below already
                // classifies it as a constraint trip — only the duration was wrong.)
                .RegexTimeoutInterval(options.CallTimeout)
                .MaxStatements(options.MaxStatements)
                .LimitRecursion(options.RecursionLimit));
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
            engine.Modules.Add("authority", b => b.AddModule(modules.Get(scriptPath, options.CallTimeout)));
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
            throw new AuthorityConstraintException($"{export}: {ex.Message}", ex);
        }
        catch (JavaScriptException ex) // the module threw — contained, engine unwound one call
        {
            throw new AuthorityScriptException($"{export}: {ex.Message}", ex);
        }
        catch (JintException ex) // unclassified engine failure — state untrustworthy, treat as fatal
        {
            throw new AuthorityConstraintException($"{export}: {ex.Message}", ex);
        }
    }

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
