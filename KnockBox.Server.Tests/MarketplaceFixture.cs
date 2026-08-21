using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnockBox.Server.Marketplace;

namespace KnockBox.Server.Tests;

/// <summary>
/// Builds marketplace catalogs and the fake releases they point at.
/// </summary>
/// <remarks>
/// Packages come from <see cref="PackageFixture"/>, so a download test verifies genuine <c>.kbg</c>
/// bytes through the genuine reader — a hand-waved body would let a bug in the validation path pass
/// unnoticed. Hashes are computed from those same bytes rather than written by hand, so a fixture
/// can't drift out of agreement with itself.
///
/// The catalog JSON is emitted as text rather than serialized from the DTOs on purpose: these tests
/// are partly about whether we can read what the marketplace actually publishes, and round-tripping
/// our own types would only prove we agree with ourselves.
/// </remarks>
internal static class MarketplaceFixture
{
    public const string CatalogUrl = "https://marketplace.test/CATALOG.json";
    public const string DownloadBase = "https://downloads.test";
    public const string Repo = "jcub1011/Alpha-Chain-Phaser-";
    public const string Tag = "v0.1.0";

    /// <summary>The URL <c>MarketplaceClient</c> derives for a release asset.</summary>
    public static string AssetUrl(string asset, string repo = Repo, string tag = Tag) =>
        $"{DownloadBase}/{repo}/releases/download/{tag}/{asset}";

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// A real, well-formed <c>.kbg</c> whose <b>GAME.json</b> declares <paramref name="version"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="PackageFixture.Valid"/>'s <c>version</c> stamps the KBG.json <i>header</i>, which is
    /// a different field: the marketplace compares the manifest's version, because that is the one the
    /// catalog is generated from. Both are set here, matching what the real packer now emits.
    /// </remarks>
    public static byte[] Package(string id = "demo", string? version = "1.0.0", string? name = null)
    {
        var manifest = new StringBuilder();
        manifest.Append($$"""{"id":{{Json(id)}},"name":{{Json(name ?? id)}},"entry":"index.html","maxPlayers":2""");
        if (version is not null) manifest.Append($",\"version\":{Json(version)}");
        manifest.Append('}');

        return PackageFixture.Build(id, name ?? id, [
            new PackageFixture.File("GAME.json", Encoding.UTF8.GetBytes(manifest.ToString())),
            new PackageFixture.File("index.html", PackageFixture.Bytes("<!doctype html><title>demo</title>")),
        ], version: version);
    }

    /// <summary>Options pointed at the fixture's fake origins, with generous limits unless overridden.</summary>
    public static MarketplaceOptions Options(long maxDownloadBytes = 64 * 1024 * 1024, long maxCatalogBytes = 1024 * 1024) =>
        MarketplaceOptions.Default with
        {
            CatalogUrl = CatalogUrl,
            DownloadBaseUrl = DownloadBase,
            MaxCatalogBytes = maxCatalogBytes,
            MaxDownloadBytes = maxDownloadBytes,
        };

    /// <summary>
    /// One catalog entry. Every field is overridable so a test can break exactly one rule; passing
    /// <c>null</c> for an optional field omits it from the JSON entirely, which is a different case
    /// from present-but-empty.
    /// </summary>
    public sealed record Entry(
        string? Id = "demo",
        string? Name = "Demo",
        string? Description = "A demo game.",
        string? Version = "1.0.0",
        string? Author = "\"jcub1011\"",
        string? LastUpdated = "2026-08-11T16:14:37.766Z",
        string? MinAppVersion = "1.0.0",
        string? MaxAppVersion = null,
        string[]? Tags = null,
        string? SourceJson = null);

    /// <summary>Renders a <c>source</c> object for a github-release entry.</summary>
    public static string Source(
        string asset = "demo.kbg", string? sha256 = null, long? size = null,
        string repo = Repo, string tag = Tag, string type = "github-release")
    {
        var parts = new List<string>
        {
            $"\"type\": {Json(type)}",
            $"\"repo\": {Json(repo)}",
            $"\"tag\": {Json(tag)}",
            $"\"asset\": {Json(asset)}",
        };
        if (sha256 is not null) parts.Add($"\"sha256\": {Json(sha256)}");
        if (size is { } s) parts.Add($"\"size\": {s}");
        return "{ " + string.Join(", ", parts) + " }";
    }

    /// <summary>Renders a whole catalog document.</summary>
    public static string Catalog(
        IEnumerable<Entry> entries, string? schemaVersion = "1.0.0", int revision = 1)
    {
        var body = new StringBuilder();
        body.Append("{\n");
        if (schemaVersion is not null) body.Append($"  \"schemaVersion\": {Json(schemaVersion)},\n");
        body.Append("  \"name\": \"Test Catalog\",\n");
        body.Append("  \"lastUpdated\": \"2026-08-11T16:14:37.766Z\",\n");
        body.Append($"  \"revision\": {revision},\n");
        body.Append("  \"plugins\": [");

        var first = true;
        foreach (var entry in entries)
        {
            if (!first) body.Append(',');
            first = false;
            body.Append('\n').Append(Render(entry));
        }
        body.Append("\n  ]\n}\n");
        return body.ToString();
    }

    /// <summary>A one-entry catalog, the common case.</summary>
    public static string Catalog(Entry entry, string? schemaVersion = "1.0.0", int revision = 1) =>
        Catalog([entry], schemaVersion, revision);

    private static string Render(Entry e)
    {
        var fields = new List<string>();
        void Add(string name, string? raw) { if (raw is not null) fields.Add($"      \"{name}\": {raw}"); }

        Add("id", e.Id is null ? null : Json(e.Id));
        Add("name", e.Name is null ? null : Json(e.Name));
        Add("description", e.Description is null ? null : Json(e.Description));
        Add("version", e.Version is null ? null : Json(e.Version));
        Add("author", e.Author);
        Add("lastUpdated", e.LastUpdated is null ? null : Json(e.LastUpdated));
        Add("minAppVersion", e.MinAppVersion is null ? null : Json(e.MinAppVersion));
        Add("maxAppVersion", e.MaxAppVersion is null ? null : Json(e.MaxAppVersion));
        Add("tags", e.Tags is null ? null : "[" + string.Join(", ", e.Tags.Select(Json)) + "]");
        Add("source", e.SourceJson ?? Source());

        return "    {\n" + string.Join(",\n", fields) + "\n    }";
    }

    private static string Json(string value) => JsonSerializer.Serialize(value);
}
