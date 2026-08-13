using KnockBox.Server.Admin;
using KnockBox.Server.Games;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Which marketplaces this server will fetch from: the built-in official one plus whatever the operator
/// registered, and the rules that keep a registration from being something the downloader would refuse.
/// </summary>
public class MarketplaceSourceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kb-sources-{Guid.NewGuid():N}");
    private readonly AdminSettingsStore _settings;
    private readonly MarketplaceSourceRegistry _registry;

    public MarketplaceSourceRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_dir, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_dir, "admin-settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        _settings = new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
        _registry = new MarketplaceSourceRegistry(
            new HttpClient(), MarketplaceOptions.Default, GamePackageLimits.Default, _settings,
            maxSources: 2, NullLoggerFactory.Instance);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static RegisteredMarketplace Source(
        string id = "community",
        string catalog = "https://example.com/CATALOG.json",
        string download = "https://example.com") =>
        new(id, "Community", catalog, download);

    [Fact]
    public void The_official_source_is_always_present_and_first()
    {
        Assert.Equal(MarketplaceSourceRegistry.OfficialId, _registry.Sources[0].Id);
        Assert.Equal(MarketplaceOptions.OfficialCatalogUrl, _registry.Sources[0].CatalogUrl);
    }

    [Fact]
    public void A_registered_source_appears_after_the_official_one()
    {
        _settings.UpsertSource(Source());

        Assert.Equal([MarketplaceSourceRegistry.OfficialId, "community"], _registry.Sources.Select(s => s.Id));
    }

    [Theory]
    [InlineData("http://example.com/CATALOG.json")]
    [InlineData("ftp://example.com/CATALOG.json")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    [InlineData("")]
    public void A_catalog_url_the_downloader_would_refuse_cannot_be_registered(string url)
    {
        // Validated with MarketplaceClient's own rule, not a second copy — a source that passes here must
        // be one the downloader will actually use.
        Assert.NotNull(_registry.Validate(Source(catalog: url)));
    }

    [Fact]
    public void Loopback_http_is_allowed_so_an_offline_mirror_or_a_test_can_be_registered()
    {
        Assert.Null(_registry.Validate(Source(
            catalog: "http://127.0.0.1:9000/CATALOG.json", download: "http://127.0.0.1:9000")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has spaces")]
    [InlineData("has/slash")]
    [InlineData("waaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaytoolongtobeanid")]
    public void An_id_that_is_not_route_safe_is_refused(string id)
    {
        Assert.NotNull(_registry.Validate(Source(id)));
    }

    [Fact]
    public void The_official_id_cannot_be_taken_over()
    {
        var why = _registry.Validate(Source(MarketplaceSourceRegistry.OfficialId));

        Assert.NotNull(why);
        Assert.Contains("Disable it instead", why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_or_overlong_name_is_refused()
    {
        Assert.NotNull(_registry.Validate(Source() with { Name = "" }));
        Assert.NotNull(_registry.Validate(Source() with { Name = new string('x', 65) }));
    }

    [Fact]
    public void Registrations_are_capped()
    {
        Assert.Null(_registry.Validate(Source("one")));
        _settings.UpsertSource(Source("one"));
        _settings.UpsertSource(Source("two"));

        Assert.NotNull(_registry.Validate(Source("three")));
        // Editing one that already exists is still allowed at the cap — otherwise an operator at the
        // limit could not disable or correct a source without deleting it first.
        Assert.Null(_registry.Validate(Source("two")));
    }

    [Fact]
    public void A_source_gets_its_own_client_carrying_its_own_urls()
    {
        _settings.UpsertSource(Source());

        var official = _registry.For(MarketplaceSourceRegistry.OfficialId);
        var community = _registry.For("community");

        Assert.NotNull(official);
        Assert.NotNull(community);
        // One catalog + one ETag per client is why there is an instance per source rather than one
        // client parameterised by URL.
        Assert.NotSame(official, community);
        // Cached: asking twice must not throw away a fetched catalog.
        Assert.Same(community, _registry.For("community"));
    }

    [Fact]
    public void Changing_a_sources_urls_drops_its_cached_client()
    {
        _settings.UpsertSource(Source());
        var before = _registry.For("community");

        _settings.UpsertSource(Source(catalog: "https://elsewhere.example/CATALOG.json"));

        // The cached catalog was only ever valid for the URL it came from.
        Assert.NotSame(before, _registry.For("community"));
    }

    [Fact]
    public void An_unknown_source_has_no_client()
    {
        Assert.Null(_registry.For("nope"));
        Assert.Null(_registry.For(null));
    }

    [Fact]
    public void Registered_sources_survive_a_restart()
    {
        _settings.UpsertSource(Source());

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_dir, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_dir, "admin-settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        var reloaded = new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);

        var source = Assert.Single(reloaded.Sources);
        Assert.Equal("community", source.Id);
        Assert.Equal("https://example.com/CATALOG.json", source.CatalogUrl);
    }

    [Fact]
    public void A_hand_edited_row_that_is_not_usable_is_dropped_rather_than_failing_the_file()
    {
        File.WriteAllText(Path.Combine(_dir, "admin-settings.json"),
            """
            {
              "maintenanceMode": true,
              "sources": [
                { "id": "bad url", "name": "Nope", "catalogUrl": "ftp://x/y", "downloadBaseUrl": "ftp://x" },
                { "id": "good", "name": "Good", "catalogUrl": "https://x.example/c.json", "downloadBaseUrl": "https://x.example" }
              ]
            }
            """);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_dir, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_dir, "admin-settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        var store = new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);

        // One bad row must not cost the operator the rest of the file — the same rule the game overrides
        // already follow.
        Assert.Null(store.LoadError);
        Assert.True(store.MaintenanceMode);
        Assert.Equal("good", Assert.Single(store.Sources).Id);
    }

    [Fact]
    public void Removing_a_source_reports_whether_there_was_one()
    {
        _settings.UpsertSource(Source());

        Assert.True(_settings.RemoveSource("community", out _));
        Assert.False(_settings.RemoveSource("community", out _));
        Assert.Empty(_settings.Sources);
    }
}
