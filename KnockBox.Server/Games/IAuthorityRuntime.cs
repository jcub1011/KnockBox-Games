namespace KnockBox.Server.Games;

/// <summary>Parsed <c>config</c> export of an authority module (static behavior knobs).</summary>
public sealed record AuthorityConfig(bool PerRecipient = false, double TickHz = 0);

/// <summary>
/// Deferred effects a module requested during its last invocation via the injected <c>kb</c>
/// capability object. kb.setOwner / kb.setLobbyOpen / kb.log only RECORD inside the constrained
/// call; the actor applies them after the invocation returns — host code (locks, sends, log sinks)
/// must not burn the module's CPU budget and get misclassified as a module failure, and ordering
/// stays sane (an OwnerChanged always follows the delta of the intent that triggered it). kb.now()
/// is a pure read and stays inline.
/// </summary>
public sealed record AuthorityEffects(
    string? SetOwner,
    bool? SetLobbyOpen,
    IReadOnlyList<(LogLevel Level, string Message)> Logs)
{
    public static readonly AuthorityEffects None = new(null, null, []);
}

/// <summary>
/// Executes one lobby's untrusted authority module. The one seam between "run sandboxed module
/// code" and everything shared (actor, wire, lifecycle, limits): the boundary is STRINGS OF JSON
/// in both directions — what the wire already carries — so backends (Jint now, WASM later) are
/// interchangeable and no CLR object graph ever crosses into a runtime. NOT thread-safe: every
/// member must be called from the actor's single drain task.
/// </summary>
public interface IAuthorityRuntime : IDisposable
{
    /// <summary>Load + instantiate the module (createAuthority(kb) + init(players)).
    /// Throws <see cref="AuthorityLoadException"/> on any failure.</summary>
    void Initialize(string playersJson);

    /// <summary>Hook names present as functions on the instantiated authority object
    /// (applyIntent/snapshot/onPlayerJoined/…/tick), so the actor knows which optional hooks and
    /// tick exist. (In JS these are properties of createAuthority's return value — only
    /// createAuthority and config are true module exports.)</summary>
    IReadOnlySet<string> Exports { get; }

    AuthorityConfig Config { get; }

    /// <summary>Invoke a hook with JSON-string args; returns the result as a JSON string ("null"
    /// for null/undefined). Throws <see cref="AuthorityScriptException"/> (contained) or
    /// <see cref="AuthorityConstraintException"/> (fatal — engine untrustworthy).</summary>
    string Invoke(string export, params string[] jsonArgs);

    /// <summary>Returns and clears the effects buffered during the last Initialize/Invoke. Call
    /// after every invocation — including a contained failure, so a partial batch is not
    /// misattributed to the next invocation.</summary>
    AuthorityEffects DrainEffects();
}
