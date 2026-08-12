using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Marketplace;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The status matrix behind "is this plugin up to date?". Pure logic — no HTTP, no disk — because the
/// answer is what an operator acts on, and every branch of it should be pinned.
/// </summary>
public class PluginUpdateEvaluatorTests
{
    private static readonly SemVer App = new(1, 2, 0, null);

    private static MarketplaceCatalog Catalog(params MarketplacePlugin[] plugins) =>
        new("1.0.0", "Test", null, null, 1, plugins);

    private static MarketplacePlugin Entry(
        string id = "demo", string? version = "1.0.0", string? min = "1.0.0", string? max = null) =>
        new(id, "Demo", "A demo.", version, new MarketplaceAuthor("tester", null), null, min, max, null,
            new MarketplaceSource("github-release", "o/r", "v1", $"{id}.kbg", new string('a', 64), null, null));

    /// <summary>The installed side, as GameCatalog would hand it over.</summary>
    private static IReadOnlyDictionary<string, GameCatalog.GameLocation> Installed(params (string Id, string? Version)[] games) =>
        games.ToDictionary(
            g => g.Id,
            g => new GameCatalog.GameLocation(
                new GameManifest(g.Id, g.Id, "index.html", null, 2, Version: g.Version),
                Path.Combine("games", g.Id)),
            StringComparer.Ordinal);

    private static PluginStatus Only(MarketplacePlugin plugin, IReadOnlyDictionary<string, GameCatalog.GameLocation> installed) =>
        Assert.Single(PluginUpdateEvaluator.Evaluate(Catalog(plugin), installed, App));

    [Fact]
    public void Reports_not_installed_when_the_server_does_not_have_it()
    {
        var status = Only(Entry(version: "1.0.0"), Installed());

        Assert.Equal(PluginUpdateStatus.NotInstalled, status.Status);
        Assert.Null(status.Installed);
        Assert.Equal(new SemVer(1, 0, 0, null), status.Available);
    }

    [Fact]
    public void Reports_up_to_date_at_the_same_version()
    {
        var status = Only(Entry(version: "1.0.0"), Installed(("demo", "1.0.0")));

        Assert.Equal(PluginUpdateStatus.UpToDate, status.Status);
        Assert.Equal(new SemVer(1, 0, 0, null), status.Installed);
        Assert.Null(status.Reason);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    public void Reports_an_update_when_the_catalog_is_newer(string installed, string available)
    {
        var status = Only(Entry(version: available), Installed(("demo", installed)));
        Assert.Equal(PluginUpdateStatus.UpdateAvailable, status.Status);
    }

    [Fact]
    public void Reports_installed_ahead_rather_than_offering_a_downgrade()
    {
        // A local build, or a catalog rolled back. Either way "update available" would be a lie that
        // moves the operator backwards.
        var status = Only(Entry(version: "1.0.0"), Installed(("demo", "1.1.0")));

        Assert.Equal(PluginUpdateStatus.InstalledAhead, status.Status);
        Assert.Equal(new SemVer(1, 1, 0, null), status.Installed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("2026-08-11")]
    public void Reports_unknown_when_the_installed_copy_has_no_usable_version(string? installedVersion)
    {
        // Every hand-made game on a server is in this state. Calling them all "out of date" would
        // make the whole list noise, so it is its own status.
        var status = Only(Entry(version: "1.0.0"), Installed(("demo", installedVersion)));

        Assert.Equal(PluginUpdateStatus.InstalledVersionUnknown, status.Status);
        Assert.Null(status.Installed);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public void Refuses_a_plugin_that_needs_a_newer_server()
    {
        var status = Only(Entry(version: "2.0.0", min: "9.0.0"), Installed());

        Assert.Equal(PluginUpdateStatus.Incompatible, status.Status);
        Assert.Contains("9.0.0", status.Reason);
        Assert.Contains("1.2.0", status.Reason);
    }

    [Fact]
    public void Refuses_a_plugin_that_caps_the_server_version_below_ours()
    {
        var status = Only(Entry(version: "2.0.0", min: "1.0.0", max: "1.1.0"), Installed());

        Assert.Equal(PluginUpdateStatus.Incompatible, status.Status);
        Assert.Contains("1.1.0", status.Reason);
    }

    [Fact]
    public void Accepts_a_plugin_at_the_exact_bounds()
    {
        // Both bounds are inclusive; an off-by-one here would hide a game on the very server it was
        // published for.
        var status = Only(Entry(version: "2.0.0", min: "1.2.0", max: "1.2.0"), Installed());
        Assert.Equal(PluginUpdateStatus.NotInstalled, status.Status);
    }

    [Fact]
    public void Incompatibility_outranks_an_available_update()
    {
        // The point of the precedence: never invite an operator to install something that cannot run.
        var status = Only(Entry(version: "2.0.0", min: "9.0.0"), Installed(("demo", "1.0.0")));

        Assert.Equal(PluginUpdateStatus.Incompatible, status.Status);
        // The installed version still travels, so the UI can say what they are on today.
        Assert.Equal(new SemVer(1, 0, 0, null), status.Installed);
    }

    [Theory]
    [InlineData("not-a-version", null)]
    [InlineData(null, "not-a-version")]
    public void Treats_an_unreadable_app_version_bound_as_incompatible(string? min, string? max)
    {
        // A constraint we cannot read must not be treated as no constraint.
        var status = Only(Entry(version: "1.0.0", min: min, max: max), Installed());

        Assert.Equal(PluginUpdateStatus.Incompatible, status.Status);
        Assert.Contains("not a semantic version", status.Reason);
    }

    [Fact]
    public void Treats_an_absent_min_app_version_as_unconstrained()
    {
        var status = Only(Entry(version: "1.0.0", min: null), Installed());
        Assert.Equal(PluginUpdateStatus.NotInstalled, status.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Reports_an_entry_with_no_id_as_unusable(string? id)
    {
        var status = Only(Entry(id: id!), Installed());

        Assert.Equal(PluginUpdateStatus.Unusable, status.Status);
        Assert.Contains("no id", status.Reason);
    }

    [Fact]
    public void Reports_an_entry_with_an_unreadable_version_as_unusable()
    {
        // Surfaced rather than dropped: a broken published entry should be visible to whoever can
        // get it fixed, not silently missing from the list.
        var status = Only(Entry(version: "latest"), Installed());

        Assert.Equal(PluginUpdateStatus.Unusable, status.Status);
        Assert.Contains("latest", status.Reason);
    }

    [Fact]
    public void Judges_every_entry_and_preserves_catalog_order()
    {
        var catalog = Catalog(
            Entry("a", "1.0.0"),
            Entry("b", "2.0.0"),
            Entry("c", "1.0.0"),
            Entry("d", "1.0.0", min: "9.0.0"));

        var statuses = PluginUpdateEvaluator.Evaluate(catalog, Installed(("b", "1.0.0"), ("c", "1.0.0")), App);

        Assert.Equal(["a", "b", "c", "d"], statuses.Select(s => s.Id));
        Assert.Equal([
            PluginUpdateStatus.NotInstalled,
            PluginUpdateStatus.UpdateAvailable,
            PluginUpdateStatus.UpToDate,
            PluginUpdateStatus.Incompatible,
        ], statuses.Select(s => s.Status));
    }

    [Fact]
    public void Handles_a_catalog_with_no_plugins()
    {
        Assert.Empty(PluginUpdateEvaluator.Evaluate(Catalog(), Installed(), App));
        Assert.Empty(PluginUpdateEvaluator.Evaluate(
            new MarketplaceCatalog("1.0.0", "Test", null, null, 1, null), Installed(), App));
    }

    [Fact]
    public void Matches_installed_ids_case_sensitively()
    {
        // Game ids name a directory, and on Linux "Demo" and "demo" are two different games.
        var status = Only(Entry("Demo"), Installed(("demo", "1.0.0")));
        Assert.Equal(PluginUpdateStatus.NotInstalled, status.Status);
    }
}
