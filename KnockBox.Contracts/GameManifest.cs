namespace KnockBox.Contracts;

/// <summary>
/// The shape of a game's <c>GAME.json</c> manifest. A game is a content folder under
/// <c>games/</c>; the server discovers it at startup and never runs its logic.
/// Extra fields (description, author, version, …) can be added later without breaking this.
/// </summary>
/// <param name="CrossOriginIsolated">
/// Opt-in: when true the game-origin responses for this game carry
/// <c>Cross-Origin-Opener-Policy: same-origin</c> + <c>Cross-Origin-Embedder-Policy: require-corp</c>,
/// which threaded Godot/Unity web exports need for <c>SharedArrayBuffer</c>. Single-threaded
/// exports should leave this false to avoid the isolation cost.
/// </param>
/// <param name="ThemeColor">
/// Optional CSS color the shell tints the in-game header with, so the chrome feels part of the
/// game. When omitted the shell samples a dominant color from the thumbnail; when that fails too
/// the header keeps its default white. The shell validates this value before use, so an invalid
/// string is simply ignored (no CSS injection).
/// </param>
/// <param name="ThemeTextColor">
/// Optional CSS color for the header's text/icons. When omitted the shell auto-picks black or
/// white for contrast against the resolved <see cref="ThemeColor"/>. Also shell-validated.
/// </param>
/// <param name="ServerAuthority">
/// Opt-in to server-authoritative mode: the path (relative to the game folder) of the game's
/// authority module — pure game rules the SERVER executes in a sandbox, one instance per lobby
/// (see docs/SERVER_AUTHORITY_DESIGN.md). Currently only <c>.js</c> modules are supported. The
/// file is validated like <see cref="Entry"/> (must exist, no path traversal, plus a size cap);
/// a manifest that declares it but fails validation skips the whole game — a game that asked for
/// server-side enforcement is never silently downgraded to the cheatable host mode. The game
/// origin never serves this file.
/// </param>
public sealed record GameManifest(
    string Id,
    string Name,
    string Entry,
    string? Thumbnail,
    int MaxPlayers,
    bool CrossOriginIsolated = false,
    string? ThemeColor = null,
    string? ThemeTextColor = null,
    string? ServerAuthority = null);
