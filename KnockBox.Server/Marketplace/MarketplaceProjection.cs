using KnockBox.Server.Games;

namespace KnockBox.Server.Marketplace;

/// <summary>One row of the merged marketplace view: what a source offers, against what is installed.</summary>
/// <param name="Status">
/// A <see cref="PluginUpdateStatus"/> value, or <see cref="MarketplaceProjection.InstalledOnly"/> for a
/// managed game no enabled source offers.
/// </param>
/// <param name="ShadowedBy">
/// Set when another source offers this id first and won. Surfaced rather than silently dropped, the
/// same way a duplicate game id is reported by the installer.
/// </param>
public sealed record MarketplaceEntry(
    string Id,
    string Name,
    string? Description,
    string? Author,
    IReadOnlyList<string>? Tags,
    string? AvailableVersion,
    string? InstalledVersion,
    string Status,
    string? Reason,
    long? SizeBytes,
    string? PublishedAt,
    string? MinAppVersion,
    string? MaxAppVersion,
    string SourceId,
    string? SourceName,
    string? ShadowedBy,
    bool Managed,
    bool Installed);

/// <summary>
/// Merges what the registered catalogs offer with what this server actually has installed.
/// </summary>
/// <remarks>
/// Pure — no I/O, no clock, no network — for the same reason <see cref="PluginUpdateEvaluator"/> is:
/// the interesting rules here are about precedence and about which of several near-identical states a
/// row is really in, and those deserve to be testable without standing up an HTTP stub.
///
/// This is <see cref="PluginUpdateEvaluator"/>'s first production caller.
/// </remarks>
public static class MarketplaceProjection
{
    /// <summary>
    /// A managed game no enabled source offers — an upload, or an entry that was withdrawn.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a <see cref="PluginUpdateStatus"/> value: every one of those is a statement
    /// about a catalog entry, and this is a statement about the absence of one. Adding it to the enum
    /// would also mean touching <see cref="PluginUpdateEvaluator"/>, which has no way to know it.
    /// </remarks>
    public const string InstalledOnly = "installedOnly";

    /// <param name="catalogs">Every fetched source, in precedence order — the first to offer an id wins.</param>
    /// <param name="installed">The live catalog's games, from <c>GameCatalog.GameLocations</c>.</param>
    /// <param name="managedIds">Ids whose package sits in the managed root, so this server may replace it.</param>
    public static IReadOnlyList<MarketplaceEntry> Project(
        IReadOnlyList<SourceCatalog> catalogs,
        IReadOnlyDictionary<string, GameCatalog.GameLocation> installed,
        IReadOnlySet<string> managedIds,
        SemVer appVersion)
    {
        var rows = new List<MarketplaceEntry>();
        // Which source claimed each id, so a later source's duplicate is reported rather than dropped.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in catalogs)
        {
            if (source.Catalog?.Plugins is not { } plugins) continue;

            foreach (var status in PluginUpdateEvaluator.Evaluate(source.Catalog, installed, appVersion))
            {
                var plugin = status.Entry;
                var id = plugin.Id ?? "";

                // An entry with no id is KEPT, as the Unusable row the evaluator already made it. Dropping
                // it here silently un-did that decision: a malformed catalog entry is exactly what a
                // publisher needs to be told about, and a page that just doesn't show their game reads as
                // this server's fault. It claims no id (there is none to claim, and every such row across
                // every source would otherwise collide on ""), and versionAction refuses to act on an
                // unusable row, so the card renders with its reason and a disabled button.
                var shadowedBy = id.Length == 0 ? null : claimed.GetValueOrDefault(id);
                if (id.Length > 0 && shadowedBy is null) claimed[id] = source.Source.Id;

                installed.TryGetValue(id, out var location);
                rows.Add(new MarketplaceEntry(
                    id,
                    // Blank counts as absent, not just null — the fallback exists so a row always has
                    // something to render, and an empty string reads as a rendering bug rather than as
                    // the malformed entry it is. An id-less entry has no id to fall back to either.
                    string.IsNullOrWhiteSpace(plugin.Name)
                        ? (id.Length > 0 ? id : "(unnamed entry)")
                        : plugin.Name,
                    plugin.Description,
                    plugin.Author?.Name,
                    plugin.Tags,
                    plugin.Version,
                    location?.Manifest.Version,
                    Camel(status.Status.ToString()),
                    status.Reason,
                    plugin.Source?.Size,
                    plugin.LastUpdated?.UtcDateTime.ToString("O"),
                    plugin.MinAppVersion,
                    plugin.MaxAppVersion,
                    source.Source.Id,
                    source.Source.Name,
                    shadowedBy,
                    managedIds.Contains(id),
                    location is not null));
            }
        }

        // Managed games nothing offers. Listed because they are still updatable and rollback-able from
        // the portal — leaving them out would make an uploaded game invisible on the only page that can
        // act on it.
        foreach (var id in managedIds)
        {
            if (claimed.ContainsKey(id) || !installed.TryGetValue(id, out var location)) continue;
            rows.Add(new MarketplaceEntry(
                location.Manifest.Id,
                location.Manifest.Name,
                null, null, null,
                AvailableVersion: null,
                location.Manifest.Version,
                InstalledOnly,
                Reason: "No registered marketplace offers this game.",
                SizeBytes: null, PublishedAt: null, MinAppVersion: null, MaxAppVersion: null,
                SourceId: "", SourceName: null, ShadowedBy: null,
                Managed: true, Installed: true));
        }

        rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
}
