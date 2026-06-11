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
public sealed record GameManifest(
    string Id,
    string Name,
    string Entry,
    string? Thumbnail,
    int MaxPlayers,
    bool CrossOriginIsolated = false);
