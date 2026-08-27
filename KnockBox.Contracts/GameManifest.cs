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
/// <param name="Version">
/// Optional build label the game declares for itself, conventionally semantic-version shaped
/// (<c>"1.2.3"</c>, or <c>"1.2.3-beta.1"</c>). Purely informational to the server — it is never
/// validated and never affects whether a game loads, because a hand-written game is free to label
/// its builds however it likes. Its one consumer is the marketplace: an installed game's
/// <c>Version</c> is what an "is there a newer release?" check compares against the catalog, and a
/// value that isn't semver-parseable simply reports as an unknown installed version rather than
/// hiding the game. Packagers should set it — <c>knockbox-pack</c> copies it into the
/// <c>.kbg</c> header automatically so the two can't disagree.
/// </param>
/// <param name="Sdk">
/// Optional record of which KnockBox client addon versions this build was made against, as
/// <c>{ "godot": "1.0.0" }</c>. Written by <c>knockbox pack</c> from the game repo's
/// <c>knockbox.json</c>, so it reports what was actually installed rather than what the author
/// remembered.
///
/// Never validated and never affects whether a game loads — like <see cref="Version"/>, and for the
/// same reason: every hand-written game has no stamp at all, and a platform that refused those would
/// refuse most of what it hosts. Its consumer is the admin portal, which compares it against
/// <c>KnockBoxSdk</c> so an operator can see a game still running on an addon from three releases
/// ago. Absent is reported as <i>unknown</i>, deliberately distinct from <i>behind</i>.
/// </param>
/// <param name="AuthorityWords">
/// Optional immutable word dictionaries the game's authority module queries through
/// <c>kb.words</c> (validate a word, pick a word by index) — keyed by a game-chosen dictionary key.
/// Each declared file is loaded once into a shared, memory-efficient CLR structure and never
/// duplicated into a lobby's sandbox, so a large dictionary (a word list of hundreds of thousands
/// of entries) costs one copy for the whole process. Files are validated like
/// <see cref="ServerAuthority"/> (exist, no path traversal, size cap) and require
/// <see cref="ServerAuthority"/> to be set (words are server-only). The game origin never serves
/// these files — they are server-side data and, for hidden-information games, secret.
/// </param>
/// <param name="MinPlayers">
/// Minimum recommended player count for the game (defaults to 1).
/// </param>
/// <param name="Tags">
/// Optional list of category/genre tags for the game (e.g. "party", "drawing", "word-game").
/// </param>
/// <param name="Description">
/// Optional short description of the game.
/// </param>
/// <param name="CreatedAt">
/// Optional game creation/installation timestamp.
/// </param>
/// <param name="UpdatedAt">
/// Optional game last updated/modified timestamp.
/// </param>
/// <param name="License">
/// Optional SPDX license identifier or expression the game declares for itself (<c>"MIT"</c>,
/// <c>"Apache-2.0"</c>). Never validated and never affects whether a game loads — like
/// <see cref="Version"/>, and for the same reason: most hand-written games declare none, and a
/// platform that refused those would refuse most of what it hosts. Surfaced so an operator can see
/// the terms of something they installed without unpacking it.
/// </param>
/// <param name="ContentRating">
/// Optional self-declaration of what the game contains, for browsing and filtering — a platform
/// label (<c>"everyone"</c>, <c>"teen"</c>, <c>"mature"</c>), explicitly NOT an ESRB, PEGI or any
/// other legal classification.
///
/// Deliberately a <c>string</c> rather than an enum. An unknown value must not stop a game loading,
/// and an enum would either throw during deserialization or need a converter to avoid it — the same
/// reasoning that keeps every member of this record nullable. Absent is meaningfully distinct from
/// any declared value: it means the author never said, which a rating filter has to treat
/// differently from a game that positively declared itself suitable for everyone.
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
    string? ServerAuthority = null,
    IReadOnlyDictionary<string, AuthorityWordDeclaration>? AuthorityWords = null,
    string? Version = null,
    IReadOnlyDictionary<string, string>? Sdk = null,
    int MinPlayers = 1,
    IReadOnlyList<string>? Tags = null,
    string? Description = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? License = null,
    string? ContentRating = null);

/// <summary>
/// One entry in <see cref="GameManifest.AuthorityWords"/>: the game-relative path of a line-delimited
/// word list plus how words are matched.
/// </summary>
/// <param name="File">Path, relative to the game folder, of the line-delimited dictionary (one word
/// per line; blank/whitespace lines ignored).</param>
/// <param name="CaseInsensitive">When true (default) queries fold <c>A–Z</c> so <c>"Apple"</c> ==
/// <c>"apple"</c>; when false words match exactly. Words are ASCII-only either way.</param>
public sealed record AuthorityWordDeclaration(string File, bool CaseInsensitive = true);
