using System.Net;
using System.Text;
using KnockBox.Server.Games;
using KnockBox.Server.Marketplace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Entry = KnockBox.Server.Tests.MarketplaceFixture.Entry;

namespace KnockBox.Server.Tests;

/// <summary>
/// <see cref="MarketplaceClient"/> against a faked origin. Downloads are exercised with genuine
/// <c>.kbg</c> bytes from <see cref="PackageFixture"/>, so the verification path runs for real —
/// a stubbed body would let a break in it pass unnoticed.
/// </summary>
public class MarketplaceClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-market-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _http = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly GamePackageLimits Generous = new(100L * 1024 * 1024, 1000, 10_000);

    private MarketplaceClient New(MarketplaceOptions? options = null, GamePackageLimits? limits = null) =>
        new(_http.Client(), options ?? MarketplaceFixture.Options(), limits ?? Generous,
            NullLogger<MarketplaceClient>.Instance);

    /// <summary>Publishes a package at its release URL and returns the catalog entry that points at it.</summary>
    private MarketplacePlugin Publish(
        string id = "demo", string version = "1.0.0", byte[]? package = null,
        string? asset = null, string? sha256 = null, long? size = null)
    {
        package ??= MarketplaceFixture.Package(id, version);
        asset ??= $"{id}.kbg";
        _http.Map(MarketplaceFixture.AssetUrl(asset), package, contentType: "application/octet-stream");

        return Catalogued(new Entry(
            Id: id, Version: version,
            SourceJson: MarketplaceFixture.Source(asset, sha256 ?? MarketplaceFixture.Sha256(package), size)));
    }

    /// <summary>Serves a catalog containing <paramref name="entry"/> and returns the parsed plugin.</summary>
    private MarketplacePlugin Catalogued(Entry entry)
    {
        var json = MarketplaceFixture.Catalog(entry);
        _http.Map(MarketplaceFixture.CatalogUrl, json);
        return MarketplaceClient.Parse(Encoding.UTF8.GetBytes(json)).Plugins![0];
    }

    private async Task<string> FailureFor(MarketplacePlugin plugin, MarketplaceOptions? options = null) =>
        (await Assert.ThrowsAsync<MarketplaceException>(() => New(options).DownloadAsync(plugin, _dir))).Message;

    // ---- catalog fetching -------------------------------------------------------------------

    [Fact]
    public async Task Fetches_and_parses_the_catalog()
    {
        _http.Map(MarketplaceFixture.CatalogUrl, MarketplaceFixture.Catalog(new Entry(Id: "demo", Version: "1.2.3")));

        var catalog = await New().GetCatalogAsync();

        Assert.Equal("demo", Assert.Single(catalog.Plugins!).Id);
        Assert.Equal("1.2.3", catalog.Plugins![0].Version);
    }

    [Fact]
    public async Task Serves_the_cached_catalog_when_the_origin_reports_304()
    {
        var body = Encoding.UTF8.GetBytes(MarketplaceFixture.Catalog(new Entry(Version: "1.2.3")));
        _http.MapConditional(MarketplaceFixture.CatalogUrl, body, "\"rev-4\"");

        var client = New();
        var first = await client.GetCatalogAsync();
        var second = await client.GetCatalogAsync();

        // Same content, and the second call sent a conditional request rather than re-reading a body.
        Assert.Equal("1.2.3", second.Plugins![0].Version);
        Assert.Same(first, second);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains("If-None-Match", _http.Requests[1].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Re_reads_the_catalog_when_a_refresh_is_forced()
    {
        var body = Encoding.UTF8.GetBytes(MarketplaceFixture.Catalog(new Entry()));
        _http.MapConditional(MarketplaceFixture.CatalogUrl, body, "\"rev-4\"");

        var client = New();
        await client.GetCatalogAsync();
        await client.GetCatalogAsync(forceRefresh: true);

        Assert.DoesNotContain("If-None-Match", _http.Requests[1].Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task Reports_an_http_failure_on_the_catalog_with_the_url_and_status()
    {
        _http.MapStatus(MarketplaceFixture.CatalogUrl, HttpStatusCode.ServiceUnavailable);

        var message = (await Assert.ThrowsAsync<MarketplaceException>(() => New().GetCatalogAsync())).Message;

        Assert.Contains("503", message);
        Assert.Contains(MarketplaceFixture.CatalogUrl, message);
    }

    [Fact]
    public async Task Reports_an_unreachable_catalog_host()
    {
        _http.MapUnreachable(MarketplaceFixture.CatalogUrl);

        var message = (await Assert.ThrowsAsync<MarketplaceException>(() => New().GetCatalogAsync())).Message;
        Assert.Contains("could not reach", message);
    }

    [Fact]
    public async Task Aborts_a_catalog_body_that_exceeds_the_cap()
    {
        // Bounded before it can pressure memory, and the message names the knob that governs it.
        _http.Map(MarketplaceFixture.CatalogUrl, new string('x', 4096));
        var options = MarketplaceFixture.Options() with { MaxCatalogBytes = 512 };

        var message = (await Assert.ThrowsAsync<MarketplaceException>(() => New(options).GetCatalogAsync())).Message;

        Assert.Contains("512-byte limit", message);
        Assert.Contains("MarketplaceMaxCatalogBytes", message);
    }

    [Fact]
    public async Task Times_out_a_catalog_fetch_that_never_answers()
    {
        _http.MapHang(MarketplaceFixture.CatalogUrl);
        var options = MarketplaceFixture.Options() with { CatalogTimeout = TimeSpan.FromMilliseconds(150) };

        var message = (await Assert.ThrowsAsync<MarketplaceException>(() => New(options).GetCatalogAsync())).Message;

        Assert.Contains("timed out", message);
        Assert.Contains("MarketplaceCatalogTimeoutSeconds", message);
    }

    [Fact]
    public async Task A_caller_cancelling_is_not_reported_as_a_timeout()
    {
        _http.MapHang(MarketplaceFixture.CatalogUrl);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => New().GetCatalogAsync(false, cts.Token));
    }

    [Fact]
    public async Task Refuses_a_catalog_url_that_is_not_https()
    {
        var options = MarketplaceFixture.Options() with { CatalogUrl = "http://marketplace.test/CATALOG.json" };

        var message = (await Assert.ThrowsAsync<MarketplaceException>(() => New(options).GetCatalogAsync())).Message;

        Assert.Contains("MarketplaceCatalogUrl", message);
        Assert.Empty(_http.Requests);
    }

    // ---- downloading ------------------------------------------------------------------------

    [Fact]
    public async Task Downloads_verifies_and_hands_back_a_package()
    {
        var package = MarketplaceFixture.Package("demo", "1.0.0");
        var plugin = Publish("demo", "1.0.0", package);

        using var downloaded = await New().DownloadAsync(plugin, _dir);

        Assert.Equal("demo", downloaded.Id);
        Assert.Equal("1.0.0", downloaded.Version);
        Assert.Equal(package.Length, downloaded.Bytes);
        Assert.Equal(MarketplaceFixture.Sha256(package), downloaded.Sha256);
        Assert.True(File.Exists(downloaded.Path));
        Assert.Equal(package, await File.ReadAllBytesAsync(downloaded.Path));
        Assert.EndsWith(GamePackage.Extension, downloaded.Path);
    }

    [Fact]
    public async Task Builds_the_download_url_from_the_catalog_rather_than_taking_one()
    {
        var plugin = Publish("demo");
        using var _ = await New().DownloadAsync(plugin, _dir);

        Assert.Equal(
            $"{MarketplaceFixture.DownloadBase}/{MarketplaceFixture.Repo}/releases/download/{MarketplaceFixture.Tag}/demo.kbg",
            _http.Requests[^1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Disposing_a_download_removes_the_file()
    {
        var downloaded = await New().DownloadAsync(Publish(), _dir);
        var path = downloaded.Path;

        downloaded.Dispose();
        Assert.False(File.Exists(path));

        downloaded.Dispose(); // idempotent: a failure path must not be handed a second exception
    }

    [Fact]
    public async Task Refuses_a_package_whose_hash_is_not_the_one_published()
    {
        // The substitution the hash exists to catch: a release asset replaced in place after it was
        // catalogued. The catalog's commit history is the trust root, not the release.
        var plugin = Publish("demo", package: MarketplaceFixture.Package("demo"), sha256: new string('a', 64));

        var message = await FailureFor(plugin);

        Assert.Contains("SHA-256", message);
        Assert.Contains("discarded", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_an_entry_that_publishes_no_hash()
    {
        var package = MarketplaceFixture.Package();
        _http.Map(MarketplaceFixture.AssetUrl("demo.kbg"), package);
        var plugin = Catalogued(new Entry(SourceJson: MarketplaceFixture.Source("demo.kbg", sha256: null)));

        Assert.Contains("no usable sha256", await FailureFor(plugin));
        Assert.Empty(_http.Requests); // refused before any request left the process
    }

    [Fact]
    public async Task Refuses_an_asset_that_is_not_a_kbg()
    {
        // The bug this feature was built around: the catalog used to name GAME.json here.
        var plugin = Catalogued(new Entry(
            SourceJson: MarketplaceFixture.Source("GAME.json", sha256: new string('a', 64))));

        var message = await FailureFor(plugin);

        Assert.Contains("GAME.json", message);
        Assert.Contains(".kbg", message);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("../../../etc/passwd.kbg")]
    [InlineData("sub/dir/demo.kbg")]
    [InlineData("demo.kbg/../../x.kbg")]
    [InlineData("..\\demo.kbg")]
    public async Task Refuses_an_asset_name_that_could_reshape_the_url(string asset)
    {
        var plugin = Catalogued(new Entry(SourceJson: MarketplaceFixture.Source(asset, sha256: new string('a', 64))));

        await FailureFor(plugin);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("owner")]                 // not owner/repo
    [InlineData("owner/repo/extra")]
    [InlineData("../owner/repo")]
    [InlineData("owner/repo?x=1")]
    [InlineData("evil.test/owner/repo")]
    public async Task Refuses_a_repo_that_could_reshape_the_url(string repo)
    {
        var plugin = Catalogued(new Entry(
            SourceJson: MarketplaceFixture.Source("demo.kbg", new string('a', 64), repo: repo)));

        await FailureFor(plugin);
        Assert.Empty(_http.Requests);
    }

    [Theory]
    [InlineData("../v1")]
    [InlineData("v1/../../x")]
    [InlineData("..")]
    [InlineData("v 1")]
    public async Task Refuses_a_tag_that_could_reshape_the_url(string tag)
    {
        var plugin = Catalogued(new Entry(
            SourceJson: MarketplaceFixture.Source("demo.kbg", new string('a', 64), tag: tag)));

        await FailureFor(plugin);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Refuses_a_source_type_it_cannot_install()
    {
        var plugin = Catalogued(new Entry(
            SourceJson: MarketplaceFixture.Source("demo.kbg", new string('a', 64), type: "local-path")));

        var message = await FailureFor(plugin);

        Assert.Contains("local-path", message);
        Assert.Contains("github-release", message);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Reports_a_release_asset_that_no_longer_exists()
    {
        var plugin = Catalogued(new Entry(SourceJson: MarketplaceFixture.Source("demo.kbg", new string('a', 64))));
        _http.MapStatus(MarketplaceFixture.AssetUrl("demo.kbg"), HttpStatusCode.NotFound);

        var message = await FailureFor(plugin);

        Assert.Contains("404", message);
        Assert.Contains("no longer exists", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Aborts_a_download_that_grows_past_the_cap_while_streaming()
    {
        // Enforced against bytes actually received, so understating Content-Length buys nothing.
        var package = MarketplaceFixture.Package("demo");
        _http.Map(MarketplaceFixture.AssetUrl("demo.kbg"), package, contentLength: 10);
        var plugin = Catalogued(new Entry(
            SourceJson: MarketplaceFixture.Source("demo.kbg", MarketplaceFixture.Sha256(package))));

        var message = await FailureFor(plugin, MarketplaceFixture.Options(maxDownloadBytes: 64));

        Assert.Contains("64-byte download limit", message);
        Assert.Contains("MarketplaceMaxDownloadBytes", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_before_downloading_when_the_catalog_advertises_an_oversized_package()
    {
        var package = MarketplaceFixture.Package("demo");
        var plugin = Publish("demo", package: package, size: 900_000_000);

        var message = await FailureFor(plugin, MarketplaceFixture.Options(maxDownloadBytes: 1024));

        Assert.Contains("900000000-byte package", message);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Discards_a_download_that_dies_mid_transfer()
    {
        var package = MarketplaceFixture.Package("demo");
        _http.MapTruncated(MarketplaceFixture.AssetUrl("demo.kbg"), package, package.Length / 2);
        var plugin = Catalogued(new Entry(
            SourceJson: MarketplaceFixture.Source("demo.kbg", MarketplaceFixture.Sha256(package))));

        await Assert.ThrowsAnyAsync<Exception>(() => New().DownloadAsync(plugin, _dir));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_bytes_that_are_not_an_archive_at_all()
    {
        var junk = Encoding.UTF8.GetBytes("this is not a zip archive");
        var plugin = Publish("demo", package: junk);

        var message = await FailureFor(plugin);

        Assert.Contains("readable archive", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_a_package_containing_a_different_game_than_advertised()
    {
        var package = MarketplaceFixture.Package("somethingelse", "1.0.0");
        var plugin = Publish("demo", "1.0.0", package);

        var message = await FailureFor(plugin);

        Assert.Contains("somethingelse", message);
        Assert.Contains("discarded", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_a_package_whose_version_disagrees_with_the_catalog()
    {
        // The catalog's version is generated FROM the package's GAME.json, so a disagreement means
        // the entry describes bytes it did not ship.
        var package = MarketplaceFixture.Package("demo", "2.0.0");
        var plugin = Publish("demo", "1.0.0", package);

        var message = await FailureFor(plugin);

        Assert.Contains("1.0.0", message);
        Assert.Contains("2.0.0", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_a_package_that_declares_no_version_when_the_catalog_does()
    {
        var package = MarketplaceFixture.Package("demo", version: null);
        var plugin = Publish("demo", "1.0.0", package);

        Assert.Contains("no version", await FailureFor(plugin));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_a_package_that_breaks_the_kbg_contract()
    {
        // Validation is the same GamePackageReader the installer uses — the marketplace does not get
        // its own, weaker copy of it. Proven by tripping a reader-level rule (the entry cap) that
        // nothing else in this class could produce, and asserting the reader's own wording.
        var package = MarketplaceFixture.Package("demo", "1.0.0");
        var plugin = Publish("demo", "1.0.0", package);

        var strict = Generous with { MaxEntries = 1 };
        var message = (await Assert.ThrowsAsync<MarketplaceException>(
            () => New(limits: strict).DownloadAsync(plugin, _dir))).Message;

        Assert.Contains("not a valid .kbg package", message);
        Assert.Contains("entries", message);
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Refuses_a_package_whose_manifest_is_not_json()
    {
        var broken = PackageFixture.Build("demo", "Demo", [
            new PackageFixture.File("GAME.json", PackageFixture.Bytes("{not json")),
            new PackageFixture.File("index.html", PackageFixture.Bytes("<!doctype html>")),
        ]);
        var plugin = Publish("demo", package: broken);

        Assert.Contains("not valid JSON", await FailureFor(plugin));
        AssertNothingLeftBehind();
    }

    [Fact]
    public async Task Names_the_file_after_its_content_so_repeat_downloads_are_idempotent()
    {
        // Content-addressed: the same bytes always land on the same path, so re-downloading replaces
        // rather than accumulating. Two different packages must not share a name.
        var client = New();
        var plugin = Publish("demo", "1.0.0");

        using var first = await client.DownloadAsync(plugin, _dir);
        using var again = await client.DownloadAsync(plugin, _dir);
        Assert.Equal(first.Path, again.Path);
        Assert.Single(Directory.GetFiles(_dir));

        var other = Publish("other", "1.0.0");
        using var second = await client.DownloadAsync(other, _dir);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Equal(2, Directory.GetFiles(_dir).Length);
    }

    // ---- repository releases fetching --------------------------------------------------------

    [Fact]
    public void ParseRepoReleases_extracts_all_versions_and_filters_drafts_and_assets()
    {
        var json = """
        [
          {
            "tag_name": "v1.0.0",
            "draft": false,
            "published_at": "2026-09-02T15:00:00Z",
            "assets": [
              {
                "name": "demo.kbg",
                "size": 2500000,
                "digest": "sha256:32e2ca3f35954dc9416664494b8e1ce7e5260a0217378cd03e73d3013b1329df"
              }
            ]
          },
          {
            "tag_name": "v0.9.0-draft",
            "draft": true,
            "assets": [
              {
                "name": "demo.kbg",
                "size": 1000
              }
            ]
          },
          {
            "tag_name": "v0.1.0",
            "draft": false,
            "published_at": "2026-08-11T12:00:00Z",
            "assets": [
              {
                "name": "GAME.json",
                "size": 500
              },
              {
                "name": "demo.kbg",
                "size": 2000000,
                "digest": "sha256:76f72e5079494e883c0717e7501367f830c42fbed0127b0eb9326aca0a618f4c"
              }
            ]
          }
        ]
        """;

        var releases = MarketplaceClient.ParseRepoReleases(Encoding.UTF8.GetBytes(json), "demo");
        Assert.Equal(2, releases.Count);

        var first = releases[0];
        Assert.Equal("1.0.0", first.Version);
        Assert.Equal("v1.0.0", first.Tag);
        Assert.Equal("demo.kbg", first.Asset);
        Assert.Equal(2500000, first.SizeBytes);
        Assert.Equal("32e2ca3f35954dc9416664494b8e1ce7e5260a0217378cd03e73d3013b1329df", first.Sha256);

        var second = releases[1];
        Assert.Equal("0.1.0", second.Version);
        Assert.Equal("v0.1.0", second.Tag);
        Assert.Equal("demo.kbg", second.Asset);
        Assert.Equal("76f72e5079494e883c0717e7501367f830c42fbed0127b0eb9326aca0a618f4c", second.Sha256);
    }

    [Fact]
    public async Task GetRepoReleasesAsync_fetches_and_caches_releases_from_repo()
    {
        var json = """
        [
          {
            "tag_name": "v1.0.0",
            "draft": false,
            "assets": [
              { "name": "alpha.kbg", "size": 1234, "digest": "sha256:1111222233334444555566667777888811112222333344445555666677778888" }
            ]
          }
        ]
        """;
        var client = New();
        var url = $"{MarketplaceFixture.DownloadBase}/repos/owner/alpha/releases";
        _http.Map(url, json);

        var releases = await client.GetRepoReleasesAsync("owner/alpha", "alpha");
        Assert.Single(releases);
        Assert.Equal("1.0.0", releases[0].Version);
        Assert.Single(_http.Requests);

        // Second call hits cache without calling HTTP again
        var cached = await client.GetRepoReleasesAsync("owner/alpha", "alpha");
        Assert.Single(cached);
        Assert.Equal("1.0.0", cached[0].Version);
        Assert.Single(_http.Requests);
    }

    /// <summary>No partial file, no stray <c>.part</c> — a rejected download leaves the directory clean.</summary>
    private void AssertNothingLeftBehind()
    {
        if (!Directory.Exists(_dir)) return;
        Assert.Empty(Directory.GetFiles(_dir, "*", SearchOption.AllDirectories));
    }
}
