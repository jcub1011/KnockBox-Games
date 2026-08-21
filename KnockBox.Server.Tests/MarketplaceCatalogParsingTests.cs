using System.Text;
using KnockBox.Server.Marketplace;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Reading <c>CATALOG.json</c>. The catalog is fetched from the network and this server does not
/// control what it contains, so the rule is: a malformed document produces a named error, never an
/// unhandled exception and never a half-read catalog.
/// </summary>
public class MarketplaceCatalogParsingTests
{
    private static MarketplaceCatalog Parse(string json) => MarketplaceClient.Parse(Encoding.UTF8.GetBytes(json));

    private static string Message(string json) =>
        Assert.Throws<MarketplaceException>(() => Parse(json)).Message;

    /// <summary>
    /// The real published catalog, verbatim (revision 4, after the asset/sha256 fix). If the
    /// marketplace's own format drifts away from what this server reads, this is the test that says so.
    /// </summary>
    private const string LiveCatalog = """
    {
      "schemaVersion": "1.0.0",
      "name": "KnockBox Games Marketplace Catalog",
      "description": "Official catalog index of available game plugins for the KnockBox Marketplace.",
      "lastUpdated": "2026-08-12T17:23:47.371Z",
      "revision": 4,
      "plugins": [
        {
          "id": "jcub1011-Alpha-Chain",
          "name": "Alpha Chain",
          "description": "A multiplayer, shinitori-esq game about building the most broken word scoring engine.",
          "version": "0.1.0",
          "author": { "name": "jcub1011" },
          "lastUpdated": "2026-08-12T17:23:47.371Z",
          "minAppVersion": "1.0.0",
          "tags": ["word-game", "party", "multiplayer"],
          "source": {
            "type": "github-release",
            "repo": "jcub1011/Alpha-Chain-Phaser-",
            "tag": "v0.1.0",
            "asset": "jcub1011-Alpha-Chain.kbg",
            "sha256": "76f72e5079494e883c0717e7501367f830c42fbed0127b0eb9326aca0a618f4c",
            "size": 2319262
          }
        }
      ]
    }
    """;

    [Fact]
    public void Reads_the_published_catalog()
    {
        var catalog = Parse(LiveCatalog);

        Assert.Equal("1.0.0", catalog.SchemaVersion);
        Assert.Equal(4, catalog.Revision);
        var plugin = Assert.Single(catalog.Plugins!);

        Assert.Equal("jcub1011-Alpha-Chain", plugin.Id);
        Assert.Equal("Alpha Chain", plugin.Name);
        Assert.Equal("0.1.0", plugin.Version);
        Assert.Equal("jcub1011", plugin.Author?.Name);
        Assert.Equal("1.0.0", plugin.MinAppVersion);
        Assert.Null(plugin.MaxAppVersion);
        Assert.Equal(["word-game", "party", "multiplayer"], plugin.Tags);

        Assert.Equal("github-release", plugin.Source?.Type);
        Assert.Equal("jcub1011/Alpha-Chain-Phaser-", plugin.Source?.Repo);
        Assert.Equal("v0.1.0", plugin.Source?.Tag);
        Assert.Equal("jcub1011-Alpha-Chain.kbg", plugin.Source?.Asset);
        Assert.Equal("76f72e5079494e883c0717e7501367f830c42fbed0127b0eb9326aca0a618f4c", plugin.Source?.Sha256);
        Assert.Equal(2319262, plugin.Source?.Size);
    }

    [Fact]
    public void Reads_an_author_given_as_a_bare_string()
    {
        // The schema allows either shape, and the sync action copies whatever GAME.json declares.
        var catalog = Parse(MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(Author: "\"jcub1011\"")));

        Assert.Equal("jcub1011", Assert.Single(catalog.Plugins!).Author?.Name);
        Assert.Null(Assert.Single(catalog.Plugins!).Author?.Email);
    }

    [Fact]
    public void Reads_an_author_given_as_an_object()
    {
        var catalog = Parse(MarketplaceFixture.Catalog(
            new MarketplaceFixture.Entry(Author: """{ "name": "jcub1011", "email": "j@example.com" }""")));

        var author = Assert.Single(catalog.Plugins!).Author;
        Assert.Equal("jcub1011", author?.Name);
        Assert.Equal("j@example.com", author?.Email);
    }

    [Fact]
    public void Ignores_unknown_properties_in_an_author_object()
    {
        // Within a schema major, added fields must not break older servers.
        var catalog = Parse(MarketplaceFixture.Catalog(
            new MarketplaceFixture.Entry(Author: """{ "name": "j", "url": "https://example.com", "meta": { "a": [1] } }""")));

        Assert.Equal("j", Assert.Single(catalog.Plugins!).Author?.Name);
    }

    [Fact]
    public void Reads_an_absent_author_as_null()
    {
        var catalog = Parse(MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(Author: null)));
        Assert.Null(Assert.Single(catalog.Plugins!).Author);
    }

    [Fact]
    public void Rejects_an_author_that_is_neither_a_string_nor_an_object()
    {
        Assert.Contains("author", Message(MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(Author: "42"))));
    }

    [Fact]
    public void Accepts_a_newer_MINOR_schema_version()
    {
        // Backward compatibility within a major is the whole reason additive fields are safe.
        var catalog = Parse(MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(), schemaVersion: "1.7.3"));
        Assert.Equal("1.7.3", catalog.SchemaVersion);
    }

    [Fact]
    public void Refuses_a_newer_MAJOR_schema_version_with_an_upgrade_hint()
    {
        var message = Message(MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(), schemaVersion: "2.0.0"));

        Assert.Contains("2.0.0", message);
        Assert.Contains("upgrade", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("banana")]
    [InlineData("1")]
    public void Refuses_a_catalog_whose_schema_version_is_not_a_version(string? schemaVersion)
    {
        var json = MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(), schemaVersion: schemaVersion);
        Assert.Contains("schemaVersion", Message(json));
    }

    [Fact]
    public void Refuses_a_catalog_that_lists_one_id_twice()
    {
        // Otherwise "is it installed?" would depend on which duplicate happened to be read first.
        var json = MarketplaceFixture.Catalog([
            new MarketplaceFixture.Entry(Id: "demo"),
            new MarketplaceFixture.Entry(Id: "demo", Version: "2.0.0"),
        ]);

        Assert.Contains("more than once", Message(json));
    }

    [Fact]
    public void Allows_distinct_ids()
    {
        var json = MarketplaceFixture.Catalog([
            new MarketplaceFixture.Entry(Id: "a"),
            new MarketplaceFixture.Entry(Id: "b"),
        ]);

        Assert.Equal(2, Parse(json).Plugins!.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    public void Refuses_a_body_that_is_not_a_catalog_object(string json) =>
        Assert.Throws<MarketplaceException>(() => Parse(json));

    [Fact]
    public void Reads_null_as_an_empty_catalog_error()
    {
        Assert.Contains("empty", Message("null"));
    }

    [Fact]
    public void Tolerates_entries_missing_every_optional_field()
    {
        // Parsing must not throw on a sparse entry; judging it is PluginUpdateEvaluator's job, and
        // acting on it is MarketplaceClient's. This is the split that keeps hostile input contained.
        var json = MarketplaceFixture.Catalog(new MarketplaceFixture.Entry(
            Id: "demo", Name: null, Description: null, Version: null, Author: null,
            LastUpdated: null, MinAppVersion: null, SourceJson: "{}"));

        var plugin = Assert.Single(Parse(json).Plugins!);
        Assert.Equal("demo", plugin.Id);
        Assert.Null(plugin.Version);
        Assert.Null(plugin.Source?.Type);
    }

    [Fact]
    public void Tolerates_a_catalog_with_no_plugins_array()
    {
        var catalog = Parse("""{ "schemaVersion": "1.0.0", "name": "Empty", "lastUpdated": "2026-01-01T00:00:00Z" }""");
        Assert.Null(catalog.Plugins);
    }

    [Fact]
    public void Ignores_properties_added_by_a_newer_minor_schema()
    {
        var catalog = Parse("""
        {
          "schemaVersion": "1.4.0",
          "name": "Test",
          "lastUpdated": "2026-01-01T00:00:00Z",
          "revision": 9,
          "featured": ["demo"],
          "plugins": [{
            "id": "demo", "version": "1.0.0", "source": { "type": "github-release", "verified": true }
          }]
        }
        """);

        Assert.Equal(9, catalog.Revision);
        Assert.Equal("demo", Assert.Single(catalog.Plugins!).Id);
    }
}
