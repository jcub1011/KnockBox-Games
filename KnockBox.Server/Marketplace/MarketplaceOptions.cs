namespace KnockBox.Server.Marketplace;

/// <summary>
/// Operator knobs for the official game marketplace, under the <c>KnockBox:Marketplace*</c> prefix.
/// Same shape as <see cref="Games.GamePackageLimits"/> and <see cref="Games.AuthorityOptions"/>:
/// a record with a static <c>FromConfiguration</c>, defaults in code, no options binder.
/// </summary>
/// <param name="Enabled">
/// Master switch. Off ⇒ nothing marketplace-related is registered and the server makes no outbound
/// requests at all, which is the right posture for an air-gapped or locked-down deployment.
/// </param>
/// <param name="CatalogUrl">
/// Where the catalog index lives. Points at the official marketplace by default; overriding it is
/// how an organisation runs its own. Must be HTTPS (or loopback).
/// </param>
/// <param name="DownloadBaseUrl">
/// Origin that release download URLs are built on. Package URLs are always *derived*
/// (<c>{base}/{repo}/releases/download/{tag}/{asset}</c>) and never taken from the catalog, so a
/// tampered entry cannot point this server at an arbitrary host.
/// </param>
/// <param name="MaxCatalogBytes">
/// Cap on the catalog response body. A catalog is tens of kilobytes; the cap exists so a
/// compromised or misbehaving host cannot stream an unbounded body into memory.
/// </param>
/// <param name="MaxDownloadBytes">
/// Cap on a downloaded <c>.kbg</c>, enforced against bytes actually received. Defaults to the same
/// 512 MiB ceiling <see cref="Games.GamePackageLimits"/> applies at install time — a package too
/// large to install should not be worth downloading.
/// </param>
/// <param name="CatalogTimeout">Overall timeout for fetching the catalog.</param>
/// <param name="DownloadTimeout">
/// Overall timeout for one package download. Generous: packages run to hundreds of megabytes and
/// operators are not always on fast links.
/// </param>
public sealed record MarketplaceOptions(
    bool Enabled,
    string CatalogUrl,
    string DownloadBaseUrl,
    long MaxCatalogBytes,
    long MaxDownloadBytes,
    TimeSpan CatalogTimeout,
    TimeSpan DownloadTimeout)
{
    /// <summary>The official KnockBox marketplace catalog.</summary>
    public const string OfficialCatalogUrl =
        "https://raw.githubusercontent.com/jcub1011/KnockBox-Games-Marketplace/main/.plugins/CATALOG.json";

    public static MarketplaceOptions Default { get; } = new(
        Enabled: true,
        CatalogUrl: OfficialCatalogUrl,
        DownloadBaseUrl: "https://github.com",
        MaxCatalogBytes: 4L * 1024 * 1024,
        MaxDownloadBytes: Games.GamePackageLimits.Default.MaxBytes,
        CatalogTimeout: TimeSpan.FromSeconds(30),
        DownloadTimeout: TimeSpan.FromMinutes(10));

    public static MarketplaceOptions FromConfiguration(IConfiguration config) => new(
        config.GetValue("KnockBox:MarketplaceEnabled", Default.Enabled),
        config["KnockBox:MarketplaceCatalogUrl"] is { Length: > 0 } url ? url : Default.CatalogUrl,
        (config["KnockBox:MarketplaceDownloadBaseUrl"] is { Length: > 0 } b ? b : Default.DownloadBaseUrl)
            .TrimEnd('/'),
        config.GetValue("KnockBox:MarketplaceMaxCatalogBytes", Default.MaxCatalogBytes),
        config.GetValue("KnockBox:MarketplaceMaxDownloadBytes", Default.MaxDownloadBytes),
        TimeSpan.FromSeconds(config.GetValue("KnockBox:MarketplaceCatalogTimeoutSeconds",
            (int)Default.CatalogTimeout.TotalSeconds)),
        TimeSpan.FromSeconds(config.GetValue("KnockBox:MarketplaceDownloadTimeoutSeconds",
            (int)Default.DownloadTimeout.TotalSeconds)));
}
