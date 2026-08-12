using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Marketplace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// End-to-end against the <b>real</b> marketplace and a real GitHub release.
/// </summary>
/// <remarks>
/// Skipped unless <c>KNOCKBOX_MARKETPLACE_LIVE=1</c>. Everything else in the suite runs against a
/// faked origin, which proves this server behaves correctly given a catalog — it cannot prove the
/// published catalog is one this server can actually use. That is what this covers, and it is worth
/// having precisely because the failure it guards against (a catalog entry pointing at the wrong
/// asset) is the bug that prompted the feature.
///
/// It is opt-in rather than always-on because a test that fails when GitHub is slow, or when the
/// machine is offline, teaches everyone to ignore red builds.
///
/// Run it with:
///   $env:KNOCKBOX_MARKETPLACE_LIVE=1
///   dotnet test KnockBox.Server.Tests --filter "FullyQualifiedName~MarketplaceLive"
/// </remarks>
public class MarketplaceLiveTests : IDisposable
{
    /// <summary>
    /// A <see cref="FactAttribute"/> that reports itself skipped unless the opt-in variable is set.
    /// </summary>
    /// <remarks>
    /// Setting <c>Skip</c> from the constructor is the no-dependency way to get a conditional test on
    /// xUnit 2.x — the alternative, an early <c>return</c>, reports as a PASS and would quietly claim
    /// coverage this run never had.
    /// </remarks>
    private sealed class LiveFactAttribute : FactAttribute
    {
        public LiveFactAttribute()
        {
            if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
                Skip = $"Set {EnableVariable}=1 to run tests that reach the real marketplace.";
        }
    }

    private const string EnableVariable = "KNOCKBOX_MARKETPLACE_LIVE";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kb-market-live-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static MarketplaceClient Client()
    {
        var options = MarketplaceOptions.Default;
        return new MarketplaceClient(
            MarketplaceClient.CreateHttpClient(options), options, GamePackageLimits.Default,
            NullLogger<MarketplaceClient>.Instance);
    }

    [LiveFact]
    public async Task Reads_the_official_catalog()
    {
        var catalog = await Client().GetCatalogAsync();

        Assert.NotNull(catalog.Plugins);
        Assert.NotEmpty(catalog.Plugins!);

        // Every published entry must be one this server could act on. A catalog that parses but whose
        // entries are all unusable is the exact failure this suite exists to notice.
        foreach (var plugin in catalog.Plugins!)
        {
            Assert.False(string.IsNullOrWhiteSpace(plugin.Id));
            Assert.True(SemVer.TryParse(plugin.Version, out _), $"'{plugin.Id}' publishes version '{plugin.Version}'.");
            Assert.Equal("github-release", plugin.Source?.Type);
            Assert.EndsWith(GamePackage.Extension, plugin.Source?.Asset ?? "");
            Assert.Equal(64, plugin.Source?.Sha256?.Length ?? 0);
        }
    }

    [LiveFact]
    public async Task Downloads_and_verifies_every_published_package()
    {
        var client = Client();
        var catalog = await client.GetCatalogAsync();

        foreach (var plugin in catalog.Plugins!)
        {
            using var package = await client.DownloadAsync(plugin, _dir);

            Assert.Equal(plugin.Id, package.Id);
            Assert.Equal(plugin.Version, package.Version);
            Assert.Equal(plugin.Source!.Sha256!.ToLowerInvariant(), package.Sha256);
            if (plugin.Source.Size is { } size) Assert.Equal(size, package.Bytes);
        }
    }

    [LiveFact]
    public async Task Judges_the_official_catalog_against_an_empty_server()
    {
        var catalog = await Client().GetCatalogAsync();
        var statuses = PluginUpdateEvaluator.Evaluate(catalog, new Dictionary<string, GameCatalog.GameLocation>(), KnockBoxVersion.Current);

        Assert.NotEmpty(statuses);
        // Nothing is installed, so everything should read as installable on a current server. Anything
        // else means a published entry declares an app-version range this build falls outside of.
        Assert.All(statuses, s => Assert.Equal(PluginUpdateStatus.NotInstalled, s.Status));
    }
}
