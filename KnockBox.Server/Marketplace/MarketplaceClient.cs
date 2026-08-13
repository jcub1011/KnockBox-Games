using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Marketplace;

/// <summary>Raised for any marketplace problem an operator could act on. The message is operator-facing.</summary>
public sealed class MarketplaceException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A verified <c>.kbg</c> on disk. Deleting the file is the owner's job — hence IDisposable.</summary>
/// <remarks>
/// A class rather than a record: it owns a file and tracks whether that file has been deleted, and
/// value equality over a mutable disposal flag would be a surprising thing to hand callers.
/// </remarks>
public sealed class DownloadedPackage(string path, string id, string? version, long bytes, string sha256) : IDisposable
{
    /// <summary>Absolute path to the downloaded package.</summary>
    public string Path { get; } = path;

    /// <summary>The game id the package declares, already checked against the catalog entry.</summary>
    public string Id { get; } = id;

    /// <summary>The version its <c>GAME.json</c> declares, already checked against the catalog entry.</summary>
    public string? Version { get; } = version;

    /// <summary>Size of the file on disk.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>Lowercase hex SHA-256 of the file, as verified against the catalog.</summary>
    public string Sha256 { get; } = sha256;

    private int _disposed;

    /// <summary>
    /// Deletes the downloaded file. Safe to call twice, and never throws — a caller in a failure
    /// path must not be handed a second exception on the way out.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { File.Delete(Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Talks to the official game marketplace: fetches the catalog index and downloads a plugin's
/// <c>.kbg</c> package.
/// </summary>
/// <remarks>
/// This is the only outbound-HTTP component in the server, and everything it fetches is untrusted,
/// so three rules hold throughout:
///
/// 1. <b>URLs are derived, never supplied.</b> The catalog carries <c>repo</c>/<c>tag</c>/<c>asset</c>
///    and this class builds the URL from them against a configured origin. There is no URL field in
///    the schema to poison, so a tampered catalog cannot aim this server at an arbitrary host.
/// 2. <b>Every body is capped while reading</b>, never trusted from <c>Content-Length</c> — the same
///    rule <see cref="GamePackageReader"/> applies to declared entry sizes.
/// 3. <b>A download is not a package until it proves it.</b> The SHA-256 must match what the catalog
///    published, the archive must pass the full <see cref="GamePackageReader.Read"/> validation, and
///    the id and version inside must match the entry that advertised them.
///
/// It deliberately does NOT install anything. A verified file is handed back and the existing
/// drop-a-<c>.kbg</c>-in flow (<see cref="GamePackageInstaller"/>) remains the only path by which a
/// package becomes a playable game.
/// </remarks>
public sealed partial class MarketplaceClient
{
    private readonly HttpClient _http;
    private readonly MarketplaceOptions _options;
    private readonly GamePackageLimits _limits;
    private readonly ILogger<MarketplaceClient> _logger;

    // Guards the cached catalog: GetCatalogAsync may be called concurrently by whatever ends up
    // driving the UI, and two callers racing must not both hammer the origin or interleave writes
    // to the ETag/snapshot pair (which are only meaningful together).
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private string? _catalogETag;
    private MarketplaceCatalog? _catalogSnapshot;

    public MarketplaceClient(
        HttpClient http, MarketplaceOptions options, GamePackageLimits limits, ILogger<MarketplaceClient> logger)
    {
        _http = http;
        _options = options;
        _limits = limits;
        _logger = logger;
    }

    /// <summary>
    /// Builds the <see cref="HttpClient"/> this class expects.
    /// </summary>
    /// <remarks>
    /// A plain singleton client rather than <c>IHttpClientFactory</c>: the factory exists to rotate
    /// handlers so a long-lived client notices DNS changes, and
    /// <see cref="SocketsHttpHandler.PooledConnectionLifetime"/> solves that directly for a client
    /// that talks to one or two hosts. That keeps <c>Microsoft.Extensions.Http</c> out of the
    /// dependency list, which matters here — every package has to clear the Native AOT gate.
    /// </remarks>
    /// <remarks>
    /// Takes no options on purpose: nothing here is source-specific. That is what makes ONE client
    /// shareable across every registered marketplace, with each <see cref="MarketplaceClient"/> holding
    /// only its own URL pair and cached catalog.
    /// </remarks>
    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            // GitHub redirects release downloads to its object storage, so redirects must be
            // followed — but a redirect loop is somebody else's bug, not something to chase.
            MaxAutomaticRedirections = 5,
        };

        // No HttpClient.Timeout: it applies to the whole request INCLUDING the response body, so a
        // large package on a slow link would abort mid-download. Per-call CancellationTokens carry
        // the timeouts instead (see CatalogTimeout / DownloadTimeout).
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"KnockBox/{Hosting.KnockBoxVersion.Current}");
        return client;
    }

    /// <summary>
    /// Fetches the catalog index, or returns the cached copy when the origin reports it unchanged.
    /// </summary>
    /// <param name="forceRefresh">Skip the conditional request and re-read the body unconditionally.</param>
    /// <exception cref="MarketplaceException">The catalog is unreachable, oversized, or malformed.</exception>
    public async Task<MarketplaceCatalog> GetCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CatalogTimeout);

        // Acquired inside the try so that queueing behind a slow in-flight fetch reports the same
        // named timeout as a slow origin does — from the caller's side they are the same wait.
        var held = false;
        try
        {
            await _catalogLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            held = true;

            using var request = new HttpRequestMessage(HttpMethod.Get, CatalogUri());

            // The catalog is served from a CDN with soft rate limits and changes only when a game is
            // published, so a conditional request is the difference between one cheap 304 and a full
            // re-download every time an operator opens the page.
            if (!forceRefresh && _catalogETag is { Length: > 0 } etag && _catalogSnapshot is not null)
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await SendAsync(request, "the marketplace catalog", timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && _catalogSnapshot is not null)
            {
                _logger.LogDebug("Marketplace catalog unchanged (304).");
                return _catalogSnapshot;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MarketplaceException(
                    $"the marketplace catalog at {_options.CatalogUrl} returned HTTP {(int)response.StatusCode} " +
                    $"({response.ReasonPhrase}). Check KnockBox:MarketplaceCatalogUrl and this server's network access.");
            }

            var body = await ReadCappedAsync(
                response, _options.MaxCatalogBytes, "the marketplace catalog",
                "KnockBox:MarketplaceMaxCatalogBytes", timeout.Token).ConfigureAwait(false);

            var catalog = Parse(body);

            _catalogETag = response.Headers.ETag?.ToString();
            _catalogSnapshot = catalog;

            _logger.LogInformation(
                "Fetched marketplace catalog revision {Revision} with {Count} plugin(s).",
                catalog.Revision, catalog.Plugins?.Count ?? 0);
            return catalog;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MarketplaceException(
                $"fetching the marketplace catalog timed out after {_options.CatalogTimeout.TotalSeconds:0}s " +
                "(KnockBox:MarketplaceCatalogTimeoutSeconds).");
        }
        finally
        {
            if (held) _catalogLock.Release();
        }
    }

    /// <summary>
    /// Parses and sanity-checks a catalog document. Separated from the fetch so the parsing rules can
    /// be tested without any HTTP at all.
    /// </summary>
    /// <exception cref="MarketplaceException">The document is not a catalog this build can use.</exception>
    public static MarketplaceCatalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        MarketplaceCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize(utf8Json, KnockBoxProtocolContext.Default.MarketplaceCatalog);
        }
        catch (JsonException ex)
        {
            throw new MarketplaceException($"the marketplace catalog is not valid JSON: {ex.Message}", ex);
        }

        if (catalog is null) throw new MarketplaceException("the marketplace catalog is empty.");

        if (!SemVer.TryParse(catalog.SchemaVersion, out var schema))
        {
            throw new MarketplaceException(
                $"the marketplace catalog declares schemaVersion '{catalog.SchemaVersion}', which is not a version.");
        }
        if (schema.Major > MarketplaceCatalog.MaxSchemaVersionMajor)
        {
            throw new MarketplaceException(
                $"the marketplace catalog uses schema version {schema}, but this server understands " +
                $"{MarketplaceCatalog.MaxSchemaVersionMajor}.x — upgrade KnockBox to read it.");
        }

        // Duplicate ids would make "which one is installed?" ambiguous, and the answer would depend
        // on enumeration order. Refuse the whole document instead of picking a winner.
        if (catalog.Plugins is { Count: > 0 } plugins)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var plugin in plugins)
            {
                if (plugin?.Id is { Length: > 0 } id && !seen.Add(id))
                    throw new MarketplaceException($"the marketplace catalog lists plugin id '{id}' more than once.");
            }
        }

        return catalog;
    }

    /// <summary>
    /// Downloads <paramref name="plugin"/>'s package into <paramref name="destinationDirectory"/> and
    /// verifies it end to end. The returned file is a package this server would accept for install;
    /// on any failure nothing is left behind.
    /// </summary>
    /// <exception cref="MarketplaceException">
    /// The entry is unusable, the download failed, or the bytes are not the package the catalog
    /// advertised.
    /// </exception>
    public async Task<DownloadedPackage> DownloadAsync(
        MarketplacePlugin plugin, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        // Everything about the entry is checked before a single byte leaves this process: a bad entry
        // should cost an error message, not a request to a host we then have to reason about.
        var (id, source, expectedHash) = ValidateEntry(plugin);

        if (source.Size is { } declared && _options.MaxDownloadBytes > 0 && declared > _options.MaxDownloadBytes)
        {
            throw new MarketplaceException(
                $"'{id}' advertises a {declared}-byte package, over the {_options.MaxDownloadBytes}-byte limit " +
                "(KnockBox:MarketplaceMaxDownloadBytes).");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.DownloadTimeout);

        Directory.CreateDirectory(destinationDirectory);
        var partial = Path.Combine(destinationDirectory, $"{id}-{Guid.NewGuid():N}{GamePackage.Extension}.part");

        try
        {
            var (bytes, hash) = await FetchPackageAsync(id, source, partial, timeout.Token).ConfigureAwait(false);

            if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expectedHash)))
            {
                throw new MarketplaceException(
                    $"'{id}' downloaded with SHA-256 {Convert.ToHexStringLower(hash)}, but the catalog publishes " +
                    $"{expectedHash.ToLowerInvariant()}. The release was modified after it was catalogued, or the " +
                    "download was corrupted; the package has been discarded.");
            }

            var version = ValidatePackage(id, plugin, partial);

            // Only now does it earn the .kbg name — nothing that failed above can be mistaken for a
            // package by whatever ends up scanning this directory.
            var final = Path.Combine(destinationDirectory, $"{id}-{Convert.ToHexStringLower(hash)[..12]}{GamePackage.Extension}");
            // The CALLER's token, not timeout.Token: the transfer is complete and hash-verified by now,
            // so the download deadline has done its job, and letting it abort a ≤150ms rename would
            // discard a valid package worth up to MaxDownloadBytes. It would also lie about why — the
            // filter below turns a timeout.Token cancellation into "downloading timed out", which is not
            // what a rename failure is.
            await AtomicFile.MoveWithRetryAsync(partial, final, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Downloaded marketplace package {Id} {Version} ({Bytes} bytes) from {Repo}@{Tag}.",
                id, version ?? "(no version)", bytes, source.Repo, source.Tag);

            return new DownloadedPackage(final, id, version, bytes, Convert.ToHexStringLower(hash));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Discard(partial);
            throw new MarketplaceException(
                $"downloading '{id}' timed out after {_options.DownloadTimeout.TotalSeconds:0}s " +
                "(KnockBox:MarketplaceDownloadTimeoutSeconds).");
        }
        catch
        {
            Discard(partial);
            throw;
        }
    }

    /// <summary>Streams the asset to <paramref name="partial"/>, hashing and capping as it goes.</summary>
    private async Task<(long Bytes, byte[] Hash)> FetchPackageAsync(
        string id, MarketplaceSource source, string partial, CancellationToken cancellationToken)
    {
        var uri = DownloadUri(source);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await SendAsync(request, $"package '{id}'", cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new MarketplaceException(
                $"'{id}' could not be downloaded: {uri} returned HTTP {(int)response.StatusCode} " +
                $"({response.ReasonPhrase}). The catalog may reference a release or asset that no longer exists.");
        }

        // Advisory only — a hostile or broken origin can understate this, so the real enforcement is
        // the running total below. Checking it first just avoids starting a download doomed to fail.
        if (response.Content.Headers.ContentLength is { } advertised
            && _options.MaxDownloadBytes > 0 && advertised > _options.MaxDownloadBytes)
        {
            throw new MarketplaceException(
                $"'{id}' is {advertised} bytes, over the {_options.MaxDownloadBytes}-byte limit " +
                "(KnockBox:MarketplaceMaxDownloadBytes).");
        }

        await using var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = new FileStream(
            partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long total = 0;
        try
        {
            int read;
            while ((read = await network.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (_options.MaxDownloadBytes > 0 && total > _options.MaxDownloadBytes)
                {
                    throw new MarketplaceException(
                        $"'{id}' exceeded the {_options.MaxDownloadBytes}-byte download limit " +
                        "(KnockBox:MarketplaceMaxDownloadBytes) and was aborted.");
                }
                hasher.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (total, hasher.GetHashAndReset());
    }

    /// <summary>
    /// Runs the downloaded file through the real package reader and checks that what is inside
    /// matches what the catalog said. Returns the version its <c>GAME.json</c> declares.
    /// </summary>
    private string? ValidatePackage(string id, MarketplacePlugin plugin, string path)
    {
        GamePackageReader.PackagePlan plan;
        byte[] manifestBytes;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            plan = GamePackageReader.Read(archive, _limits);
            manifestBytes = GamePackageReader.ReadManifestBytes(plan);
        }
        catch (GamePackageException ex)
        {
            throw new MarketplaceException($"'{id}' is not a valid .kbg package: {ex.Message}", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new MarketplaceException($"'{id}' did not download as a readable archive: {ex.Message}", ex);
        }

        // Ordinal: game ids name a directory on disk, and on Linux "Demo" and "demo" are two games.
        if (!string.Equals(plan.Id, id, StringComparison.Ordinal))
        {
            throw new MarketplaceException(
                $"the package published for '{id}' actually contains game '{plan.Id}'. The catalog entry and the " +
                "release disagree; the package has been discarded.");
        }

        GameManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, KnockBoxProtocolContext.Default.GameManifest);
        }
        catch (JsonException ex)
        {
            throw new MarketplaceException($"'{id}' contains a GAME.json that is not valid JSON: {ex.Message}", ex);
        }

        // The catalog's version is generated FROM this file by the publishing action, so a mismatch
        // means the entry is describing bytes it did not ship — exactly the substitution the hash
        // check guards against, caught here at the semantic level too.
        if (!string.Equals(manifest?.Version, plugin.Version, StringComparison.Ordinal))
        {
            throw new MarketplaceException(
                $"the catalog advertises '{id}' version {plugin.Version}, but the downloaded package declares " +
                $"{manifest?.Version ?? "no version"}. The package has been discarded.");
        }

        return manifest?.Version;
    }

    /// <summary>
    /// Checks that an entry is one this server can act on, and returns its usable parts. This is the
    /// whole gate between a published catalog and an outbound request, so the rules are strict and
    /// each failure names what an operator (or the marketplace maintainer) has to fix.
    /// </summary>
    private static (string Id, MarketplaceSource Source, string Sha256) ValidateEntry(MarketplacePlugin plugin)
    {
        if (plugin.Id is not { Length: > 0 } id || !IdPattern().IsMatch(id))
            throw new MarketplaceException($"the catalog entry has an unusable plugin id ('{plugin.Id}').");

        if (plugin.Source is not { } source)
            throw new MarketplaceException($"the catalog entry for '{id}' declares no source.");

        if (!string.Equals(source.Type, "github-release", StringComparison.Ordinal))
        {
            throw new MarketplaceException(
                $"the catalog entry for '{id}' uses source type '{source.Type}', which this server cannot install. " +
                "Only 'github-release' is supported.");
        }

        if (source.Repo is not { Length: > 0 } repo || !RepoPattern().IsMatch(repo))
            throw new MarketplaceException($"the catalog entry for '{id}' has an unusable repo ('{source.Repo}').");

        // One URL path segment. Anything with a separator or a traversal component could reshape the
        // derived URL into a different path on the origin.
        if (source.Tag is not { Length: > 0 } tag || !TagPattern().IsMatch(tag) || tag is "." or "..")
            throw new MarketplaceException($"the catalog entry for '{id}' has an unusable release tag ('{source.Tag}').");

        if (source.Asset is not { Length: > 0 } asset || !AssetPattern().IsMatch(asset))
        {
            throw new MarketplaceException(
                $"the catalog entry for '{id}' names asset '{source.Asset}', which is not a {GamePackage.Extension} " +
                "file name. The entry must point at the game package itself.");
        }

        if (source.Sha256 is not { Length: 64 } sha || !Sha256Pattern().IsMatch(sha))
        {
            throw new MarketplaceException(
                $"the catalog entry for '{id}' publishes no usable sha256. A package is only installed when its " +
                "hash matches the one the marketplace published, so an entry without one cannot be trusted.");
        }

        return (id, source, sha);
    }

    private Uri CatalogUri()
    {
        if (!Uri.TryCreate(_options.CatalogUrl, UriKind.Absolute, out var uri) || !IsAllowedScheme(uri))
        {
            throw new MarketplaceException(
                $"KnockBox:MarketplaceCatalogUrl ('{_options.CatalogUrl}') is not an absolute https URL.");
        }
        return uri;
    }

    private Uri DownloadUri(MarketplaceSource source)
    {
        // Built from validated parts against the configured origin — the catalog never supplies a URL.
        // Escaping is belt-and-braces: the patterns above already exclude everything that would need it.
        var url = $"{_options.DownloadBaseUrl}/{source.Repo}/releases/download/" +
                  $"{Uri.EscapeDataString(source.Tag!)}/{Uri.EscapeDataString(source.Asset!)}";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedScheme(uri))
        {
            throw new MarketplaceException(
                $"KnockBox:MarketplaceDownloadBaseUrl ('{_options.DownloadBaseUrl}') does not form an https URL.");
        }
        return uri;
    }

    /// <summary>
    /// HTTPS only, except against loopback — which exists so a test or an offline mirror can run over
    /// plain HTTP without punching a hole in the rule that matters on a real network.
    /// </summary>
    private static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>
    /// Whether a URL an operator typed may be used as a catalog or download origin — the same rule,
    /// exposed so <see cref="MarketplaceSourceRegistry"/> validates a registration with it rather than
    /// keeping a second copy that could drift.
    /// </summary>
    internal static bool IsAllowedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedScheme(uri);

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new MarketplaceException($"could not reach {what} at {request.RequestUri}: {ex.Message}", ex);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(
        HttpResponseMessage response, long maxBytes, string what, string knob, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (maxBytes > 0 && buffer.Length > maxBytes)
                    throw new MarketplaceException($"{what} is larger than the {maxBytes}-byte limit ({knob}).");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    private void Discard(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the partial marketplace download at {Path}.", path);
        }
    }

    // Mirrors the id pattern in marketplace.schema.json, and is stricter than it needs to be on
    // purpose: the id also names a directory once installed.
    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.+-]+$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\.kbg$")]
    private static partial Regex AssetPattern();

    [GeneratedRegex(@"^[A-Fa-f0-9]{64}$")]
    private static partial Regex Sha256Pattern();
}
