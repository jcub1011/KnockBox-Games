using System.IO.Compression;
using KnockBox.Contracts;

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
    string gamesRoot, string compressedRoot, bool gzip, int minBytes,
    ILogger<GameAssetPrecompressor> logger)
{
    // Contents already compressed by their own format — re-compressing wastes CPU and rarely shrinks.
    private static readonly HashSet<string> IncompressibleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".ico",
        ".mp3", ".ogg", ".wav", ".mp4", ".webm", ".woff2",
        ".br", ".gz", ".zip",
    };

    // Coalescing gate: at most one reconcile runs at a time. A request that arrives mid-run sets
    // _rerun so the latest catalog state is processed once the current pass finishes — rapid
    // hot-reload bursts collapse into the minimum number of passes without ever missing the newest state.
    private readonly Lock _gate = new();
    private bool _running;
    private bool _rerun;
    private IReadOnlyCollection<GameManifest> _latest = [];

    /// <summary>
    /// Reconciles the cache to <paramref name="games"/>. Per-file errors are logged and skipped so one
    /// bad asset never aborts the pass. Re-entrant calls coalesce: a second caller records the new state
    /// and returns immediately while the first caller loops to pick it up.
    /// </summary>
    public void ReconcileAll(IReadOnlyCollection<GameManifest> games)
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
                IReadOnlyCollection<GameManifest> snapshot;
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

    private void ReconcileOnce(IReadOnlyCollection<GameManifest> games)
    {
        var liveIds = new HashSet<string>(games.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        var compressed = 0;
        var removed = PruneRemovedGames(liveIds);

        foreach (var id in liveIds)
        {
            var srcDir = Path.Combine(gamesRoot, id);
            if (!Directory.Exists(srcDir)) continue;
            compressed += CompressGameDir(id, srcDir);
            removed += PruneOrphanVariants(id, srcDir);
        }

        // Only narrate when something actually changed — this runs on a timer and would otherwise spam.
        if ((compressed > 0 || removed > 0) && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Pre-compression reconcile: {Compressed} asset(s) (re)compressed, {Removed} stale variant(s)/dir(s) removed.",
                compressed, removed);
    }

    // Deletes games-compressed/<id> for any id no longer in the catalog or whose source folder is gone.
    private int PruneRemovedGames(HashSet<string> liveIds)
    {
        if (!Directory.Exists(compressedRoot)) return 0;
        var removed = 0;
        foreach (var dir in Directory.EnumerateDirectories(compressedRoot))
        {
            var id = new DirectoryInfo(dir).Name;
            if (liveIds.Contains(id) && Directory.Exists(Path.Combine(gamesRoot, id))) continue;
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove stale compressed dir {Dir}.", dir); }
        }
        return removed;
    }

    // (Re)compresses source files whose recorded (mtime, length) no longer matches — or whose produced
    // variants were removed — and records the outcome in the per-game index so unchanged files are
    // skipped and not-beneficial files aren't re-attempted every pass. Returns the count processed.
    private int CompressGameDir(string id, string srcDir)
    {
        var dir = Path.Combine(compressedRoot, id);
        var oldIndex = LoadIndex(dir);
        var newIndex = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        var count = 0;

        foreach (var src in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(src);
                if (!ShouldCompress(info.Name, info.Length, minBytes)) continue;

                var relative = Path.GetRelativePath(srcDir, src);

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
    private int PruneOrphanVariants(string id, string srcDir)
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
            File.Move(tmp, path, overwrite: true);
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
