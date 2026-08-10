namespace KnockBox.Server.Games;

/// <summary>
/// The <c>KBG.json</c> header of a <c>.kbg</c> package. See <c>docs/KBG_FORMAT.md</c> for the
/// normative field definitions.
/// </summary>
/// <remarks>
/// Every field is nullable/defaulted and nothing is validated here — a package is untrusted input, so
/// deserialization must never throw on a hostile or truncated header. <c>GamePackageReader</c> does
/// all the checking. Unknown JSON fields are ignored by design: that is what keeps v1 packages
/// readable as the format grows.
/// </remarks>
public sealed record GamePackageHeader(
    int FormatVersion,
    string? Id,
    string? Name,
    string? Version,
    string? PackedBy,
    string? PackedAt,
    IReadOnlyList<GamePackageFile>? Files);

/// <summary>One row of the header's <c>files</c> list: a logical game-folder path and how it is stored.</summary>
public sealed record GamePackageFile(
    string? Path,
    string? Encoding,
    long Size,
    string? Sha256);
