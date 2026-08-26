using KnockBox.Server.Games;
using KnockBox.Server.Hosting;

namespace KnockBox.Server.Admin;

/// <summary>
/// Measures what each game costs on disk, for the admin portal's catalog view.
///
/// A game's footprint is not one folder. It is the folder its files live in (under <c>games/</c> for a
/// hand-placed game, under the unpacked-package root for a <c>.kbg</c> one), <b>plus</b> the
/// pre-compressed <c>.br</c>/<c>.gz</c> variants the server derived from it, <b>plus</b> the source
/// <c>.kbg</c> archive if it was installed from one — which is still sitting in <c>games/</c>, because
/// that is what the installer watches to decide whether the game should still exist. Reporting only the
/// first would understate a large WASM game by roughly the size of its own compressed cache.
///
/// Results are cached because this walks directories, and the dashboard polls. The first read computes
/// synchronously (a caller must never be handed zeroes that look like a real answer); later reads return
/// the cached figures and, once stale, kick off one background refresh so no request waits on a walk.
/// </summary>
public sealed class DiskUsageReporter(
    ContentPaths.Resolved paths,
    GameCatalog catalog,
    TimeProvider clock,
    IConfiguration config,
    ILogger<DiskUsageReporter> logger)
{
    /// <summary>How long a measurement is served before a refresh is triggered.</summary>
    public static readonly TimeSpan DefaultCacheDuration = TimeSpan.FromSeconds(60);

    // Count everything, including hidden and system files, since they occupy the disk too. Reparse
    // points are skipped so a symlink that points at an ancestor can't send the walk round forever.
    // IgnoreInaccessible keeps one unreadable subfolder from failing the whole measurement.
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    // 0 disables caching (every read walks the directories) — useful when diagnosing a size that looks
    // wrong, and a foot-gun to leave on, hence the default.
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(
        config.GetValue("KnockBox:AdminDiskUsageCacheSeconds", (int)DefaultCacheDuration.TotalSeconds));
    private volatile Report? _cached;
    // 0 = idle, 1 = a background refresh is in flight. Guards against the dashboard's poll queueing a
    // new walk every few seconds while the previous one is still running.
    private int _refreshing;

    /// <summary>One game's disk footprint, broken out so the portal can explain the total.</summary>
    /// <param name="BackupBytes">
    /// Retained previous versions of a managed package, kept for rollback. Counted per game because it is
    /// the operator's own retention setting that produced it — an unexplained multiple of a large game's
    /// package size is exactly the figure someone opens this page to understand.
    /// </param>
    public sealed record GameDisk(
        string Id, long DirectoryBytes, long CompressedBytes, long PackageBytes, long BackupBytes)
    {
        public long TotalBytes => DirectoryBytes + CompressedBytes + PackageBytes + BackupBytes;
    }

    /// <summary>A complete measurement.</summary>
    public sealed record Report(
        DateTimeOffset TakenAt,
        IReadOnlyList<GameDisk> Games,
        long CompressedCacheBytes,
        long LogsBytes,
        long ManagedRootBytes)
    {
        /// <summary>Total across every game (folders, compressed variants, source packages and backups).</summary>
        public long TotalGameBytes => Games.Sum(g => g.TotalBytes);
    }

    // Keyed by full path. Small and bounded by the number of roots — the portal only ever asks about the
    // parents of game directories and packages, which are the three or four configured roots.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string? Blocked)>
        _writability = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Null when the server can create and delete files in <paramref name="directory"/>, else why it
    /// can't — cached on the same cadence as the disk figures, and for a sharper reason than they are.
    /// </summary>
    /// <remarks>
    /// The answer is reached by WRITING a file there (see <see cref="DirectoryProbe"/>), and the games
    /// root is watched: uncached, the catalog tab's 20-second poll probed once per game per poll, each
    /// probe raised a change event, and every event scheduled a rediscovery — a rescan loop the portal
    /// inflicted on itself simply by being open. It is also advisory: <c>AdminOperations.DeleteGame</c>
    /// re-probes for real before it removes anything, so a minute-stale "you could delete this" costs an
    /// error message, never a bad delete.
    /// </remarks>
    public string? WhyNotWritable(string directory)
    {
        var now = clock.GetUtcNow();
        if (_cacheDuration > TimeSpan.Zero
            && _writability.TryGetValue(directory, out var cached)
            && now - cached.At < _cacheDuration)
        {
            return cached.Blocked;
        }

        var blocked = DirectoryProbe.WhyNotWritable(directory);
        _writability[directory] = (now, blocked);
        return blocked;
    }

    /// <summary>
    /// The latest measurement, computing one synchronously if none exists yet and scheduling a
    /// background refresh when the cached one has gone stale.
    /// </summary>
    public Report Current()
    {
        var cached = _cached;
        if (cached is null) return _cached = Measure();

        // Caching off (0, or a nonsense negative) means measure HERE. Falling through to the staleness
        // check below would schedule a background walk and still return the previous report, so every
        // read would pay for a walk and none would see its result — which is the opposite of what an
        // operator turning the cache off to diagnose a wrong-looking size is asking for.
        if (_cacheDuration <= TimeSpan.Zero) return _cached = Measure();

        if (clock.GetUtcNow() - cached.TakenAt >= _cacheDuration) ScheduleRefresh();
        return cached;
    }

    private void ScheduleRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try { _cached = Measure(); }
            // A failed measurement must never surface as an unobserved task exception; the previous
            // report simply stands until the next attempt.
            catch (Exception ex) { logger.LogWarning(ex, "Disk usage measurement failed; keeping the previous figures."); }
            finally { Volatile.Write(ref _refreshing, 0); }
        });
    }

    private Report Measure()
    {
        var games = new List<GameDisk>();
        foreach (var (id, location) in catalog.GameLocations)
        {
            games.Add(new GameDisk(
                id,
                DirectoryBytes(location.Directory),
                DirectoryBytes(Path.Combine(paths.GamesCompressedRoot, id)),
                PackageBytes(id),
                DirectoryBytes(ManagedPackageLayout.BackupDir(paths.GamesManagedRoot, id))));
        }
        games.Sort((a, b) => b.TotalBytes.CompareTo(a.TotalBytes)); // biggest first: the reason to look

        return new Report(
            clock.GetUtcNow(),
            games,
            DirectoryBytes(paths.GamesCompressedRoot),
            DirectoryBytes(paths.LogsRoot),
            DirectoryBytes(paths.GamesManagedRoot));
    }

    /// <summary>Total size of every file under a directory, or 0 if it doesn't exist or can't be read.</summary>
    public static long DirectoryBytes(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        try
        {
            long total = 0;
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", WalkOptions))
            {
                // A file deleted between listing and stat throws here; it contributes nothing anyway.
                try { total += file.Length; } catch (FileNotFoundException) { } catch (IOException) { }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // The source archive for a package-installed game, in whichever package root actually holds it —
    // resolved rather than derived, because the installer accepts any *.kbg file name and a
    // portal-installed package lives in the managed root, not games/.
    private long PackageBytes(string id)
    {
        if (GamePackageLocations.Find(paths, id) is not { } package) return 0;
        try { return new FileInfo(package.Path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
