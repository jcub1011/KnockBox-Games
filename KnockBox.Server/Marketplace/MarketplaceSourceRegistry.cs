using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using KnockBox.Server.Admin;
using KnockBox.Server.Games;

namespace KnockBox.Server.Marketplace;

/// <summary>One source's contribution to the merged catalog, and whether fetching it worked.</summary>
public sealed record SourceCatalog(
    RegisteredMarketplace Source,
    MarketplaceCatalog? Catalog,
    string? Error);

/// <summary>
/// The marketplaces this server will fetch from: the built-in official one plus any the operator
/// registered.
/// </summary>
/// <remarks>
/// <see cref="MarketplaceClient"/> holds exactly one catalog and one ETag, so it cannot be
/// parameterised by URL — one instance per source is the right granularity, and the registry creates
/// them. They all share a single <see cref="HttpClient"/>, which is safe because
/// <see cref="MarketplaceClient.CreateHttpClient"/> reads nothing source-specific.
///
/// Per-source options are the global ones with the two URLs swapped: the byte caps and timeouts are
/// operator policy about this server, not facts about a catalog, so they stay shared.
///
/// A source that cannot be reached yields an error string and does not fail the aggregate — the same
/// discipline <c>GameCatalog.ScanError</c> follows, because one unreachable community feed must not
/// hide the official catalog.
/// </remarks>
public sealed partial class MarketplaceSourceRegistry(
    HttpClient http,
    MarketplaceOptions options,
    GamePackageLimits limits,
    AdminSettingsStore settings,
    int maxSources,
    ILoggerFactory loggerFactory)
{
    /// <summary>The built-in source. Always present, disable-able, never removable.</summary>
    public const string OfficialId = "official";

    public const int DefaultMaxSources = 8;

    // Also a route value, so it is kept to characters that need no escaping anywhere.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    // One client per source, dropped when a source's URLs change — its cached catalog is only valid
    // for the URL it was fetched from.
    private readonly ConcurrentDictionary<string, (RegisteredMarketplace Source, MarketplaceClient Client)>
        _clients = new(StringComparer.OrdinalIgnoreCase);

    public int MaxSources { get; } = maxSources > 0 ? maxSources : DefaultMaxSources;

    /// <summary>Every source, official first, then registration order.</summary>
    public IReadOnlyList<RegisteredMarketplace> Sources =>
    [
        // Its URLs are configuration, but its enabled flag is operator policy — hard-coding it true made
        // "disable it instead" (what Validate and the delete route both tell an operator, and what
        // docs/ADMIN.md documents) impossible to actually do.
        new(OfficialId, "Official KnockBox marketplace", options.CatalogUrl, options.DownloadBaseUrl,
            Enabled: settings.OfficialSourceEnabled),
        .. settings.Sources,
    ];

    public RegisteredMarketplace? Find(string? id) =>
        Sources.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The client for a source, or null when there is no such source.</summary>
    public MarketplaceClient? For(string? id)
    {
        if (Find(id) is not { } source) return null;

        var cached = _clients.GetValueOrDefault(source.Id);
        if (cached.Client is not null && cached.Source == source) return cached.Client;

        var client = new MarketplaceClient(
            http,
            options with { CatalogUrl = source.CatalogUrl, DownloadBaseUrl = source.DownloadBaseUrl },
            limits,
            loggerFactory.CreateLogger<MarketplaceClient>());
        _clients[source.Id] = (source, client);
        return client;
    }

    /// <summary>
    /// Fetches every enabled source. One failure is reported, never thrown.
    /// </summary>
    /// <param name="forceRefresh">
    /// Bypasses the ETag short-circuit. False — the default — is what makes this cheap enough to call on
    /// every catalog read: an unchanged catalog costs one 304 and no re-parse.
    /// </param>
    public async Task<IReadOnlyList<SourceCatalog>> FetchAllAsync(
        bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var enabled = Sources.Where(s => s.Enabled).ToList();
        var results = new SourceCatalog[enabled.Count];

        // Bounded so a long source list can't open one connection per entry at once.
        using var slots = new SemaphoreSlim(3, 3);
        await Task.WhenAll(enabled.Select(async (source, index) =>
        {
            await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var client = For(source.Id);
                if (client is null)
                {
                    results[index] = new SourceCatalog(source, null, "This source is no longer registered.");
                    return;
                }

                var catalog = await client.GetCatalogAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
                results[index] = new SourceCatalog(source, catalog, null);
            }
            catch (MarketplaceException ex)
            {
                results[index] = new SourceCatalog(source, null, ex.Message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                results[index] = new SourceCatalog(source, null, "The catalog request timed out.");
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);

        return results;
    }

    /// <summary>Why a registration is invalid, or null when it is fine.</summary>
    public string? Validate(RegisteredMarketplace source)
    {
        if (!IsValidId(source.Id))
            return "Id must be 1-32 characters of letters, digits, '-' or '_'.";
        if (string.Equals(source.Id, OfficialId, StringComparison.OrdinalIgnoreCase))
            return $"'{OfficialId}' is the built-in source and cannot be replaced. Disable it instead.";
        if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 64)
            return "Name must be 1-64 characters.";
        // Validated with MarketplaceClient's own rule rather than a second copy of it, so a source can
        // never be registered that the downloader would then refuse to use.
        if (!MarketplaceClient.IsAllowedUrl(source.CatalogUrl))
            return "The catalog URL must be an absolute https URL (http is allowed only on loopback).";
        if (!MarketplaceClient.IsAllowedUrl(source.DownloadBaseUrl))
            return "The download base URL must be an absolute https URL (http is allowed only on loopback).";
        if (settings.Sources.Count >= MaxSources
            && !settings.Sources.Any(s => string.Equals(s.Id, source.Id, StringComparison.OrdinalIgnoreCase)))
            return $"At most {MaxSources} extra marketplaces can be registered " +
                   "(KnockBox:MarketplaceMaxSources).";
        return null;
    }
}
