namespace KnockBox.Contracts;

/// <summary>
/// The shape of a game's <c>GAME.json</c> manifest. A game is a content folder under
/// <c>games/</c>; the server discovers it at startup and never runs its logic.
/// Extra fields (description, author, version, …) can be added later without breaking this.
/// </summary>
public sealed record GameManifest(
    string Id,
    string Name,
    string Entry,
    string? Thumbnail,
    int MinPlayers,
    int MaxPlayers);
