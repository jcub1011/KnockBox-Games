namespace KnockBox.Server.Games;

/// <summary>
/// Knobs for the server-authority sandbox (<c>KnockBox:Authority*</c> config; see
/// docs/SERVER_AUTHORITY_DESIGN.md §8). The per-call constraints (memory / timeout / statements /
/// recursion) bound what one module invocation can burn; <see cref="TickHzMax"/> clamps a module's
/// requested tick rate and <see cref="MaxLobbies"/> bounds the aggregate when an operator sets it —
/// that is the v1 answer to a hot module (a shared scheduler is future work). Note <see cref="MaxMemoryBytes"/> is a
/// per-invocation allocation budget, not a cap on what an engine retains across calls: authority
/// modules are operator-installed (dropped into games/), so the sandbox is defense-in-depth against
/// buggy or compromised games, not arbitrary hostile uploads.
///
/// Sizing note: <see cref="MaxLobbies"/> defaults to <b>0 (unlimited)</b>, so nothing server-side
/// refuses the hundredth concurrent authority lobby — concurrent engines are bounded by the host, and
/// in Docker by the container memory limit, where the GC pushes back rather than the server refusing.
/// That is deliberate: a default refusal nobody configured is worse than a host-level bound. Set
/// MaxLobbies (from the admin portal, which persists it) when you want the server to refuse *before*
/// the GC starts fighting; MaxLobbies × MaxMemoryBytes is then the theoretical per-call ceiling.
/// </summary>
public sealed record AuthorityOptions(
    // Master switch. When false, creating a lobby for a serverAuthority game fails with a clear
    // error — never a silent downgrade to host mode.
    bool Enabled,
    long MaxMemoryBytes,
    // Wall-clock budget per module invocation. A blunt fatal trigger (a GC pause inside the call
    // counts against it), so the default leaves headroom. It is also the ONLY runaway guard armed by
    // default now — see MaxStatements for the measurement that made that the right trade.
    TimeSpan CallTimeout,
    // DEFAULTS TO 0 (OFF), and that is a performance decision with a measured number behind it.
    //
    // Jint 4.16 splits execution constraints into "amortizable" (checked every N statements — a
    // countdown decrement and branch) and "exact" (checked before EVERY statement). TimeoutInterval is
    // amortizable; MaxStatements and LimitMemory are documented as never amortizable, and Jint's own
    // docs note that an exact constraint "can additionally disable the interpreter's tight-loop lane,
    // which costs every loop in the program".
    //
    // Measured on a real authority module (Alpha Chain, 2,500 ticks, identical deterministic workload,
    // median of 5 runs): the old default set — timeout + statements + memory + recursion — ran 224 ms.
    // Dropping MaxStatements alone: 67 ms, a 3.3x speedup. Also dropping the recursion limit in favour
    // of StackOverflowGuard: 51 ms, 4.4x. That factor is not a micro-optimisation; it is the difference
    // between a game fitting in CallTimeout and its lobby being killed, and every hosted game paid it.
    //
    // What is lost: MaxStatements was the DETERMINISTIC runaway guard, so a runaway loop is now caught
    // by the wall clock instead, which is load-sensitive (a GC pause counts against it). The lobby dies
    // either way; only the exact moment becomes less reproducible. Set a non-zero value to arm it again
    // — and expect roughly a 3x interpreter slowdown for every authority game on the server when you do.
    int MaxStatements,
    // DEFAULTS TO 0 (OFF) in favour of ConstraintOptions.StackOverflowGuard, which is both cheaper and
    // strictly wider. Per Jint's docs LimitRecursion "is probed at the call expression only", so `new`,
    // getters/setters, valueOf/toString coercions, Proxy traps and host callbacks all reach a function
    // body without passing it; the guard measures the remaining native stack instead and so covers every
    // route, at a documented 1.7-2.3% cost on recursion-heavy code. Jint gives MaxRecursionDepth
    // precedence when both are set, so a non-zero value here disarms the wider guard — set it only to
    // reproduce old behaviour.
    int RecursionLimit,
    // Clamp on a module's requested config.tickHz.
    double TickHzMax,
    // Max authority-module file size, checked at discovery and again at load.
    long MaxScriptBytes,
    // Max size of a single declared authorityWords dictionary file, checked at discovery. Larger than
    // MaxScriptBytes because dictionaries are the big blobs (a ~350k-word list is a few MB); the data
    // lives on the CLR heap (shared across lobbies), NOT in a per-invocation Jint budget.
    long MaxWordFileBytes,
    // Actor inbound channel bound. Two-tier overflow: intents drop with a warning (client
    // resyncs), ticks coalesce, roster work is never dropped.
    int QueueCapacity,
    // Cap on concurrent server-authority lobbies (0 = unlimited, and the default). Read LIVE via
    // AuthorityOptionsProvider, so a portal edit applies to the next lobby rather than the next restart.
    int MaxLobbies,
    // How long a game's shared parsed authority module may sit unused before the cache stops holding it
    // (0 = keep for the process lifetime). Defaulted rather than required so the one test that builds this
    // record by hand keeps compiling; FromConfiguration is the only caller with a policy to state.
    // Also read live — see AuthorityModuleCache.EvictIdle and ServerAuthorityManager.SweepModuleCache.
    TimeSpan ModuleCacheIdle = default,
    // Structural bound on `new Array(n)` and array growth, which is the one allocation shape a wall clock
    // is bad at catching: a single statement can ask for billions of slots. Cheap because it is checked
    // at the array operation rather than per statement, so it is the part of the memory story that
    // survives MaxStatements being off.
    uint MaxArrayLength = AuthorityOptions.DefaultMaxArrayLength,
    // Fraction of CallTimeout a single module call may reach before the server logs a warning naming the
    // game. THE POINT OF THIS KNOB: a game developer cannot measure Jint cost from a browser — solo play
    // runs the same code in V8 over an in-memory array under a JIT — so the first signal that a module is
    // near its budget used to be the lobby dying. 0 disables the warning.
    double SlowCallWarnFraction = AuthorityOptions.DefaultSlowCallWarnFraction,
    // How many CONSECUTIVE recoverable overruns (a timeout or statement trip on a coalesced tick) are
    // tolerated before the lobby is torn down. See ServerAuthority: a tick is droppable by design, so one
    // slow one is a hitch rather than a reason to end everybody's game; a module that cannot get through
    // a tick at all still dies, just after N tries instead of one.
    int MaxConsecutiveOverruns = AuthorityOptions.DefaultMaxConsecutiveOverruns,
    // Cap on how many words a single kb.words.pickRange call may return. Bounds both the JS array a
    // module can conjure in one crossing and the strings the host allocates to fill it.
    int MaxWordsPerCall = AuthorityOptions.DefaultMaxWordsPerCall,
    // Per-game CallTimeout overrides, keyed by game id (KnockBox:AuthorityCallTimeoutMsByGame:<id>).
    // Read at engine construction like every other per-call constraint, so a change applies to lobbies
    // started afterwards — which is why this is configuration and not a portal knob (see
    // OperatorAuthorityOptions on knobs that would lie about when they take effect).
    IReadOnlyDictionary<string, int>? CallTimeoutMsByGame = null)
{
    public const long DefaultMaxScriptBytes = 1_048_576;
    public const long DefaultMaxWordFileBytes = 33_554_432;
    public const uint DefaultMaxArrayLength = 10_000_000;
    public const double DefaultSlowCallWarnFraction = 0.5;
    public const int DefaultMaxConsecutiveOverruns = 3;
    public const int DefaultMaxWordsPerCall = 512;

    /// <summary>The wall-clock budget one call of <paramref name="gameId"/>'s module gets: its own
    /// override when an operator configured one, otherwise the server-wide <see cref="CallTimeout"/>.</summary>
    public TimeSpan CallTimeoutFor(string gameId) =>
        CallTimeoutMsByGame is { } map && map.TryGetValue(gameId, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : CallTimeout;

    public static AuthorityOptions FromConfiguration(IConfiguration config) => new(
        config.GetValue("KnockBox:AuthorityEnabled", true),
        config.GetValue("KnockBox:AuthorityMaxMemoryBytes", 33_554_432L),
        TimeSpan.FromMilliseconds(config.GetValue("KnockBox:AuthorityCallTimeoutMs", 250)),
        // Both default to 0 = off. See the members for the measurement; the short version is that the
        // pair cost a 4.4x interpreter slowdown for every authority game to re-guard what the amortized
        // wall-clock timeout and StackOverflowGuard already cover.
        config.GetValue("KnockBox:AuthorityMaxStatements", 0),
        config.GetValue("KnockBox:AuthorityRecursionLimit", 0),
        config.GetValue("KnockBox:AuthorityTickHzMax", 20.0),
        config.GetValue("KnockBox:AuthorityMaxScriptBytes", DefaultMaxScriptBytes),
        config.GetValue("KnockBox:AuthorityMaxWordFileBytes", DefaultMaxWordFileBytes),
        config.GetValue("KnockBox:AuthorityQueueCapacity", 256),
        // 0 = unlimited. See the sizing note above: the host (and the container's memory limit) is the
        // bound by default, and an operator who wants a hard refusal sets this from the portal.
        config.GetValue("KnockBox:AuthorityMaxLobbies", 0),
        TimeSpan.FromMinutes(config.GetValue("KnockBox:AuthorityModuleCacheIdleMinutes", 30)),
        config.GetValue("KnockBox:AuthorityMaxArrayLength", DefaultMaxArrayLength),
        config.GetValue("KnockBox:AuthoritySlowCallWarnFraction", DefaultSlowCallWarnFraction),
        config.GetValue("KnockBox:AuthorityMaxConsecutiveOverruns", DefaultMaxConsecutiveOverruns),
        config.GetValue("KnockBox:AuthorityMaxWordsPerCall", DefaultMaxWordsPerCall),
        ReadCallTimeoutOverrides(config));

    /// <summary>Reads the per-game timeout overrides. An absent section yields null rather than an empty
    /// map, so the common case carries no dictionary at all.</summary>
    private static IReadOnlyDictionary<string, int>? ReadCallTimeoutOverrides(IConfiguration config)
    {
        var section = config.GetSection("KnockBox:AuthorityCallTimeoutMsByGame");
        if (!section.Exists()) return null;
        Dictionary<string, int>? map = null;
        foreach (var child in section.GetChildren())
        {
            if (!int.TryParse(child.Value, out var ms) || ms <= 0) continue;
            (map ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))[child.Key] = ms;
        }
        return map;
    }
}
