using System.IO.Compression;
using System.Text.Json;
using KnockBox.Server.Hosting;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// Installs <c>.kbg</c> game packages. Placing the file in a package root is the entire installation
/// procedure — no CLI on the host, no restart.
///
/// Because the games directory is mounted READ-ONLY in production (several servers may share one game
/// library), packages cannot be expanded in place. They are extracted into a separate writable root —
/// exactly how the pre-compressed asset cache works — which <see cref="GameCatalog"/> searches after
/// the games directory itself, so a hand-placed folder always wins a contested id.
///
/// It scans MORE THAN ONE package root: <c>games/</c>, where an operator drops a package by hand, and
/// the writable managed root, where the admin portal installs what it fetched or was handed. Roots are
/// scanned in order and the first to claim an id keeps it, so a hand-placed package always beats a
/// portal-installed one — the same precedence <see cref="GameCatalog"/> applies to folders.
///
/// This class owns no watcher and no timer. It hangs off <see cref="GameCatalog.Discovered"/>, the
/// signal the watcher and the polling fallback already drive, and asks for a rediscovery via
/// <see cref="GameCatalog.ScheduleRescan"/> once it has changed something. That keeps every rescan on
/// the catalog's single debounced path, where two scans can't race and publish out of order.
/// </summary>
public sealed class GamePackageInstaller(
    IReadOnlyList<GamePackageInstaller.PackageRoot> roots,
    string unpackedRoot,
    GamePackageLimits limits,
    GameAssetPrecompressor? precompressor,
    ILogger<GamePackageInstaller> logger)
{
    /// <summary>One directory scanned for <c>.kbg</c> files.</summary>
    /// <param name="Token">
    /// Which root this is, recorded in each extracted game's marker — one of the
    /// <see cref="PackageMarker"/> root constants. Two roots can hold same-named packages, so the token
    /// is what stops an extracted game from being matched to the wrong file.
    /// </param>
    public readonly record struct PackageRoot(string Path, string Token);

    // Staging and to-be-deleted directories live under this single dot-prefixed container. It holds no
    // GAME.json, so GameCatalog skips it silently instead of warning about a folder whose name doesn't
    // match a manifest id.
    private const string StagingDirName = ".staging";

    // A package must present the SAME (mtime, length) on two consecutive passes before it is installed.
    // A large file still being copied in changes between passes, so this is what stops a half-copied
    // archive from being read at all — cheaper and far more reliable than any timing heuristic. It only
    // works if consecutive passes are separated by real time, which is why Reconcile() runs exactly ONE
    // pass per call and defers the next one to the caller's debounced rescan.
    private readonly Dictionary<string, (long Mtime, long Length)> _lastSeen = new(StringComparer.OrdinalIgnoreCase);

    // Source file PATH -> the id it installed. Lets an unchanged pass verify the extracted folder's
    // marker without opening the archive at all: recovering the id was the ONLY reason the no-change
    // path used to seek to the end of a potentially huge ZIP and inflate its header. Empty after a
    // restart, which just means the first pass pays the old cost once.
    //
    // Keyed by full path rather than file name because two roots may hold packages with the SAME name:
    // keyed by name, "demo.kbg" in games/ and "demo.kbg" in the managed root would share one row and
    // fight over which id it recorded, and Forget would then drop the survivor's bookkeeping too.
    private readonly Dictionary<string, string> _installedIds = new(StringComparer.OrdinalIgnoreCase);

    // Packages this server itself moved into place atomically, vouched for via Adopt so the next pass
    // installs them without waiting for the two-pass settle check below. A queue rather than a direct
    // write into _lastSeen because Adopt is called from request/job threads while _lastSeen is touched
    // only on the (serialized) reconcile pass: an adoption arriving mid-pass simply lands on the next
    // one, which costs a debounce interval and never correctness.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Path, long Mtime, long Length)>
        _adoptions = new();

    // A package whose source file has been gone for this many consecutive passes is uninstalled.
    // Requiring more than one pass matters because an operator replacing a package by delete-then-copy
    // transiently has no file there, and uninstalling in that window would drop a game out of the
    // catalog mid-session.
    private const int PassesBeforeUninstall = 2;
    private readonly Dictionary<string, int> _absentPasses = new(StringComparer.OrdinalIgnoreCase);

    // Packages that failed validation: path -> the (mtime, length) that failed, so an edited or replaced
    // file is retried while a broken one isn't re-read every 60 seconds. Keyed by PATH (with the stamp
    // as the value rather than part of the key) so a retry replaces the row instead of adding one, and
    // so the sweep at the end of a pass can drop rows for packages that are gone. Process-lifetime only:
    // a restart SHOULD retry, in case the failure was environmental.
    private readonly Dictionary<string, (long Mtime, long Length)> _quarantined = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Per-package bookkeeping rows currently held (settle stamps, quarantine stamps, installed ids). For
    /// tests/diagnostics — proves <see cref="Forget"/> actually reclaims rows for packages that are gone.
    /// Only meaningful between passes: the maps are touched solely on the (serialized) reconcile pass.
    /// </summary>
    internal int TrackedPackages => _lastSeen.Count + _quarantined.Count + _installedIds.Count;

    /// <summary>
    /// Raised with a game id the moment its files have actually been extracted and swapped into place.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="GameCatalog.Discovered"/>, which says the catalog has been re-read — a
    /// pass in which nothing was extracted still fires that. The one caller that needs this precision is
    /// <see cref="PackageManager"/>: placing a package only renames the <c>.kbg</c> and asks for a rescan,
    /// so without a signal at THIS point an update reported success, released the game's lifecycle gate,
    /// and let a player start a lobby on the old build moments before the directory was swapped under it.
    ///
    /// Raised on the (serialized) reconcile thread, so a handler must not block; the only one completes a
    /// <c>TaskCompletionSource</c>. A throwing handler is contained here rather than being allowed to
    /// abandon the rest of the pass.
    /// </remarks>
    public event Action<string>? Installed;

    private void OnInstalled(string gameId)
    {
        try { Installed?.Invoke(gameId); }
        catch (Exception ex) { logger.LogError(ex, "An Installed handler threw; continuing."); }
    }

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
    /// <remarks>
    /// Exactly ONE pass runs per call. A request that arrives mid-pass is reported back as
    /// <c>Pending</c> instead of being served by an immediate second pass, because both of this
    /// class's guards — settle-before-install and absent-before-uninstall — compare a pass against
    /// the state the PREVIOUS pass recorded. Two passes microseconds apart make a 400 MB archive that
    /// is still being copied look settled, and drive the uninstall countdown to completion inside one
    /// call. The caller turns Pending into another rescan through the catalog's 500 ms debounce, which
    /// is where the real elapsed time between passes comes from.
    /// </remarks>
    public ReconcileResult Reconcile()
    {
        lock (_gate)
        {
            if (_running) { _rerun = true; return default; }
            _running = true;
            _rerun = false;
        }

        try
        {
            var pass = ReconcileOnce();
            lock (_gate)
            {
                var owed = _rerun;
                _rerun = false;
                _running = false;
                return new ReconcileResult(pass.Changed, pass.Pending || owed);
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
        DrainAdoptions();

        // Materialize eagerly so an access failure throws HERE. Pruning on a failed listing would read
        // "no packages exist" and uninstall the entire library over a transient permissions problem. A
        // root that is merely MISSING is the same hazard for the same reason — it is indistinguishable
        // from every package in it having been deleted.
        //
        // But that hazard is confined to the games installed FROM the unreadable root, so it is answered
        // by protecting those (see `blindRoots` at the uninstall step) rather than by abandoning the whole
        // pass. Abandoning it meant one root the server could not read — a games-managed folder whose
        // creation failed at startup, say — silently switched off `.kbg` hot-drop for the other root as
        // well, leaving the platform's headline feature dead behind a single non-blocking diagnostic.
        var packages = new List<(string Path, string Root)>();
        var blindRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.Path))
            {
                blindRoots.Add(root.Token);
                unreadable.Add($"'{root.Path}' does not exist");
                continue;
            }

            List<string> found;
            try
            {
                found = [.. Directory.EnumerateFiles(root.Path, GamePackage.SearchPattern)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "Cannot list game packages in {Path}; leaving its installed packages untouched.",
                    root.Path);
                blindRoots.Add(root.Token);
                unreadable.Add($"'{root.Path}' could not be listed ({ex.Message})");
                continue;
            }

            // Sorted WITHIN each root and appended in root order, rather than one sort across all of
            // them: which package wins a contested id then follows root precedence (games/ first) and is
            // stable across passes and hosts, instead of depending on enumeration order or path spelling.
            found.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var path in found) packages.Add((path, root.Token));
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

        // Game id -> the package file that claimed it, so a second package claiming the same id is
        // reported and ignored rather than fighting over one extracted folder.
        var claimedIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Every package file that was PRESENT this pass, whatever came of it. This is what the prune step
        // treats as live. It used to be the set of packages that installed SUCCESSFULLY — but a package
        // that hasn't settled yet, or one that is quarantined, never gets that far, so "still being
        // copied" and "malformed replacement" both read as "the package is gone" and deleted the
        // extracted game that was serving players perfectly well.
        //
        // Two views of the same set, because they answer different questions: seenPaths keys the
        // _installedIds sweep (which is keyed by path), while live keys the uninstall check against what
        // a marker records — a (root, file name) pair, since the same name can exist in both roots.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, root) in packages)
        {
            try
            {
                var outcome = Install(path, root, claimedIds, seenPaths, live);
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

        var uninstall = Uninstall(live, blindRoots);
        changed |= uninstall.Changed;
        pending |= uninstall.Pending;

        Forget(packages, seenPaths);

        // An unreadable root is reported alongside a malformed package rather than only logged: both mean
        // "a game you expect to be here isn't", and this is the string the deployment-warning page shows.
        var problems = new List<string>();
        if (failures.Count > 0)
            problems.Add($"{failures.Count} game package(s) could not be installed — {string.Join("; ", failures)}");
        if (unreadable.Count > 0)
            problems.Add($"{unreadable.Count} package folder(s) could not be read — {string.Join("; ", unreadable)}. " +
                "Games installed from them are left in place, but nothing there can be installed or removed.");
        InstallFailure = problems.Count == 0 ? null : string.Join(" ", problems);
        return new ReconcileResult(changed, pending);
    }

    /// <summary>
    /// Drops per-package bookkeeping for files that are no longer in the games folder, so the maps
    /// describe what is on disk rather than everything ever seen. Without it, an operator iterating on
    /// a broken package leaves one quarantine row per attempt, and every package name ever dropped in
    /// keeps a settle row for the process lifetime. (<c>_absentPasses</c> is already cleaned as part of
    /// install/uninstall.)
    /// </summary>
    private void Forget(List<(string Path, string Root)> packages, HashSet<string> seenPaths)
    {
        var present = new HashSet<string>(packages.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var stale in _lastSeen.Keys.Where(p => !present.Contains(p)).ToList())
            _lastSeen.Remove(stale);
        foreach (var stale in _quarantined.Keys.Where(p => !present.Contains(p)).ToList())
            _quarantined.Remove(stale);
        // Swept against the pass's seen-set rather than the listing, because a package deleted between
        // the listing and its Install() call is present here but was never seen.
        foreach (var stale in _installedIds.Keys.Where(p => !seenPaths.Contains(p)).ToList())
            _installedIds.Remove(stale);
    }

    /// <summary>
    /// Vouches that a package file is complete, so the next pass installs it without waiting for the
    /// two-pass settle check. Only ever valid for a file this server renamed into place itself.
    /// </summary>
    /// <remarks>
    /// The settle check exists because copying a large archive in is not atomic, and a half-copied file
    /// must never be read. A same-volume <see cref="File.Move(string, string, bool)"/> has no such
    /// window: the file is complete the instant it appears under that name. Making an operator who
    /// clicked Install wait two debounced passes buys nothing and makes the portal look stuck.
    ///
    /// The caller still has to ask for a rescan (<see cref="GameCatalog.ScheduleRescan"/>) — this only
    /// removes the extra round trip, it does not schedule anything.
    /// </remarks>
    public void Adopt(string packagePath)
    {
        try
        {
            var info = new FileInfo(packagePath);
            if (info.Exists)
                _adoptions.Enqueue((Path.GetFullPath(packagePath), info.LastWriteTimeUtc.Ticks, info.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not adopting is never wrong, only slower: the package settles the ordinary way instead.
            logger.LogDebug(ex, "Could not adopt {Path}; it will settle over two passes instead.", packagePath);
        }
    }

    /// <summary>Seeds the settle record from vouched-for packages. Runs first, on the reconcile thread.</summary>
    private void DrainAdoptions()
    {
        while (_adoptions.TryDequeue(out var adoption))
            _lastSeen[adoption.Path] = (adoption.Mtime, adoption.Length);
    }

    /// <summary>Installs one package if it is settled, not quarantined, and not already current.</summary>
    private ReconcileResult Install(
        string path, string root, Dictionary<string, string> claimedIds,
        HashSet<string> seenPaths, HashSet<string> live)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return default; // deleted between listing and now
        var fileName = Path.GetFileName(path);
        // Record the file as PRESENT before any early return below. Every one of them leaves an
        // already-extracted game in place, so the prune step must not read them as "package gone".
        seenPaths.Add(path);
        live.Add(LiveKey(root, fileName));
        var stamp = (info.LastWriteTimeUtc.Ticks, info.Length);

        // Settle check: only proceed when this pass sees exactly what the previous pass saw. Pending, not
        // done — the caller must come back, or a package copied in while nothing else changes would sit
        // there forever.
        var settled = _lastSeen.TryGetValue(path, out var previous) && previous == stamp;
        _lastSeen[path] = stamp;
        if (!settled)
        {
            logger.LogDebug("Waiting for {File} to settle before installing it.", fileName);
            return new ReconcileResult(Changed: false, Pending: true);
        }

        if (_quarantined.TryGetValue(path, out var badStamp) && badStamp == stamp)
        {
            // Settled and known-bad: nothing more to do until the file itself changes, so NOT pending —
            // otherwise a single malformed package would keep the rescan loop running forever.
            return default;
        }

        // Fast path for the overwhelmingly common "nothing changed" pass: if we know which id this file
        // installed and that folder's marker still matches, the archive answers no question worth
        // opening it for. Recovering the id is the only thing the open bought us, and a ZIP's central
        // directory lives at the END of the file — so this is what makes Reconcile's documented cost
        // (a listing plus a stat per package) true rather than aspirational.
        if (_installedIds.TryGetValue(path, out var knownId)
            && IsCurrent(Path.Combine(unpackedRoot, knownId), path, root, stamp))
        {
            if (!TryClaim(knownId, path, claimedIds)) return default;
            logger.LogDebug("Game package {File} is already installed and current.", fileName);
            return default;
        }

        // FileShare.Read denies concurrent WRITERS, so on Windows this fails outright while something is
        // still writing the file — a useful second line of defence behind the settle check. (POSIX
        // advisory locking gives no such guarantee, which is why the settle check is the primary guard.)
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = OpenArchive(stream, path);

        var (header, id) = GamePackageReader.PeekIdentity(archive);

        if (!TryClaim(id, path, claimedIds)) return default;

        // No _absentPasses bookkeeping here: that map is keyed by the extracted DIRECTORY name (the game
        // id), not by the package file name, and Uninstall already clears a game's countdown the moment it
        // sees the game's marker source among this pass's live packages. Resetting it from here only ever
        // looked like it was doing something.
        var target = Path.Combine(unpackedRoot, id);

        if (IsCurrent(target, path, root, stamp))
        {
            _installedIds[path] = id; // seed the fast path (e.g. first pass after a restart)
            logger.LogDebug("Game package {File} is already installed and current.", fileName);
            return default;
        }

        var plan = GamePackageReader.Read(archive, limits);
        var staging = Path.Combine(unpackedRoot, StagingDirName, $"{id}-{Guid.NewGuid():N}");

        try
        {
            var written = GamePackageReader.Extract(plan, staging, limits);
            PackageMarker.Write(staging, path, root, stamp);
            SwapIntoPlace(staging, target);

            logger.LogInformation("Installed game package '{Id}' ({Name}{Version}) from {File}: {Count} file(s).",
                id, header.Name ?? id, header.Version is null ? "" : " " + header.Version, fileName, written.Count);

            _installedIds[path] = id;
            Seed(plan, id, target);
            // Raised only for a real extraction — the two "already installed and current" fast paths above
            // return before this. PackageManager holds the game's lifecycle gate closed until it fires, so
            // an update stays unlaunchable until the new files are the ones being served rather than only
            // until the .kbg was renamed into place.
            OnInstalled(id);
            return new ReconcileResult(Changed: true, Pending: false);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Claims an id for one package file, or logs the collision and refuses. Two packages cannot share
    /// an id: the loser's assets would still be reachable by path while the catalog served the winner's
    /// manifest.
    /// </summary>
    private bool TryClaim(string id, string path, Dictionary<string, string> claimedIds)
    {
        if (claimedIds.TryGetValue(id, out var winner))
        {
            logger.LogWarning(
                "Both {Winner} and {Loser} contain the game id '{Id}'. Keeping {Winner} and ignoring {Loser} — " +
                "remove one of them.", Path.GetFileName(winner), Path.GetFileName(path), id, Path.GetFileName(winner),
                Path.GetFileName(path));
            return false;
        }
        claimedIds[id] = path;
        return true;
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
            // A packaged game may ship server-only files (a serverAuthority module, authorityWords
            // dictionaries). The game origin never serves those, so a compressed copy is pure waste —
            // and for a deliberately secret word list, a copy that shouldn't exist at all. Read the
            // manifest we just extracted and leave them out of the seed.
            var denied = DeniedRelatives(target);

            // Entries are opened lazily and one at a time, while the archive is still open.
            precompressor.SeedFromPackage(id, target, plan.Files
                .Where(f => !GameAssetPrecompressor.IsExcluded(f.LogicalPath, denied))
                .Select(f =>
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
    /// The never-served files of the extracted game — its serverAuthority module and any authorityWords
    /// dictionaries — read from the manifest we just wrote and normalized by
    /// <see cref="GameAssetPrecompressor.DeniedRelatives"/>, which is also what the reconcile pass
    /// compares against. Deliberately NOT a second implementation of that rule: a third normalization of
    /// "which files are never served" is how one of them ends up writing a compressed copy of a
    /// deliberately secret word list. An unreadable or invalid manifest yields an EMPTY set on purpose —
    /// this runs before the catalog has validated the game, and the only cost of a wrong guess is a
    /// redundant variant, which the next reconcile prunes. The authoritative gate is
    /// <see cref="Hosting.GameOriginAssetGate"/>.
    /// </summary>
    private static IReadOnlySet<string> DeniedRelatives(string gameDir)
    {
        try
        {
            var manifestPath = Path.Combine(gameDir, GamePackage.ManifestEntryName);
            if (!File.Exists(manifestPath)) return EmptyDenied;
            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), KnockBoxProtocolContext.Default.GameManifest);
            return manifest is null ? EmptyDenied : GameAssetPrecompressor.DeniedRelatives(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing to do: seed everything, and let the reconcile pass sort it out.
            return EmptyDenied;
        }
    }

    private static readonly IReadOnlySet<string> EmptyDenied = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Replaces <paramref name="target"/> with <paramref name="staging"/> as close to atomically as the
    /// filesystem allows: move the live folder aside, move the new one in, then delete the old one.
    /// </summary>
    /// <remarks>
    /// The move-aside step is required, not cosmetic: <see cref="Directory.Move"/> fails when the
    /// destination exists, and on Windows deleting a directory whose files are open throws (POSIX allows
    /// unlink-while-open), so deleting the live folder first would fail whenever a request happened to be
    /// streaming an asset. A leftover aside-folder is swept on the next pass.
    ///
    /// All three renames go through <see cref="AtomicFile.MoveDirectoryWithRetry"/> for the reason that
    /// helper documents: on Windows a directory rename fails outright while any file beneath it is held
    /// open without share-delete, and both directories here are ones this process has just finished
    /// writing — the moment a real-time scanner is looking at them. Without the retry an install failed
    /// with "Access to the path ... is denied", logged "it will be retried", and then genuinely was, on
    /// the next pass — but a pass only comes from a file event or the poll, so under Docker's 10-second
    /// poll that is a ten-second-late install, and for <see cref="PackageManager"/> it is long past the
    /// bounded wait on <c>Installed</c> that holds the lifecycle gate.
    /// </remarks>
    private void SwapIntoPlace(string staging, string target)
    {
        var aside = Path.Combine(unpackedRoot, StagingDirName, $"replaced-{Guid.NewGuid():N}");
        var movedAside = false;

        if (Directory.Exists(target))
        {
            AtomicFile.MoveDirectoryWithRetry(target, aside);
            movedAside = true;
        }

        try
        {
            AtomicFile.MoveDirectoryWithRetry(staging, target);
        }
        catch when (movedAside)
        {
            // Put the previous version back rather than leaving the game missing entirely.
            try { AtomicFile.MoveDirectoryWithRetry(aside, target); } catch { /* best effort: the next pass reinstalls */ }
            throw;
        }

        if (movedAside) TryDelete(aside);
    }

    /// <summary>
    /// Uninstalls extracted games whose source package has been gone for long enough.
    /// </summary>
    /// <param name="live">
    /// The (root, file name) keys that were PRESENT this pass — not the ones that installed successfully.
    /// A package still being copied, or one quarantined as malformed, is very much still there, and its
    /// previously-extracted game must survive until the file itself is actually removed. Keyed by root as
    /// well as name so a "demo.kbg" appearing in one root does not vouch for an extracted game that came
    /// from a same-named package in the other.
    /// </param>
    /// <param name="blindRoots">
    /// Root tokens this pass could not read at all. A game installed from one of them is left alone
    /// unconditionally: "the folder is unreadable" and "every package in it was deleted" look identical
    /// from here, and only one of those should cost players their game. This is what lets an unreadable
    /// root be skipped instead of abandoning the whole pass — the healthy roots still install and
    /// uninstall normally.
    /// </param>
    private ReconcileResult Uninstall(HashSet<string> live, HashSet<string> blindRoots)
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

        var removed = false;
        var countingDown = false;

        foreach (var dir in dirs)
        {
            var name = new DirectoryInfo(dir).Name;
            if (name == StagingDirName) continue;

            var marker = PackageMarker.TryRead(dir);
            // No marker means this folder was not put here by a completed install (a crash mid-swap, or
            // something an operator dropped into a server-owned cache). Either way it is not ours to
            // keep, but it still goes through the same patience as a vanished package.
            if (marker is { } found
                && (live.Contains(LiveKey(found.Root, found.Source)) || blindRoots.Contains(found.Root)))
            {
                _absentPasses.Remove(name);
                continue;
            }
            var source = marker?.Source;

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
    private static bool IsCurrent(string target, string packagePath, string root, (long Mtime, long Length) stamp)
    {
        if (!Directory.Exists(target)) return false;
        var marker = PackageMarker.TryRead(target);
        return marker is not null
            && marker.Value.Mtime == stamp.Mtime
            && marker.Value.Length == stamp.Length
            && string.Equals(marker.Value.Root, root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(marker.Value.Source, Path.GetFileName(packagePath), StringComparison.OrdinalIgnoreCase);
    }

    // Identifies a package across roots. The separator is a character no file name may contain, so
    // "managed" + "a\0b.kbg" can never collide with "managed\0a" + "b.kbg".
    private static string LiveKey(string root, string fileName) => root + '\0' + fileName;

    private void Quarantine(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists) _quarantined[path] = (info.LastWriteTimeUtc.Ticks, info.Length);
    }

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
