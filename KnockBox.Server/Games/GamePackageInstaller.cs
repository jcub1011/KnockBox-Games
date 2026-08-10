using System.IO.Compression;

namespace KnockBox.Server.Games;

/// <summary>
/// Installs <c>.kbg</c> game packages that an administrator copies into the games directory. Copying
/// the file in is the entire installation procedure — no CLI on the host, no restart.
///
/// Because the games directory is mounted READ-ONLY in production (several servers may share one game
/// library), packages cannot be expanded in place. They are extracted into a separate writable root —
/// exactly how the pre-compressed asset cache works — which <see cref="GameCatalog"/> searches after
/// the games directory itself, so a hand-placed folder always wins a contested id.
///
/// This class owns no watcher and no timer. It hangs off <see cref="GameCatalog.Discovered"/>, the
/// signal the watcher and the polling fallback already drive, and asks for a rediscovery via
/// <see cref="GameCatalog.ScheduleRescan"/> once it has changed something. That keeps every rescan on
/// the catalog's single debounced path, where two scans can't race and publish out of order.
/// </summary>
public sealed class GamePackageInstaller(
    string gamesRoot,
    string unpackedRoot,
    GamePackageLimits limits,
    GameAssetPrecompressor? precompressor,
    ILogger<GamePackageInstaller> logger)
{
    // Per-game freshness record, written INSIDE the extracted folder. Keeping it there (rather than in
    // one central index) means it is deleted along with the folder, so the index can never disagree
    // with what is on disk. Dot-prefixed so PhysicalFileProvider's default exclusion filters never
    // serve it.
    private const string MarkerFileName = ".kb-package";

    // Staging and to-be-deleted directories live under this single dot-prefixed container. It holds no
    // GAME.json, so GameCatalog skips it silently instead of warning about a folder whose name doesn't
    // match a manifest id.
    private const string StagingDirName = ".staging";

    // A package must present the SAME (mtime, length) on two consecutive passes before it is installed.
    // A large file still being copied in changes between passes, so this is what stops a half-copied
    // archive from being read at all — cheaper and far more reliable than any timing heuristic.
    private readonly Dictionary<string, (long Mtime, long Length)> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    // A package whose source file has been gone for this many consecutive passes is uninstalled.
    // Requiring more than one pass matters because an operator replacing a package by delete-then-copy
    // transiently has no file there, and uninstalling in that window would drop a game out of the
    // catalog mid-session.
    private const int PassesBeforeUninstall = 2;
    private readonly Dictionary<string, int> _absentPasses = new(StringComparer.OrdinalIgnoreCase);

    // Packages that failed validation, keyed by identity (path + mtime + length) so an edited or
    // replaced file is retried while a broken one isn't re-read every 60 seconds. Process-lifetime
    // only: a restart SHOULD retry, in case the failure was environmental.
    private readonly HashSet<string> _quarantined = new(StringComparer.OrdinalIgnoreCase);

    // Coalescing gate: at most one pass runs at a time, and a request arriving mid-pass sets _rerun so
    // the newest state is still processed once. Same shape as GameAssetPrecompressor.
    private readonly Lock _gate = new();
    private bool _running;
    private bool _rerun;

    /// <summary>
    /// How many <c>.kbg</c> files were seen in the games directory on the last pass. Lets the
    /// deployment diagnostics distinguish "no games because none were provided" from "no games even
    /// though packages are sitting right there", which is otherwise an invisible failure.
    /// </summary>
    public int PackagesObserved { get; private set; }

    /// <summary>
    /// A summary of packages the last pass could not install, or null when all of them installed.
    /// Surfaced live on the deployment-warning page.
    /// </summary>
    public string? InstallFailure { get; private set; }

    /// <summary>The outcome of a pass, and whether another one is owed.</summary>
    /// <param name="Changed">Something was installed or uninstalled: the catalog should rediscover.</param>
    /// <param name="Pending">
    /// A decision was deliberately deferred — a package hasn't settled yet, or one is counting down to
    /// being uninstalled. The caller must schedule another pass, or that work would stall until some
    /// unrelated file event happened to arrive. With the polling fallback disabled (the default outside
    /// Docker) no such event is coming, so treating this as "nothing to do" would mean a copied-in
    /// package never installs at all.
    /// </param>
    public readonly record struct ReconcileResult(bool Changed, bool Pending);

    /// <summary>
    /// Brings the unpacked root in line with the packages in the games directory: installs new and
    /// changed ones, uninstalls those whose file is gone. Safe to call often — a pass where nothing
    /// changed costs one directory listing plus a stat per package.
    /// </summary>
    public ReconcileResult Reconcile()
    {
        lock (_gate)
        {
            if (_running) { _rerun = true; return default; }
            _running = true;
        }

        try
        {
            var changed = false;
            while (true)
            {
                var pass = ReconcileOnce();
                changed |= pass.Changed;
                lock (_gate)
                {
                    if (!_rerun) { _running = false; return new ReconcileResult(changed, pass.Pending); }
                    _rerun = false;
                }
            }
        }
        catch
        {
            lock (_gate) { _running = false; }
            throw;
        }
    }

    private ReconcileResult ReconcileOnce()
    {
        if (!Directory.Exists(gamesRoot)) return default;

        // Materialize eagerly so an access failure throws HERE. Pruning on a failed listing would read
        // "no packages exist" and uninstall the entire library over a transient permissions problem, so
        // this must return before the prune step rather than continuing with an empty list.
        List<string> packages;
        try
        {
            packages = [.. Directory.EnumerateFiles(gamesRoot, GamePackage.SearchPattern)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Cannot list game packages in {Path}; leaving installed packages untouched.", gamesRoot);
            return default;
        }

        PackagesObserved = packages.Count;
        if (packages.Count == 0 && !Directory.Exists(unpackedRoot))
        {
            InstallFailure = null;
            return default; // nothing installed and nothing to install: don't create the root for no reason
        }

        try
        {
            Directory.CreateDirectory(unpackedRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cannot create the unpacked-package root {Path}; .kbg packages cannot be installed.", unpackedRoot);
            InstallFailure = $"The unpacked-package folder '{unpackedRoot}' could not be created ({ex.Message}). " +
                "It must be writable by the server — in Docker the container runs as UID 1654.";
            return default;
        }

        SweepStaging();

        var changed = false;
        var pending = false;
        var failures = new List<string>();
        // Deterministic order, so which package wins a contested id is stable across passes and hosts
        // rather than depending on directory-enumeration order.
        packages.Sort(StringComparer.OrdinalIgnoreCase);

        // Source file name -> id, for the prune step and for detecting two packages claiming one id.
        var installedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var claimedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in packages)
        {
            try
            {
                var outcome = Install(path, claimedIds, installedBy);
                changed |= outcome.Changed;
                pending |= outcome.Pending;
            }
            catch (GamePackageException ex)
            {
                // A malformed package is an operator problem, not a server fault: name the file, say
                // what's wrong, and quarantine it so the message appears once rather than every pass.
                logger.LogError("Cannot install {File}: {Reason}", Path.GetFileName(path), ex.Message);
                Quarantine(path);
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected failure installing {File}; it will be retried.", Path.GetFileName(path));
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        var uninstall = Uninstall(installedBy);
        changed |= uninstall.Changed;
        pending |= uninstall.Pending;

        InstallFailure = failures.Count == 0
            ? null
            : $"{failures.Count} game package(s) could not be installed — {string.Join("; ", failures)}";
        return new ReconcileResult(changed, pending);
    }

    /// <summary>Installs one package if it is settled, not quarantined, and not already current.</summary>
    private ReconcileResult Install(string path, Dictionary<string, string> claimedIds, Dictionary<string, string> installedBy)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return default; // deleted between listing and now
        var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);

        // Settle check: only proceed when this pass sees exactly what the previous pass saw. Pending, not
        // done — the caller must come back, or a package copied in while nothing else changes would sit
        // there forever.
        var settled = _lastSeen.TryGetValue(path, out var previous) && previous == stamp;
        _lastSeen[path] = stamp;
        if (!settled)
        {
            logger.LogDebug("Waiting for {File} to settle before installing it.", Path.GetFileName(path));
            return new ReconcileResult(Changed: false, Pending: true);
        }

        if (_quarantined.Contains(QuarantineKey(path, stamp)))
        {
            // Settled and known-bad: nothing more to do until the file itself changes, so NOT pending —
            // otherwise a single malformed package would keep the rescan loop running forever.
            return default;
        }

        // FileShare.Read denies concurrent WRITERS, so on Windows this fails outright while something is
        // still writing the file — a useful second line of defence behind the settle check. (POSIX
        // advisory locking gives no such guarantee, which is why the settle check is the primary guard.)
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = OpenArchive(stream, path);

        var (header, id) = GamePackageReader.PeekIdentity(archive);

        if (claimedIds.TryGetValue(id, out var winner))
        {
            logger.LogWarning(
                "Both {Winner} and {Loser} contain the game id '{Id}'. Keeping {Winner} and ignoring {Loser} — " +
                "remove one of them.", Path.GetFileName(winner), Path.GetFileName(path), id, Path.GetFileName(winner),
                Path.GetFileName(path));
            return default;
        }
        claimedIds[id] = path;

        var target = Path.Combine(unpackedRoot, id);
        installedBy[Path.GetFileName(path)] = id;
        _absentPasses.Remove(Path.GetFileName(path));

        if (IsCurrent(target, path, stamp))
        {
            logger.LogDebug("Game package {File} is already installed and current.", Path.GetFileName(path));
            return default;
        }

        var plan = GamePackageReader.Read(archive, limits);
        var staging = Path.Combine(unpackedRoot, StagingDirName, $"{id}-{Guid.NewGuid():N}");

        try
        {
            var written = GamePackageReader.Extract(plan, staging, limits);
            WriteMarker(staging, path, stamp);
            SwapIntoPlace(staging, target);

            logger.LogInformation("Installed game package '{Id}' ({Name}{Version}) from {File}: {Count} file(s).",
                id, header.Name ?? id, header.Version is null ? "" : " " + header.Version, Path.GetFileName(path), written.Count);

            Seed(plan, id, target);
            return new ReconcileResult(Changed: true, Pending: false);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private static ZipArchive OpenArchive(Stream stream, string path)
    {
        try
        {
            return new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            // A ZIP's central directory sits at the END of the file, so a truncated or still-copying
            // archive reliably lands here. Not quarantined by the caller's identity key alone: once the
            // copy completes, mtime/length change and it is retried.
            throw new GamePackageException(
                $"not a readable ZIP archive ({ex.Message}). If the file is still being copied, this resolves itself.");
        }
    }

    /// <summary>Hands the package's Brotli payloads to the pre-compressed cache instead of re-compressing them.</summary>
    private void Seed(GamePackageReader.PackagePlan plan, string id, string target)
    {
        if (precompressor is null) return;
        try
        {
            // Entries are opened lazily and one at a time, while the archive is still open.
            precompressor.SeedFromPackage(id, target, plan.Files.Select(f =>
                (f.LogicalPath.Replace('/', Path.DirectorySeparatorChar),
                 f.Brotli ? (Func<Stream>?)(() => f.Entry.Open()) : null)));
        }
        catch (Exception ex)
        {
            // Seeding is an optimisation. Losing it costs CPU on the next reconcile, nothing more.
            logger.LogWarning(ex, "Could not seed the pre-compressed cache for '{Id}'; it will be compressed normally.", id);
        }
    }

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="staging"/> as close to atomically as the
    /// filesystem allows: move the live folder aside, move the new one in, then delete the old one.
    /// </summary>
    /// <remarks>
    /// The move-aside step is required, not cosmetic: <see cref="Directory.Move"/> fails when the
    /// destination exists, and on Windows deleting a directory whose files are open throws (POSIX allows
    /// unlink-while-open), so deleting the live folder first would fail whenever a request happened to be
    /// streaming an asset. A leftover aside-folder is swept on the next pass.
    /// </remarks>
    private void SwapIntoPlace(string staging, string target)
    {
        var aside = Path.Combine(unpackedRoot, StagingDirName, $"replaced-{Guid.NewGuid():N}");
        var movedAside = false;

        if (Directory.Exists(target))
        {
            Directory.Move(target, aside);
            movedAside = true;
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch when (movedAside)
        {
            // Put the previous version back rather than leaving the game missing entirely.
            try { Directory.Move(aside, target); } catch { /* best effort: the next pass reinstalls */ }
            throw;
        }

        if (movedAside) TryDelete(aside);
    }

    /// <summary>Uninstalls extracted games whose source package has been gone for long enough.</summary>
    private ReconcileResult Uninstall(Dictionary<string, string> installedBy)
    {
        if (!Directory.Exists(unpackedRoot)) return default;

        List<string> dirs;
        try
        {
            dirs = [.. Directory.EnumerateDirectories(unpackedRoot)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Cannot list {Path}; leaving installed packages untouched.", unpackedRoot);
            return default;
        }

        var live = new HashSet<string>(installedBy.Keys, StringComparer.OrdinalIgnoreCase);
        var removed = false;
        var countingDown = false;

        foreach (var dir in dirs)
        {
            var name = new DirectoryInfo(dir).Name;
            if (name == StagingDirName) continue;

            var source = ReadMarkerSource(dir);
            // No marker means this folder was not put here by a completed install (a crash mid-swap, or
            // something an operator dropped into a server-owned cache). Either way it is not ours to
            // keep, but it still goes through the same patience as a vanished package.
            if (source is not null && live.Contains(source))
            {
                _absentPasses.Remove(name);
                continue;
            }

            var misses = _absentPasses.GetValueOrDefault(name) + 1;
            _absentPasses[name] = misses;
            if (misses < PassesBeforeUninstall)
            {
                // Owed another pass, or the countdown stalls until an unrelated file event arrives.
                countingDown = true;
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
                _absentPasses.Remove(name);
                removed = true;
                logger.LogInformation("Uninstalled game '{Id}': its package {File} is no longer in the games folder.",
                    name, source ?? "(unknown)");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not remove the unpacked game folder {Dir}; retrying next pass.", dir);
            }
        }

        return new ReconcileResult(removed, countingDown);
    }

    /// <summary>True when the extracted folder was produced by exactly this package file and version.</summary>
    private bool IsCurrent(string target, string packagePath, (long Mtime, long Length) stamp)
    {
        if (!Directory.Exists(target)) return false;
        var marker = ReadMarker(target);
        return marker is not null
            && marker.Value.Mtime == stamp.Mtime
            && marker.Value.Length == stamp.Length
            && string.Equals(marker.Value.Source, Path.GetFileName(packagePath), StringComparison.OrdinalIgnoreCase);
    }

    // Format: "<mtimeTicks>\t<length>\t<source file name>". The name is last so a tab in a filename
    // can't corrupt the numeric fields — same convention as the pre-compress index.
    private void WriteMarker(string dir, string packagePath, (long Mtime, long Length) stamp) =>
        File.WriteAllText(Path.Combine(dir, MarkerFileName),
            $"{stamp.Mtime}\t{stamp.Length}\t{Path.GetFileName(packagePath)}\n");

    private (long Mtime, long Length, string Source)? ReadMarker(string dir)
    {
        var path = Path.Combine(dir, MarkerFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var line = File.ReadLines(path).FirstOrDefault();
            if (line is null) return null;
            var parts = line.Split('\t', 3);
            if (parts.Length < 3) return null;
            if (!long.TryParse(parts[0], out var mtime)) return null;
            if (!long.TryParse(parts[1], out var length)) return null;
            return (mtime, length, parts[2]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable marker just means "looks stale", which reinstalls. Safe.
            return null;
        }
    }

    private string? ReadMarkerSource(string dir) => ReadMarker(dir)?.Source;

    private void Quarantine(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists) _quarantined.Add(QuarantineKey(path, (info.LastWriteTimeUtc.Ticks, info.Length)));
    }

    private static string QuarantineKey(string path, (long Mtime, long Length) stamp) =>
        $"{path}|{stamp.Mtime}|{stamp.Length}";

    /// <summary>Clears staging leftovers from a crashed or interrupted pass.</summary>
    private void SweepStaging()
    {
        var staging = Path.Combine(unpackedRoot, StagingDirName);
        if (!Directory.Exists(staging)) return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(staging)) TryDelete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not sweep the staging folder {Dir}.", staging);
        }
    }

    private static void TryDelete(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best effort: swept on the next pass */ }
    }
}
