namespace KnockBox.Server.Games;

/// <summary>
/// Loading or instantiating an authority module failed (bad syntax, relative import, missing
/// createAuthority/required hooks, malformed config, init threw, oversize file). Fatal at birth:
/// lobby creation fails with a clear error to the creator (design §7).
/// </summary>
public sealed class AuthorityLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The module threw inside one invocation. CONTAINED: the interpreter unwound one call and engine
/// state is still consistent — drop the intent, re-broadcast the snapshot so clients converge, and
/// keep the lobby alive (design §7).
/// </summary>
public sealed class AuthorityScriptException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A sandbox constraint tripped (memory / timeout / statements / recursion) or the engine failed in
/// an unclassifiable way. FATAL: the engine may be mid-mutation and its state is untrustworthy —
/// the lobby is closed loudly rather than limping (design §7).
/// </summary>
public sealed class AuthorityConstraintException(
    string message,
    AuthorityConstraintKind kind = AuthorityConstraintKind.Unclassified,
    Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Which budget the call blew, which is what decides whether the lobby survives it.</summary>
    public AuthorityConstraintKind Kind { get; } = kind;

    /// <summary>Whether dropping this one call is a defensible response.
    ///
    /// A timeout or a statement overrun leaves the engine itself intact — Jint unwound the call the same
    /// way it unwinds a module throw, which the contained path has always survived — so the damage is
    /// bounded to the module's own partially-updated state, and re-broadcasting the snapshot resyncs every
    /// client to whatever that is. A memory trip is different in kind: the engine may still be holding the
    /// heap that blew the cap, so continuing to feed it work is how a bad lobby becomes a bad server.
    /// Cancellation means the host is shutting the actor down and there is nothing to recover to.</summary>
    public bool IsRecoverable =>
        Kind is AuthorityConstraintKind.Timeout or AuthorityConstraintKind.Statements;
}

/// <summary>Which per-call budget a <see cref="AuthorityConstraintException"/> reports.</summary>
public enum AuthorityConstraintKind
{
    /// <summary>An engine failure that fits none of the budgets — state is untrustworthy, always fatal.</summary>
    Unclassified,
    /// <summary>Wall-clock <c>CallTimeout</c> (including a regex timeout, which derives from it).</summary>
    Timeout,
    /// <summary><c>MaxStatements</c>, when an operator has armed it.</summary>
    Statements,
    /// <summary><c>MaxMemoryBytes</c>.</summary>
    Memory,
    /// <summary>Recursion depth or native stack exhaustion.</summary>
    Recursion,
    /// <summary>Host-driven cancellation — the actor is stopping.</summary>
    Cancelled,
}
