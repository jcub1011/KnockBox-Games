namespace KnockBox.Server.Games;

/// <summary>
/// Resource ceilings applied when installing a <c>.kbg</c>. A package is untrusted input, so a
/// malicious or accidentally enormous one must be refused rather than allowed to fill the disk (a
/// "zip bomb": a few kilobytes of compressed data declaring gigabytes of output).
/// </summary>
/// <param name="MaxBytes">
/// Cap on the total UNCOMPRESSED size of a package. Enforced while copying, not from the sizes the
/// package declares — those are attacker-controlled.
/// </param>
/// <param name="MaxEntries">Cap on the number of archive entries.</param>
/// <param name="MaxRatio">
/// Cap on total-uncompressed ÷ archive-size. A legitimate game lands well under 10:1; anything at
/// hundreds-to-one is a bomb, not a game.
/// </param>
public sealed record GamePackageLimits(long MaxBytes, int MaxEntries, int MaxRatio)
{
    /// <summary>Defaults: 512 MiB, 20 000 entries, 200:1. Any value ≤ 0 disables that individual check.</summary>
    public static GamePackageLimits Default { get; } = new(512L * 1024 * 1024, 20_000, 200);

    public static GamePackageLimits FromConfiguration(IConfiguration config) => new(
        config.GetValue("KnockBox:MaxPackageBytes", Default.MaxBytes),
        config.GetValue("KnockBox:MaxPackageEntries", Default.MaxEntries),
        config.GetValue("KnockBox:MaxPackageRatio", Default.MaxRatio));
}
