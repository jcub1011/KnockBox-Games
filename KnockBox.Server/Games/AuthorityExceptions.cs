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
public sealed class AuthorityConstraintException(string message, Exception? inner = null) : Exception(message, inner);
