using KnockBox.Contracts;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Marketplace;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Merging what the registered catalogs offer with what is actually installed. Pure, so the precedence
/// rules and the near-identical states are testable without an HTTP stub.
/// </summary>
public class MarketplaceProjectionTests
{
    private static readonly SemVer App = new(1, 0, 0, null);

    private static RegisteredMarketplace Source(string id, bool enabled = true) =>
        new(id, $"{id} marketplace", "https://example.com/CATALOG.json", "https://example.com", enabled);

    private static MarketplacePlugin Plugin(string id, string? version = "1.0.0", string? name = null) =>
        new(id, name ?? id, $"The {id} game", version, new MarketplaceAuthor("Someone", null),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, ["party"],
            new MarketplaceSource("github-release", "owner/repo", "v1", $"{id}.kbg",
                new string('a', 64), 1234, null));

    private static SourceCatalog Catalog(RegisteredMarketplace source, params MarketplacePlugin[] plugins) =>
        new(source, new MarketplaceCatalog("1.0", "Test", null, null, 1, plugins), null);

    private static Dictionary<string, GameCatalog.GameLocation> Installed(
        params (string Id, string? Version)[] games) =>
        games.ToDictionary(
            g => g.Id,
            g => new GameCatalog.GameLocation(
                new GameManifest(g.Id, g.Id, "index.html", null, 4, Version: g.Version), $"/games/{g.Id}"),
            StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> Managed(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void An_offered_game_that_is_not_installed_is_reported_as_such()
    {
        var rows = MarketplaceProjection.Project(
            [Catalog(Source("official"), Plugin("alpha"))], Installed(), Managed(), App);

        var row = Assert.Single(rows);
        Assert.Equal("alpha", row.Id);
        Assert.Equal("notInstalled", row.Status);
        Assert.Equal("1.0.0", row.AvailableVersion);
        Assert.Null(row.InstalledVersion);
        Assert.False(row.Installed);
        Assert.Equal("official", row.SourceId);
    }

    [Fact]
    public void An_older_installed_version_is_reported_as_an_update()
    {
        var rows = MarketplaceProjection.Project(
            [Catalog(Source("official"), Plugin("alpha", "2.0.0"))],
            Installed(("alpha", "1.0.0")), Managed("alpha"), App);

        var row = Assert.Single(rows);
        Assert.Equal("updateAvailable", row.Status);
        Assert.Equal("1.0.0", row.InstalledVersion);
        Assert.Equal("2.0.0", row.AvailableVersion);
        Assert.True(row.Installed);
        Assert.True(row.Managed);
    }

    [Fact]
    public void A_managed_game_no_source_offers_is_still_listed()
    {
        // An upload, or an entry that was withdrawn. Leaving it out would make an uploaded game
        // invisible on the only page that can update or roll it back.
        var rows = MarketplaceProjection.Project(
            [], Installed(("mine", "1.0.0")), Managed("mine"), App);

        var row = Assert.Single(rows);
        Assert.Equal(MarketplaceProjection.InstalledOnly, row.Status);
        Assert.Equal("1.0.0", row.InstalledVersion);
        Assert.Null(row.AvailableVersion);
        Assert.True(row.Managed);
        Assert.Equal("", row.SourceId);
    }

    [Fact]
    public void An_installed_game_that_is_not_managed_and_not_offered_is_left_out()
    {
        // A hand-placed folder in games/. Nothing here can act on it, so offering a row with every
        // control disabled would be noise.
        var rows = MarketplaceProjection.Project(
            [], Installed(("handmade", "1.0.0")), Managed(), App);

        Assert.Empty(rows);
    }

    [Fact]
    public void The_first_source_to_offer_an_id_wins_and_the_loser_is_reported()
    {
        var rows = MarketplaceProjection.Project(
            [
                Catalog(Source("official"), Plugin("alpha", "1.0.0")),
                Catalog(Source("community"), Plugin("alpha", "9.9.9")),
            ],
            Installed(), Managed(), App);

        Assert.Equal(2, rows.Count);
        var winner = rows.Single(r => r.SourceId == "official");
        var loser = rows.Single(r => r.SourceId == "community");
        Assert.Null(winner.ShadowedBy);
        // Surfaced rather than silently dropped — the same discipline the installer applies to two
        // packages claiming one id.
        Assert.Equal("official", loser.ShadowedBy);
    }

    [Fact]
    public void A_source_that_failed_to_fetch_contributes_nothing_but_does_not_break_the_rest()
    {
        var rows = MarketplaceProjection.Project(
            [
                new SourceCatalog(Source("broken"), null, "the host could not be reached"),
                Catalog(Source("official"), Plugin("alpha")),
            ],
            Installed(), Managed(), App);

        var row = Assert.Single(rows);
        Assert.Equal("official", row.SourceId);
    }

    [Fact]
    public void An_entry_with_no_id_is_skipped()
    {
        var rows = MarketplaceProjection.Project(
            [Catalog(Source("official"), Plugin("alpha"), Plugin("", "1.0.0"))],
            Installed(), Managed(), App);

        Assert.Single(rows);
    }

    [Fact]
    public void An_installed_game_with_no_declared_version_is_distinguished_from_one_needing_an_update()
    {
        // Every hand-made game has no version; nagging about all of them as "update available" is noise,
        // which is why PluginUpdateEvaluator keeps the two apart.
        var rows = MarketplaceProjection.Project(
            [Catalog(Source("official"), Plugin("alpha", "2.0.0"))],
            Installed(("alpha", null)), Managed("alpha"), App);

        Assert.Equal("installedVersionUnknown", Assert.Single(rows).Status);
    }

    [Fact]
    public void Rows_are_sorted_by_name()
    {
        var rows = MarketplaceProjection.Project(
            [Catalog(Source("official"),
                Plugin("c", name: "Charlie"), Plugin("a", name: "Alpha"), Plugin("b", name: "Bravo"))],
            Installed(), Managed(), App);

        Assert.Equal(["Alpha", "Bravo", "Charlie"], rows.Select(r => r.Name));
    }

    [Fact]
    public void Catalog_metadata_is_carried_through_for_the_portal_to_render()
    {
        var rows = MarketplaceProjection.Project(
            [Catalog(Source("official"), Plugin("alpha"))], Installed(), Managed(), App);

        var row = Assert.Single(rows);
        Assert.Equal("The alpha game", row.Description);
        Assert.Equal("Someone", row.Author);
        Assert.Equal(["party"], row.Tags);
        Assert.Equal(1234, row.SizeBytes);
        Assert.Equal("official marketplace", row.SourceName);
    }
}
