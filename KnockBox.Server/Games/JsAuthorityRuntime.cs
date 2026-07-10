using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

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
    AuthorityOptions options,
    TimeProvider time) : IAuthorityRuntime
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
                .MaxStatements(options.MaxStatements)
                .LimitRecursion(options.RecursionLimit));
            _engine = engine;
            _parser = new JsonParser(engine);
            _serializer = new JsonSerializer(engine);

            // Scrub ambient time BEFORE the module's top-level code can run and capture it.
            // (Engine.Realm is not public in Jint 4.11; deleting a globalThis PROPERTY is legal even
            // in strict mode — only unqualified `delete Date` isn't.)
            engine.Evaluate("delete globalThis.Date");

            engine.Modules.Add("authority", File.ReadAllText(scriptPath));
            var ns = engine.Modules.Import("authority");

            var create = ns.Get("createAuthority");
            var isFunction = engine.Evaluate("(v) => typeof v === 'function'");
            if (!engine.Call(isFunction, create).AsBoolean())
                throw new AuthorityLoadException("Authority module must export a createAuthority(kb) function.");

            Config = ParseConfig(engine, ns.Get("config"));

            var instanceValue = engine.Call(create, BuildKb(engine));
            if (instanceValue is not ObjectInstance instance)
                throw new AuthorityLoadException("createAuthority(kb) must return the authority object.");
            _instance = instance;

            var exports = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hook in RequiredHooks.Concat(OptionalHooks))
                if (engine.Call(isFunction, instance.Get(hook)).AsBoolean())
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

        JsValue result;
        try
        {
            result = engine.Call(fn, instance, args);
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

        if (result.IsNull() || result.IsUndefined()) return "null";
        return _serializer!.Serialize(result).AsString();
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

        // Freeze so a buggy module can't repoint capabilities mid-game.
        var freeze = engine.Evaluate("Object.freeze");
        engine.Call(freeze, log);
        engine.Call(freeze, kb);
        return kb;

        void AddLog(JsObject target, string name, LogLevel level) =>
            target.Set(name, new ClrFunction(engine, name, (_, args) =>
            {
                _logs.Add((level, args.Length > 0 ? args[0].ToString() : ""));
                return JsValue.Undefined;
            }));
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
