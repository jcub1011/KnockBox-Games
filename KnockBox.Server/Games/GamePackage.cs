namespace KnockBox.Server.Games;

/// <summary>
/// Constants of the <c>.kbg</c> (KnockBox Game) package format. The normative specification lives in
/// <c>docs/KBG_FORMAT.md</c>; the writer side is <c>tools/pack-game/kbg.mjs</c>. Keep all three in
/// step.
/// </summary>
public static class GamePackage
{
    /// <summary>File extension of a game package, including the dot.</summary>
    public const string Extension = ".kbg";

    /// <summary>Search pattern matching package files in a games directory.</summary>
    public const string SearchPattern = "*" + Extension;

    /// <summary>
    /// Highest <c>formatVersion</c> this server understands. A package declaring more than this is
    /// rejected with an upgrade hint: v1 files stay readable forever, but reading a FUTURE version is
    /// explicitly a non-goal.
    /// </summary>
    public const int MaxFormatVersion = 1;

    /// <summary>Name of the header entry inside the archive. Always present, always stored.</summary>
    public const string HeaderEntryName = "KBG.json";

    /// <summary>The standard KnockBox manifest, at the archive root.</summary>
    public const string ManifestEntryName = "GAME.json";

    /// <summary>Suffix appended to a logical path to form the ZIP entry name of a Brotli payload.</summary>
    public const string BrotliSuffix = ".br";
}
