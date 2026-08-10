namespace KnockBox.Server.Games;

/// <summary>
/// Knobs for the server-authority sandbox (<c>KnockBox:Authority*</c> config; see
/// docs/SERVER_AUTHORITY_DESIGN.md §8). The per-call constraints (memory / timeout / statements /
/// recursion) bound what one module invocation can burn; <see cref="TickHzMax"/> clamps a module's
/// requested tick rate and <see cref="MaxLobbies"/> bounds the aggregate — that is the v1 answer to
/// a hot module (a shared scheduler is future work). Note <see cref="MaxMemoryBytes"/> is a
/// per-invocation allocation budget, not a cap on what an engine retains across calls: authority
/// modules are operator-installed (dropped into games/), so the sandbox is defense-in-depth against
/// buggy or compromised games, not arbitrary hostile uploads. Sizing note: MaxLobbies ×
/// MaxMemoryBytes is the theoretical per-call ceiling (~3.2 GB at the defaults) — lower both on
/// small hosts.
/// </summary>
public sealed record AuthorityOptions(
    // Master switch. When false, creating a lobby for a serverAuthority game fails with a clear
    // error — never a silent downgrade to host mode.
    bool Enabled,
    long MaxMemoryBytes,
    // Wall-clock budget per module invocation. A blunt fatal trigger (a GC pause inside the call
    // counts against it), so the default leaves headroom; MaxStatements is the deterministic
    // runaway guard.
    TimeSpan CallTimeout,
    int MaxStatements,
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
    // Cap on concurrent server-authority lobbies (0 = unlimited).
    int MaxLobbies)
{
    public const long DefaultMaxScriptBytes = 1_048_576;
    public const long DefaultMaxWordFileBytes = 33_554_432;

    public static AuthorityOptions FromConfiguration(IConfiguration config) => new(
        config.GetValue("KnockBox:AuthorityEnabled", true),
        config.GetValue("KnockBox:AuthorityMaxMemoryBytes", 33_554_432L),
        TimeSpan.FromMilliseconds(config.GetValue("KnockBox:AuthorityCallTimeoutMs", 250)),
        config.GetValue("KnockBox:AuthorityMaxStatements", 1_000_000),
        config.GetValue("KnockBox:AuthorityRecursionLimit", 64),
        config.GetValue("KnockBox:AuthorityTickHzMax", 20.0),
        config.GetValue("KnockBox:AuthorityMaxScriptBytes", DefaultMaxScriptBytes),
        config.GetValue("KnockBox:AuthorityMaxWordFileBytes", DefaultMaxWordFileBytes),
        config.GetValue("KnockBox:AuthorityQueueCapacity", 256),
        config.GetValue("KnockBox:AuthorityMaxLobbies", 100));
}
