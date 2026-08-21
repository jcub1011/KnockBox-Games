using System.IO.Compression;
using KnockBox.Contracts;
using KnockBox.Server.Hosting;

namespace KnockBox.Server.Games;

/// <summary>
/// Maintains a derived cache of pre-compressed game assets under <c>games-compressed/&lt;id&gt;/…</c>,
/// mirroring each game's source tree with <c>.br</c> (and optionally <c>.gz</c>) siblings. Because the
/// work runs once per asset change (not once per request), it uses the maximum-effort
/// <see cref="CompressionLevel.SmallestSize"/> — the opposite tradeoff to the on-the-fly
/// <c>ResponseCompression</c> fallback, which must use <c>Fastest</c>.
///
/// Reconciliation is idempotent and cheap when nothing changed (stat + mtime compare, skip): it
/// (re)compresses files whose source is newer, prunes variants whose source vanished, and removes
/// whole directories for games that left the catalog. Driven by <see cref="GameCatalog.Discovered"/>
/// plus a periodic timer in <c>Program.cs</c>, so a game added/updated/removed in <c>games/</c> is
/// reflected with no restart. The cache is fully regenerable, so it can live on ephemeral storage.
/// </summary>
public sealed class GameAssetPrecompressor(
    string compressedRoot, bool gzip, int minBytes,
    ILogger<GameAssetPrecompressor> logger)
{
    // Contents already compressed by their own format — re-compressing wastes CPU and rarely shrinks.
    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico",
        ".mp3", ".ogg", ".wav", ".mp4", ".webm", ".woff2",
        ".br", ".gz", ".zip", GamePackage.Extension,
    };

    // Coalescing gate: at most one reconcile runs at a time. A request that arrives mid-run sets
    // _rerun so the latest catalog state is processed once the current pass finishes — rapid
    // hot-reload bursts collapse into the minimum number of passes without ever missing the newest state.
    private readonly Lock _gate = new();
    private bool _running;
    private bool _rerun;
    private IReadOnlyDictionary<string, GameCatalog.GameLocation> _latest =
        new Dictionary<string, GameCatalog.GameLocation>();

    // Games seeded straight from a .kbg (id -> the extracted source dir) that the catalog has not
    // published yet, guarded by _gate because seeding runs on the installer's task while a reconcile may
    // be running on another.
    //
    // The installer extracts, seeds, and only THEN asks for a rediscovery, so for the debounce plus scan
    // that follows, the id is absent from every catalog map — and PruneRemovedGames' rule is "absent from
    // the catalog ⇒ delete the directory". A reconcile landing in that window (the periodic timer, or the
    // sibling Discovered handler carrying the pre-install map) therefore deleted the seed it had just
    // written, and the next pass re-paid the max-effort Brotli the seed exists to avoid. Entries are
    // dropped as soon as the catalog publishes the id, so this protects only that window.
    private readonly Dictionary<string, string> _seeded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reconciles the cache to <paramref name="games"/>, the catalog's id → manifest + directory map
    /// (take it from <see cref="GameCatalog.GameLocations"/> or the <c>Discovered</c> event). Per-file
    /// errors are logged and skipped so one bad asset never aborts the pass. Re-entrant calls coalesce:
    /// a second caller records the new state and returns immediately while the first caller loops to
    /// pick it up.
    /// </summary>
    /// <remarks>
    /// The caller supplies directories rather than ids-plus-a-root because a game's files may live
    /// under the administrator's games directory OR under the unpacked-package cache. Keeping this
    /// class root-agnostic means it never has to know which. The manifest rides along because some of
    /// a game's files must never be compressed at all (see <see cref="DeniedRelatives"/>).
    /// </remarks>
    public void ReconcileAll(IReadOnlyDictionary<string, GameCatalog.GameLocation> games)
    {
        lock (_gate)
        {
            _latest = games;
            if (_running) { _rerun = true; return; }
            _running = true;
        }

        try
        {
            while (true)
            {
                IReadOnlyDictionary<string, GameCatalog.GameLocation> snapshot;
                lock (_gate) { snapshot = _latest; _rerun = false; }

                ReconcileOnce(snapshot);

                lock (_gate)
                {
                    if (!_rerun) { _running = false; return; }
                }
            }
        }
        catch
        {
            lock (_gate) { _running = false; }
            throw;
        }
    }

    private void ReconcileOnce(IReadOnlyDictionary<string, GameCatalog.GameLocation> games)
    {
        var compressed = 0;
        // A game the catalog now publishes is protected by the ordinary rules; it no longer needs its
        // post-seed grace, so retire it rather than letting the map grow for the process lifetime.
        lock (_gate)
            foreach (var id in games.Keys) _seeded.Remove(id);

        var removed = PruneRemovedGames(games);

        foreach (var (id, location) in games)
        {
            var srcDir = location.Directory;
            if (!Directory.Exists(srcDir)) continue;
            // A game's server-authority module and its authorityWords dictionaries are never served
            // (Hosting/GameOriginAssetGate), so warming variants of them is pointless — and pre-existing
            // variants must be actively pruned so the compressed cache can't leak them either.
            var excluded = DeniedRelatives(location.Manifest);
            compressed += CompressGameDir(id, srcDir, excluded);
            removed += PruneOrphanVariants(id, srcDir, excluded);
        }

        // Only narrate when something actually changed — this runs on a timer and would otherwise spam.
        if ((compressed > 0 || removed > 0) && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Pre-compression reconcile: {Compressed} asset(s) (re)compressed, {Removed} stale variant(s)/dir(s) removed.",
                compressed, removed);
    }

    // Deletes games-compressed/<id> for any id no longer in the catalog or whose source folder is gone.
    //
    // The source-folder test MUST use the catalog's resolved directory, not gamesRoot/<id>: a game
    // installed from a .kbg lives under the unpacked root instead, and testing the wrong path would
    // mark it removed on every pass — deleting its cache and forcing a full SmallestSize recompress
    // each time the timer fires. Keep this in step with ReconcileOnce.
    private int PruneRemovedGames(IReadOnlyDictionary<string, GameCatalog.GameLocation> games)
    {
        if (!Directory.Exists(compressedRoot)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(compressedRoot))
        {
            var id = new DirectoryInfo(dir).Name;
            if (games.TryGetValue(id, out var location) && Directory.Exists(location.Directory)) continue;
            if (IsAwaitingDiscovery(id)) continue; // just seeded from a package; the catalog hasn't caught up
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove stale compressed dir {Dir}.", dir); }
        }
        return removed;
    }

    // True while a package-seeded game is still waiting for the catalog to publish it AND its extracted
    // files are still on disk. The directory check is what keeps this from being a permanent exemption:
    // once the game's files are gone the entry is dropped and the cache is prunable again.
    private bool IsAwaitingDiscovery(string id)
    {
        lock (_gate)
        {
            if (!_seeded.TryGetValue(id, out var sourceDir)) return false;
            if (Directory.Exists(sourceDir)) return true;
            _seeded.Remove(id);
            return false;
        }
    }

    // (Re)compresses source files whose recorded (mtime, length) no longer matches — or whose produced
    // variants were removed — and records the outcome in the per-game index so unchanged files are
    // skipped and not-beneficial files aren't re-attempted every pass. Returns the count processed.
    private int CompressGameDir(string id, string srcDir, IReadOnlySet<string>? excluded = null)
    {
        var dir = Path.Combine(compressedRoot, id);
        var oldIndex = LoadIndex(dir);
        var newIndex = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        var count = 0;

        foreach (var src in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var relative = Path.GetRelativePath(srcDir, src);
                if (IsExcluded(relative, excluded)) continue; // never-served authority module / word file

                var info = new FileInfo(src);
                if (!ShouldCompress(info.Name, info.Length, minBytes)) continue;

                // Fresh when BOTH the source's mtime and its length match what produced the current
                // variants, and (if we produced any) they're still on disk. Comparing length as well as
                // mtime catches an in-place/offline edit that changed content without advancing the
                // timestamp — which a pure mtime check would miss.
                if (oldIndex.TryGetValue(relative, out var prev)
                    && prev.MtimeTicks == info.LastWriteTimeUtc.Ticks
                    && prev.Length == info.Length
                    && (!prev.Produced || VariantsPresent(dir, relative)))
                {
                    newIndex[relative] = prev;
                    continue;
                }

                var produced = Compress(src, Path.Combine(dir, relative + ".br"), CompressionAlgo.Brotli);
                if (produced && gzip)
                    Compress(src, Path.Combine(dir, relative + ".gz"), CompressionAlgo.Gzip);
                else if (!produced)
                    DeleteIfExists(Path.Combine(dir, relative + ".gz")); // br not worth it ⇒ neither is gz

                newIndex[relative] = new IndexEntry(info.LastWriteTimeUtc.Ticks, info.Length, produced);
                count++;
            }
            catch (Exception ex)
            {
                // Leave this file out of the new index so the next pass retries it.
                logger.LogWarning(ex, "Failed to pre-compress {File}; serving it uncompressed.", src);
            }
        }

        SaveIndex(dir, newIndex);
        return count;
    }

    /// <summary>
    /// Seeds the cache for one game straight from a <c>.kbg</c> package, whose payloads are already
    /// Brotli streams. Saves re-compressing them at <see cref="CompressionLevel.SmallestSize"/>, which
    /// takes ~50 seconds for a large WASM export and would otherwise happen on this server's next
    /// reconcile after every install.
    ///
    /// Call it AFTER the game's files are in place, because freshness is keyed to the extracted file's
    /// (mtime, length) — exactly what <see cref="CompressGameDir"/> compares on later passes, so a
    /// seeded game is then skipped instead of recompressed.
    /// </summary>
    /// <param name="id">The game id; names the cache directory.</param>
    /// <param name="sourceDir">Where the game's extracted files live.</param>
    /// <param name="entries">
    /// One item per packaged file: its path relative to <paramref name="sourceDir"/>, and the Brotli
    /// blob to store (or null when the package stored that file uncompressed).
    /// </param>
    /// <returns>The number of variants written.</returns>
    public int SeedFromPackage(string id, string sourceDir, IEnumerable<(string Relative, Func<Stream>? OpenBrotli)> entries)
    {
        var dir = Path.Combine(compressedRoot, id);
        var index = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        var written = 0;

        // Claim the post-seed grace BEFORE writing anything, so a reconcile that starts mid-seed can't
        // delete what this call is still producing (see _seeded).
        lock (_gate) _seeded[id] = sourceDir;

        foreach (var (relative, openBrotli) in entries)
        {
            try
            {
                var src = Path.Combine(sourceDir, relative);
                var info = new FileInfo(src);
                if (!info.Exists) continue; // extraction skipped it; nothing to key freshness against
                if (!ShouldCompress(info.Name, info.Length, minBytes)) continue;

                if (openBrotli is null)
                {
                    // The packer judged this file not worth compressing. Record it as "tried, not
                    // beneficial" — the same state Compress() returning false produces — so later
                    // passes don't keep re-attempting it. Producing that state means producing ALL of
                    // it, including Compress()'s drop of any prior variant: an upgrade whose new build
                    // stores this file raw would otherwise leave the OLD version's .br on disk, and
                    // because a not-produced index row is skipped forever by CompressGameDir and is not
                    // an orphan to PruneOrphanVariants, every br-accepting client would keep receiving
                    // the previous version's bytes at the new version's URL.
                    DeleteIfExists(Path.Combine(dir, relative + ".br"));
                    DeleteIfExists(Path.Combine(dir, relative + ".gz"));
                    index[relative] = new IndexEntry(info.LastWriteTimeUtc.Ticks, info.Length, Produced: false);
                    continue;
                }

                var dest = Path.Combine(dir, relative + ".br");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var tmp = dest + ".tmp";
                try
                {
                    using (var blob = openBrotli())
                    using (var output = File.Create(tmp))
                    {
                        blob.CopyTo(output);
                    }
                    File.Move(tmp, dest, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort: orphan retried next reconcile */ } }
                }

                // A package ships Brotli only, but the index row below records Produced: true, and
                // CompressGameDir treats "produced" as meaning EVERY expected variant is on disk (see
                // VariantsPresent). Leaving .gz absent while gzip is enabled therefore makes the very next
                // reconcile judge this file stale and recompress BOTH variants at SmallestSize — undoing
                // the seed and re-paying the ~49s-per-large-asset Brotli this whole path exists to avoid.
                // So produce the .gz here. Gzip is roughly two orders of magnitude cheaper than
                // Brotli-11, runs once per install, and never touches the request path.
                if (gzip) Compress(src, Path.Combine(dir, relative + ".gz"), CompressionAlgo.Gzip);

                index[relative] = new IndexEntry(info.LastWriteTimeUtc.Ticks, info.Length, Produced: true);
                written++;
            }
            catch (Exception ex)
            {
                // Leave it out of the index so the ordinary reconcile compresses it the usual way.
                logger.LogWarning(ex, "Could not seed pre-compressed variant for {Game}/{File}; it will be compressed normally.",
                    id, relative);
            }
        }

        SaveIndex(dir, index);
        if (written > 0 && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Seeded {Count} pre-compressed asset(s) for '{Id}' from its package, skipping max-effort re-compression.",
                written, id);
        return written;
    }

    // True when the variants we expect for a produced file are present (so a hand-deleted .br/.gz is
    // rebuilt). Requiring .gz when gzip is enabled also rebuilds it after gzip is switched back on.
    private bool VariantsPresent(string dir, string relative) =>
        File.Exists(Path.Combine(dir, relative + ".br")) && (!gzip || File.Exists(Path.Combine(dir, relative + ".gz")));

    // Writes to a temp file then atomically moves it into place, so a reader never sees a half-written
    // variant. Returns false (dropping any prior variant) when the result isn't smaller than the source —
    // an already-dense payload we didn't catch by extension; serving then falls back to the raw file.
    private bool Compress(string src, string dest, CompressionAlgo algo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".tmp";
        try
        {
            using (var input = File.OpenRead(src))
            using (var output = File.Create(tmp))
            using (Stream comp = algo == CompressionAlgo.Brotli
                ? new BrotliStream(output, CompressionLevel.SmallestSize)
                : new GZipStream(output, CompressionLevel.SmallestSize))
            {
                input.CopyTo(comp);
            }

            if (new FileInfo(tmp).Length >= new FileInfo(src).Length)
            {
                File.Delete(tmp);
                DeleteIfExists(dest);
                return false;
            }

            File.Move(tmp, dest, overwrite: true);
            return true;
        }
        finally
        {
            // Best effort: a failed delete leaves a harmless orphan .tmp, retried on the next reconcile —
            // nothing an operator can act on, so swallow rather than log noise.
            if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort: orphan retried next reconcile */ } }
        }
    }

    private static void DeleteIfExists(string path)
    {
        // Best effort: a failed delete leaves a harmless orphan variant, retried on the next reconcile —
        // nothing an operator can act on, so swallow rather than log noise.
        if (File.Exists(path)) { try { File.Delete(path); } catch { /* best effort: orphan retried next reconcile */ } }
    }

    // Deletes variants whose source file is gone, whose source should no longer be compressed, or whose
    // encoding is disabled (.gz when gzip is off). Returns the count removed.
    private int PruneOrphanVariants(string id, string srcDir, IReadOnlySet<string>? excluded = null)
    {
        var dir = Path.Combine(compressedRoot, id);
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var variant in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(variant), IndexFileName, StringComparison.Ordinal)) continue;
            var ext = Path.GetExtension(variant);
            // Stray temp file from a process that died mid-write — safe to drop (reconcile is single-run,
            // so nothing is writing one right now), and it's never served.
            if (string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(variant); removed++; }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove stray temp file {File}.", variant); }
                continue;
            }
            var isBr = string.Equals(ext, ".br", StringComparison.OrdinalIgnoreCase);
            var isGz = string.Equals(ext, ".gz", StringComparison.OrdinalIgnoreCase);
            if (!isBr && !isGz) continue; // never created by us; leave it alone

            var relativeVariant = Path.GetRelativePath(dir, variant);
            var sourceRelative = relativeVariant[..^3]; // strip ".br" / ".gz"
            var src = Path.Combine(srcDir, sourceRelative);

            var orphan = (isGz && !gzip)
                || IsExcluded(sourceRelative, excluded) // variant of a never-served authority module / word file
                || !File.Exists(src)
                || !ShouldCompress(Path.GetFileName(src), new FileInfo(src).Length, minBytes);
            if (!orphan) continue;

            try { File.Delete(variant); removed++; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove orphan variant {File}.", variant); }
        }
        return removed;
    }

    /// <summary>
    /// Pure decision: compress a file unless it's below <paramref name="minBytes"/> (compression
    /// overhead outweighs the win) or its extension is a known already-compressed format. A denylist
    /// (rather than an allowlist) keeps the "any engine asset just works" property — unknown types are
    /// compressed, and the not-smaller check in <see cref="Compress"/> is the backstop.
    /// </summary>
    public static bool ShouldCompress(string fileName, long size, int minBytes)
    {
        if (size < minBytes) return false;
        return !IncompressibleExtensions.Contains(Path.GetExtension(fileName));
    }

    // Game-relative path with separators normalized, lowercased so the exclusion set can be compared
    // ordinal-ignore-case (games live on case-insensitive filesystems too).
    private static string NormalizeRelative(string relative) => relative.Replace('\\', '/').ToLowerInvariant();

    // The never-served files for a game: its serverAuthority module and every authorityWords dictionary.
    internal static IReadOnlySet<string> DeniedRelatives(GameManifest g)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(g.ServerAuthority)) set.Add(NormalizeRelative(g.ServerAuthority));
        if (g.AuthorityWords is { } words)
            foreach (var decl in words.Values)
                if (!string.IsNullOrEmpty(decl?.File)) set.Add(NormalizeRelative(decl.File));
        return set;
    }

    // Internal so the .kbg installer can test its package-logical paths against the SAME set and the
    // SAME normalization the reconcile pass uses, instead of carrying its own copy of the rule.
    internal static bool IsExcluded(string relative, IReadOnlySet<string>? excluded) =>
        excluded is not null && excluded.Contains(NormalizeRelative(relative));

    // Per-game freshness record (one line per compressed source file). Lives inside games-compressed/<id>
    // as a dot-prefixed file, which PhysicalFileProvider's default exclusion filters keep from ever being
    // served. Plain text + manual parsing (no reflection) keeps it Native-AOT-safe.
    private const string IndexFileName = ".kb-precompress.index";

    private readonly record struct IndexEntry(long MtimeTicks, long Length, bool Produced);

    private Dictionary<string, IndexEntry> LoadIndex(string dir)
    {
        var index = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        var path = Path.Combine(dir, IndexFileName);
        if (!File.Exists(path)) return index;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                // Format: "<mtimeTicks>\t<length>\t<produced 0|1>\t<relpath>". relpath is last so a tab in
                // a filename can't corrupt the numeric fields.
                var t1 = line.IndexOf('\t');
                var t2 = t1 < 0 ? -1 : line.IndexOf('\t', t1 + 1);
                var t3 = t2 < 0 ? -1 : line.IndexOf('\t', t2 + 1);
                if (t3 < 0) continue;
                if (!long.TryParse(line.AsSpan(0, t1), out var mtime)) continue;
                if (!long.TryParse(line.AsSpan(t1 + 1, t2 - t1 - 1), out var len)) continue;
                index[line[(t3 + 1)..]] = new IndexEntry(mtime, len, line[t2 + 1] == '1');
            }
        }
        catch (Exception ex)
        {
            // A corrupt/unreadable index just means everything looks stale and is rebuilt — safe.
            logger.LogWarning(ex, "Could not read pre-compress index {Path}; rebuilding.", path);
            index.Clear();
        }
        return index;
    }

    private void SaveIndex(string dir, Dictionary<string, IndexEntry> index)
    {
        var path = Path.Combine(dir, IndexFileName);
        if (index.Count == 0) { DeleteIfExists(path); return; }
        try
        {
            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllLines(tmp, index.Select(e =>
                $"{e.Value.MtimeTicks}\t{e.Value.Length}\t{(e.Value.Produced ? 1 : 0)}\t{e.Key}"));
            // Retried, unlike the two per-file moves in this class: losing THIS one costs the whole
            // game's freshness index, so the next pass re-compresses every asset at SmallestSize —
            // the ~49s-per-large-asset bill the seed path exists to avoid. The catch below still means
            // a genuine failure is a warning and nothing more.
            AtomicFile.MoveWithRetry(tmp, path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write pre-compress index {Path}; the next pass will rebuild.", path);
        }
    }

    /// <summary>
    /// Pure content negotiation: picks the best pre-compressed encoding the client accepts, preferring
    /// Brotli over Gzip. Returns the encoding token (<c>"br"</c>/<c>"gzip"</c>) or null when the client
    /// accepts neither (or only gzip while gzip variants are disabled). The caller still confirms the
    /// variant file exists before serving it.
    /// </summary>
    public static string? NegotiateEncoding(string? acceptEncoding, bool gzipEnabled)
    {
        if (AcceptsEncoding(acceptEncoding, "br")) return "br";
        if (gzipEnabled && AcceptsEncoding(acceptEncoding, "gzip")) return "gzip";
        return null;
    }

    // True when the Accept-Encoding header lists the token with a non-zero quality. A "token;q=0"
    // means "explicitly not acceptable"; a missing/other q-value means acceptable. Lenient parse —
    // adequate for the handful of codings browsers actually send.
    internal static bool AcceptsEncoding(string? acceptEncoding, string token)
    {
        if (string.IsNullOrEmpty(acceptEncoding)) return false;
        foreach (var part in acceptEncoding.Split(','))
        {
            var segment = part.Trim();
            var semi = segment.IndexOf(';');
            var coding = (semi < 0 ? segment : segment[..semi]).Trim();
            if (!string.Equals(coding, token, StringComparison.OrdinalIgnoreCase)) continue;
            return semi < 0 || !IsQualityZero(segment[(semi + 1)..]);
        }
        return false;
    }

    // Parses the q-value from an Accept-Encoding parameter section (e.g. "q=0", "q=0.0", "q=0.5");
    // returns true only when it is exactly zero.
    private static bool IsQualityZero(string parameters)
    {
        foreach (var param in parameters.Split(';'))
        {
            var p = param.Trim();
            if (!p.StartsWith("q=", StringComparison.OrdinalIgnoreCase)) continue;
            return double.TryParse(p.AsSpan(2), System.Globalization.CultureInfo.InvariantCulture, out var q) && q == 0;
        }
        return false;
    }

    private enum CompressionAlgo { Brotli, Gzip }
}
