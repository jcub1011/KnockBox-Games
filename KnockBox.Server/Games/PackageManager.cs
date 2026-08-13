using System.IO.Compression;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Admin;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>Why an operation was refused before any job was started.</summary>
public enum PackageRefusal
{
    None,

    /// <summary><c>KnockBox:ManagedPackages</c> is off, or the managed root isn't writable.</summary>
    Unavailable,

    /// <summary>Another job already holds this game. The portal shows the running one instead.</summary>
    Busy,

    /// <summary>Nothing to act on: no such game, or no retained version to roll back to.</summary>
    NotFound,

    /// <summary>The game is provided by a package in the read-only games folder, which this cannot replace.</summary>
    NotManaged,

    /// <summary>The bytes are not an installable package.</summary>
    Invalid,
}

/// <summary>The answer to a start-an-operation request: a job, or why there isn't one.</summary>
public readonly record struct PackageJobStart(PackageJob? Job, PackageRefusal Refusal, string? Error)
{
    public bool Started => Job is not null;

    public static PackageJobStart Ok(PackageJob job) => new(job, PackageRefusal.None, null);
    public static PackageJobStart No(PackageRefusal refusal, string error) => new(null, refusal, error);
}

/// <summary>
/// Installs, updates, rolls back and removes <c>.kbg</c> packages in the managed root — everything the
/// admin portal does to a game's files.
/// </summary>
/// <remarks>
/// It lives in <c>Games</c>, not <c>Marketplace</c>, because its subject is the package lifecycle, which
/// works perfectly well with the marketplace switched off: an operator can upload a package and roll it
/// back on an air-gapped host. A marketplace is one optional source of bytes, injected from outside.
///
/// Everything funnels through <see cref="PlaceAsync"/>, and every path into it re-runs
/// <see cref="GamePackageReader.Read"/> — including rollback, because a file that has sat on disk for
/// months is not more trustworthy than one off the network. There is deliberately no second, weaker
/// validation anywhere.
///
/// Per-game serialization is the job registry's own <c>ActiveFor</c> rather than a semaphore: a
/// dictionary entry is inspectable, which is what lets a second click be answered with "this is already
/// happening, here it is" instead of a silent queue — and a draining job may hold its game for hours.
/// </remarks>
public sealed class PackageManager(
    ContentPaths.Resolved paths,
    GameCatalog catalog,
    GamePackageInstaller? installer,
    PackageJobRegistry jobs,
    GameLifecycleGate lifecycle,
    LobbyManager lobbies,
    LobbyCloser closer,
    GamePackageLimits limits,
    PackageManagerOptions options,
    TimeProvider clock,
    ILogger<PackageManager> logger)
{
    private readonly SemaphoreSlim _installSlots = new(options.MaxConcurrentInstalls, options.MaxConcurrentInstalls);

    /// <summary>The job feed the portal polls.</summary>
    public PackageJobRegistry Jobs => jobs;

    /// <summary>Whether portal installs are possible at all, and why not when they aren't.</summary>
    public string? InstallBlockedReason()
    {
        if (!options.Enabled)
            return "Portal installs are disabled (KnockBox:ManagedPackages=false). Packages copied into the " +
                   "games folder by hand still install.";
        if (installer is null)
            return "Package installation is disabled (KnockBox:Packages=false).";
        if (!Directory.Exists(paths.GamesManagedRoot))
            return $"The managed package folder '{paths.GamesManagedRoot}' does not exist.";
        return DirectoryWritable(paths.GamesManagedRoot)
            ? null
            : $"The managed package folder '{paths.GamesManagedRoot}' is not writable by the server " +
              "(in Docker the container runs as UID 1654).";
    }

    public bool CanInstall => InstallBlockedReason() is null;

    // ── Receiving an upload ───────────────────────────────────────────────────────────────────────

    /// <summary>A package received but not yet validated, sitting in the staging folder.</summary>
    public sealed record StagedPackage(string Path, long Bytes) : IDisposable
    {
        public void Dispose()
        {
            try { if (File.Exists(Path)) File.Delete(Path); } catch { /* best effort */ }
        }
    }

    /// <summary>Thrown when an upload exceeds <see cref="GamePackageLimits.MaxBytes"/>.</summary>
    public sealed class PackageTooLargeException(long limit)
        : Exception($"The package exceeds the {limit:N0}-byte limit (KnockBox:MaxPackageBytes).")
    {
        public long Limit { get; } = limit;
    }

    /// <summary>
    /// Streams an uploaded package into the staging folder, counting bytes as they arrive.
    /// </summary>
    /// <remarks>
    /// The cap is enforced against bytes ACTUALLY WRITTEN, never against <c>Content-Length</c> — that
    /// header is supplied by the client and a chunked request has none at all. The same discipline
    /// <see cref="GamePackageReader"/> applies to an archive's declared sizes.
    ///
    /// Staging deliberately sits on the managed root's own volume, so the eventual move into place is a
    /// rename rather than a copy across filesystems.
    /// </remarks>
    public async Task<StagedPackage> ReceiveAsync(Stream body, CancellationToken cancellationToken = default)
    {
        var staging = ManagedPackageLayout.StagingDir(paths.GamesManagedRoot);
        Directory.CreateDirectory(staging);
        var path = Path.Combine(staging, $"upload-{Guid.NewGuid():N}{GamePackage.Extension}.part");

        long total = 0;
        try
        {
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > limits.MaxBytes) throw new PackageTooLargeException(limits.MaxBytes);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            try { File.Delete(path); } catch { /* best effort */ }
            throw;
        }

        return new StagedPackage(path, total);
    }

    // ── Starting work ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a staged package and schedules it to be installed. Takes ownership of the staged file.
    /// </summary>
    /// <remarks>
    /// Validation happens INLINE — the caller is still holding the request, and a package that isn't one
    /// should be answered with an error, not with a job id that fails a second later. Only the apply,
    /// which may wait on lobbies, is handed to the background.
    /// </remarks>
    public PackageJobStart StartInstallFromFile(
        StagedPackage staged, PackageJobSource source, PackageApplyMode mode)
    {
        if (InstallBlockedReason() is { } blocked)
        {
            staged.Dispose();
            return PackageJobStart.No(PackageRefusal.Unavailable, blocked);
        }

        PackageIdentity identity;
        try
        {
            identity = Inspect(staged.Path);
        }
        catch (GamePackageException ex)
        {
            staged.Dispose();
            return PackageJobStart.No(PackageRefusal.Invalid, ex.Message);
        }

        var refusal = CheckReplaceable(identity.Id);
        if (refusal.Refusal != PackageRefusal.None)
        {
            staged.Dispose();
            return refusal;
        }

        var installed = catalog.GameLocations.GetValueOrDefault(identity.Id);
        var kind = installed is null ? PackageJobKind.Install : PackageJobKind.Update;
        var job = jobs.Create(kind, source, identity.Id, identity.Name ?? identity.Id,
            installed?.Manifest.Version, identity.Version, mode);

        Run(job, staged, identity);
        return PackageJobStart.Ok(job);
    }

    /// <summary>
    /// Schedules a download from a registered marketplace, then an install.
    /// </summary>
    /// <remarks>
    /// Unlike an upload, the bytes are fetched by US, in the background — so this returns as soon as the
    /// job exists, and download progress is reported through it. Validation is
    /// <see cref="MarketplaceClient.DownloadAsync"/>'s own: hash-check against the catalog entry, then
    /// the same <see cref="GamePackageReader.Read"/> everything else here uses.
    /// </remarks>
    public PackageJobStart StartMarketplaceInstall(
        MarketplaceClient client, MarketplacePlugin plugin, PackageApplyMode mode)
    {
        if (InstallBlockedReason() is { } blocked)
            return PackageJobStart.No(PackageRefusal.Unavailable, blocked);

        var id = plugin.Id ?? "";
        if (id.Length == 0)
            return PackageJobStart.No(PackageRefusal.Invalid, "The catalog entry has no id.");

        var refusal = CheckReplaceable(id);
        if (refusal.Refusal != PackageRefusal.None) return refusal;

        var installed = catalog.GameLocations.GetValueOrDefault(id);
        var job = jobs.Create(
            installed is null ? PackageJobKind.Install : PackageJobKind.Update,
            PackageJobSource.Marketplace, id, plugin.Name ?? id,
            installed?.Manifest.Version, plugin.Version, mode);

        RunDownload(job, client, plugin);
        return PackageJobStart.Ok(job);
    }

    private void RunDownload(PackageJob job, MarketplaceClient client, MarketplacePlugin plugin)
    {
        _ = Task.Run(async () =>
        {
            var token = jobs.TokenFor(job.JobId);
            DownloadedPackage? downloaded = null;
            StagedPackage? staged = null;
            try
            {
                await _installSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    jobs.SetStatus(job.JobId, PackageJobStatus.Downloading,
                        $"Downloading from {plugin.Source?.Repo ?? "the marketplace"}.");
                    // The catalog's declared size, so the bar is determinate from the first frame. It is
                    // only a hint — DownloadAsync enforces the real cap against bytes received.
                    if (plugin.Source?.Size is > 0 and var declared) jobs.Progress(job.JobId, 0, declared);

                    var staging = ManagedPackageLayout.StagingDir(paths.GamesManagedRoot);
                    Directory.CreateDirectory(staging);
                    downloaded = await client.DownloadAsync(plugin, staging, token).ConfigureAwait(false);

                    jobs.Progress(job.JobId, downloaded.Bytes, downloaded.Bytes);
                    jobs.SetStatus(job.JobId, PackageJobStatus.Verifying, "Verifying the package.");

                    // DownloadAsync already hash-checked and read it; Inspect re-reads the manifest so
                    // the placement path is identical to an upload's, with no second-guessing about
                    // which validation ran where.
                    var identity = Inspect(downloaded.Path);
                    if (!string.Equals(identity.Id, job.GameId, StringComparison.Ordinal))
                        throw new GamePackageException(
                            $"the downloaded package declares id '{identity.Id}', not '{job.GameId}'.");

                    // Hand the file to the staged-package lifetime so failure cleans up either way.
                    staged = new StagedPackage(downloaded.Path, downloaded.Bytes);
                    downloaded = null;

                    await ApplyAsync(job, staged, identity, consumedBackup: null, token).ConfigureAwait(false);
                }
                finally
                {
                    _installSlots.Release();
                }
            }
            catch (OperationCanceledException)
            {
                jobs.Finish(job.JobId, PackageJobStatus.Cancelled, "Cancelled.");
            }
            catch (MarketplaceException ex)
            {
                // Already operator-facing prose ("the release was modified after it was catalogued"),
                // so it is surfaced verbatim rather than wrapped.
                logger.LogWarning(ex, "Marketplace download for '{GameId}' failed.", job.GameId);
                jobs.Finish(job.JobId, PackageJobStatus.Failed, "Failed.", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Marketplace install job {JobId} for '{GameId}' failed.",
                    job.JobId, job.GameId);
                jobs.Finish(job.JobId, PackageJobStatus.Failed, "Failed.", Describe(ex));
            }
            finally
            {
                downloaded?.Dispose();
                staged?.Dispose();
                lifecycle.Leave(job.GameId);
            }
        });
    }

    /// <summary>Schedules a return to a retained earlier version.</summary>
    public PackageJobStart StartRollback(string gameId, string? version, PackageApplyMode mode)
    {
        if (InstallBlockedReason() is { } blocked)
            return PackageJobStart.No(PackageRefusal.Unavailable, blocked);

        if (!catalog.GameLocations.TryGetValue(gameId, out var installed))
            return PackageJobStart.No(PackageRefusal.NotFound, $"No installed game with id '{gameId}'.");

        var id = installed.Manifest.Id;
        var refusal = CheckReplaceable(id);
        if (refusal.Refusal != PackageRefusal.None) return refusal;

        var backups = Backups(id);
        var target = version is null
            ? backups.FirstOrDefault()
            : backups.FirstOrDefault(b => string.Equals(b.Version, version, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return PackageJobStart.No(PackageRefusal.NotFound,
                version is null
                    ? $"'{id}' has no retained earlier version to roll back to. Retention is " +
                      $"KnockBox:PackageBackupCount ({options.BackupCount})."
                    : $"'{id}' has no retained version '{version}'.");

        // Copied out of the backups folder, not moved: if validation or the swap fails, the retained
        // version must still be there to try again.
        var staging = ManagedPackageLayout.StagingDir(paths.GamesManagedRoot);
        StagedPackage staged;
        try
        {
            Directory.CreateDirectory(staging);
            var copy = Path.Combine(staging, $"rollback-{Guid.NewGuid():N}{GamePackage.Extension}.part");
            File.Copy(target.Path, copy);
            staged = new StagedPackage(copy, new FileInfo(copy).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PackageJobStart.No(PackageRefusal.Unavailable,
                $"Could not stage the retained version ({ex.Message}).");
        }

        PackageIdentity identity;
        try
        {
            // The retained file goes through the SAME reader as a fresh download. Age is not trust.
            identity = Inspect(staged.Path);
            if (!string.Equals(identity.Id, id, StringComparison.Ordinal))
                throw new GamePackageException(
                    $"the retained package declares id '{identity.Id}', not '{id}'.");
        }
        catch (GamePackageException ex)
        {
            staged.Dispose();
            return PackageJobStart.No(PackageRefusal.Invalid,
                $"The retained version of '{id}' can no longer be installed: {ex.Message}");
        }

        var job = jobs.Create(PackageJobKind.Rollback, PackageJobSource.Backup, id,
            installed.Manifest.Name, installed.Manifest.Version, identity.Version, mode);

        // The consumed backup is removed only after a successful swap — see Run.
        Run(job, staged, identity, consumedBackup: target.Path);
        return PackageJobStart.Ok(job);
    }

    /// <summary>Whether this game's files are ours to replace, and whether anything else is doing so.</summary>
    private PackageJobStart CheckReplaceable(string gameId)
    {
        if (jobs.ActiveFor(gameId) is { } running)
            return PackageJobStart.No(PackageRefusal.Busy,
                $"'{gameId}' already has a {running.Kind.ToString().ToLowerInvariant()} in progress " +
                $"({running.Phase}).");

        // A package sitting in the read-only games folder wins the id (GameCatalog scans it first), so
        // writing a managed copy would install a game nobody would ever be served. Refuse here rather
        // than after a download, and name the fix.
        var existing = GamePackageLocations.Find(paths, gameId);
        if (existing is { Managed: false })
            return PackageJobStart.No(PackageRefusal.NotManaged,
                $"'{gameId}' is provided by '{existing.Value.Path}' in the games folder, which takes " +
                "precedence over anything installed here. Remove it there first.");

        return default;
    }

    // ── Doing the work ────────────────────────────────────────────────────────────────────────────

    private void Run(PackageJob job, StagedPackage staged, PackageIdentity identity, string? consumedBackup = null)
    {
        _ = Task.Run(async () =>
        {
            var token = jobs.TokenFor(job.JobId);
            try
            {
                await _installSlots.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await ApplyAsync(job, staged, identity, consumedBackup, token).ConfigureAwait(false);
                }
                finally
                {
                    _installSlots.Release();
                }
            }
            catch (OperationCanceledException)
            {
                jobs.Finish(job.JobId, PackageJobStatus.Cancelled, "Cancelled.");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Package job {JobId} for '{GameId}' failed.", job.JobId, job.GameId);
                jobs.Finish(job.JobId, PackageJobStatus.Failed, "Failed.", Describe(ex));
            }
            finally
            {
                staged.Dispose();
                lifecycle.Leave(job.GameId);
            }
        });
    }

    private async Task ApplyAsync(
        PackageJob job, StagedPackage staged, PackageIdentity identity, string? consumedBackup,
        CancellationToken cancellationToken)
    {
        if (!await WaitForLobbiesAsync(job, cancellationToken).ConfigureAwait(false)) return;

        lifecycle.Enter(job.GameId, GameLifecycle.Updating);
        jobs.SetStatus(job.JobId, PackageJobStatus.Applying, "Installing files.");

        var warning = Place(job.GameId, staged.Path, identity, consumedBackup);

        jobs.Finish(job.JobId, PackageJobStatus.Succeeded,
            job.Kind switch
            {
                PackageJobKind.Rollback => $"Rolled back to {identity.Version ?? "the retained version"}.",
                PackageJobKind.Update => $"Updated to {identity.Version ?? "the new version"}.",
                _ => "Installed.",
            },
            warning: warning);
    }

    /// <summary>
    /// Honours the apply mode. Returns false when the job has been resolved without applying.
    /// </summary>
    /// <remarks>
    /// The three modes differ only in what they do about lobbies that are running right now:
    /// <list type="bullet">
    /// <item><b>Auto</b> — apply when there are none, otherwise leave the game alone entirely. Never
    /// interrupts and never blocks a player.</item>
    /// <item><b>Drain</b> — gate the game so no new lobby can start, then wait for the running ones to
    /// end on their own. Cancellable throughout.</item>
    /// <item><b>Force</b> — gate first, THEN close: gating second would leave a window in which a player
    /// reconnecting could start a lobby into a directory about to be swapped.</item>
    /// </list>
    /// </remarks>
    private async Task<bool> WaitForLobbiesAsync(PackageJob job, CancellationToken cancellationToken)
    {
        var running = LobbyCount(job.GameId);
        if (running == 0) return true;

        switch (job.Mode)
        {
            case PackageApplyMode.Auto:
                jobs.Finish(job.JobId, PackageJobStatus.Cancelled,
                    $"Deferred: {running} lobby/lobbies are still running this game.");
                return false;

            case PackageApplyMode.Force:
                lifecycle.Enter(job.GameId, GameLifecycle.Updating);
                jobs.SetStatus(job.JobId, PackageJobStatus.Applying, $"Closing {running} lobby/lobbies.");
                closer.CloseForGame(job.GameId, "This game is being updated — please start a new game in a moment.");
                return true;

            default:
                lifecycle.Enter(job.GameId, GameLifecycle.Draining);
                jobs.Mutate(job.JobId, j => j with
                {
                    Status = PackageJobStatus.WaitingForLobbies,
                    Phase = $"Waiting for {running} lobby/lobbies to finish.",
                    LobbiesWaiting = running,
                });

                // Polled rather than event-driven on purpose. A hook on lobby removal would be one more
                // thing that has to be wired correctly for a game to ever become launchable again, and a
                // missed edge would strand it forever; a poll cannot miss an edge. Lobbies last minutes,
                // so a second is a rounding error against the wait itself.
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), clock, cancellationToken).ConfigureAwait(false);
                    running = LobbyCount(job.GameId);
                    if (running == 0) return true;

                    jobs.Mutate(job.JobId, j => j.LobbiesWaiting == running
                        ? j
                        : j with
                        {
                            Phase = $"Waiting for {running} lobby/lobbies to finish.",
                            LobbiesWaiting = running,
                        });
                }
        }
    }

    private int LobbyCount(string gameId) =>
        lobbies.Snapshot().Count(l => string.Equals(l.GameId, gameId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The one place bytes become the live package for a game. Returns a warning, or null.
    /// </summary>
    /// <remarks>
    /// The order matters more than it looks:
    /// <list type="number">
    /// <item>The current package is COPIED to the backups folder, not moved. A move would leave the id
    /// with no package for the duration, and a reconcile pass landing in that window starts the two-pass
    /// uninstall countdown on a perfectly healthy game.</item>
    /// <item>The staged file is moved into place with <c>overwrite: true</c> — one atomic rename, so
    /// there is no instant at which the id has no package, not even across a crash.</item>
    /// <item>The modification time is stamped forward. <see cref="File.Move(string, string, bool)"/>
    /// preserves it, and a rollback restores a file whose original stamp could match what the extracted
    /// folder's marker already records — making the installer conclude it is already current and skip
    /// the very swap that was asked for.</item>
    /// <item><see cref="GamePackageInstaller.Adopt"/> then vouches for the file, so the extraction
    /// happens on the next pass rather than the one after it.</item>
    /// </list>
    /// </remarks>
    private string? Place(string gameId, string stagedPath, PackageIdentity identity, string? consumedBackup)
    {
        var target = ManagedPackageLayout.PackagePath(paths.GamesManagedRoot, gameId);
        string? warning = null;

        if (options.BackupCount > 0 && File.Exists(target))
        {
            try
            {
                var previous = InstalledVersion(gameId);
                Directory.CreateDirectory(ManagedPackageLayout.BackupDir(paths.GamesManagedRoot, gameId));
                File.Copy(target, BackupPath(gameId, previous), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the backup costs the ability to roll back; refusing the update over it would be
                // a worse trade, so say so and carry on.
                logger.LogWarning(ex, "Could not retain the previous package for '{GameId}'.", gameId);
                warning = $"The previous version could not be retained ({ex.Message}), so this update can't be rolled back.";
            }
        }

        // Captured before the overwrite, so the stamp below can be forced past it.
        DateTime? previousStamp = null;
        try { if (File.Exists(target)) previousStamp = File.GetLastWriteTimeUtc(target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stamp from the clock */ }

        File.Move(stagedPath, target, overwrite: true);
        try
        {
            // Strictly LATER than whatever was there before, not merely "now". The installer keys
            // freshness on (mtime, length) — deliberately, to avoid re-hashing hundreds of megabytes
            // every pass — so two versions of the same game that happen to be the same length and land
            // inside one filesystem timestamp tick would otherwise look identical to it, and the second
            // one would never be extracted. A rollback is the likeliest way to hit that: it restores
            // bytes whose original stamp is already recorded in the extracted folder's marker.
            var now = clock.GetUtcNow().UtcDateTime;
            File.SetLastWriteTimeUtc(target,
                previousStamp is { } previous && now <= previous ? previous.AddTicks(1) : now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not stamp {Path}; the installer will compare the original time.", target);
        }

        // Consumed only now: until the swap succeeded, that file was still the only copy of this version.
        if (consumedBackup is not null)
        {
            try { File.Delete(consumedBackup); } catch { /* best effort: prune catches it */ }
        }

        installer?.Adopt(target);
        catalog.ScheduleRescan();
        PruneBackups(gameId);

        logger.LogInformation("Installed package for '{Id}' ({Version}) into the managed root.",
            gameId, identity.Version ?? "no version");
        return warning;
    }

    // ── Backups ───────────────────────────────────────────────────────────────────────────────────

    /// <param name="Version">The version this file holds, or null when the manifest declared none.</param>
    public sealed record RetainedPackage(string Path, string? Version, long Bytes, DateTimeOffset RetainedAt);

    /// <summary>Retained earlier versions of a game's package, newest first.</summary>
    public IReadOnlyList<RetainedPackage> Backups(string gameId)
    {
        var dir = ManagedPackageLayout.BackupDir(paths.GamesManagedRoot, gameId);
        if (!Directory.Exists(dir)) return [];

        var found = new List<RetainedPackage>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, GamePackage.SearchPattern))
            {
                // Everything needed is in the name — "<ticks>-<version>-<sha12>.kbg" — so there is no
                // sidecar index that could disagree with the bytes, the same reasoning that puts the
                // install marker inside the extracted folder.
                var name = Path.GetFileNameWithoutExtension(path);
                var parts = name.Split('-');
                if (parts.Length < 3 || !long.TryParse(parts[0], out var ticks)) continue;
                var version = string.Join('-', parts[1..^1]);
                try
                {
                    found.Add(new RetainedPackage(
                        path,
                        version == NoVersion ? null : version,
                        new FileInfo(path).Length,
                        new DateTimeOffset(ticks, TimeSpan.Zero)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
                {
                    // Skip an unreadable or nonsense entry rather than failing the whole listing.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not list retained packages for '{GameId}'.", gameId);
            return [];
        }

        found.Sort((a, b) => b.RetainedAt.CompareTo(a.RetainedAt));
        return found;
    }

    private const string NoVersion = "noversion";

    private string BackupPath(string gameId, string? version)
    {
        // Sanitized because it lands in a file name and comes from a manifest inside an untrusted
        // archive. Anything outside the allowed set is discarded wholesale rather than escaped.
        var label = version is null || version.Length is 0 or > 32
                    || version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '+' or '_'))
            ? NoVersion
            : version;
        var stamp = clock.GetUtcNow().UtcTicks;
        var unique = Guid.NewGuid().ToString("N")[..12];
        return Path.Combine(ManagedPackageLayout.BackupDir(paths.GamesManagedRoot, gameId),
            $"{stamp}-{label}-{unique}{GamePackage.Extension}");
    }

    private void PruneBackups(string gameId)
    {
        var retained = Backups(gameId);
        if (retained.Count <= options.BackupCount) return;

        foreach (var stale in retained.Skip(options.BackupCount))
        {
            try { File.Delete(stale.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not prune the retained package {Path}.", stale.Path);
            }
        }
    }

    // ── Reading a package ─────────────────────────────────────────────────────────────────────────

    /// <param name="Name">The manifest's display name, for a job the operator can recognise.</param>
    public readonly record struct PackageIdentity(string Id, string? Name, string? Version);

    /// <summary>
    /// Validates a package file and reads its identity — the same sequence
    /// <c>MarketplaceClient.ValidatePackage</c> runs, never a second weaker copy.
    /// </summary>
    private PackageIdentity Inspect(string path)
    {
        using var archive = OpenArchive(path);
        var plan = GamePackageReader.Read(archive, limits);
        var manifestBytes = GamePackageReader.ReadManifestBytes(plan);

        GameManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, KnockBoxProtocolContext.Default.GameManifest);
        }
        catch (JsonException ex)
        {
            throw new GamePackageException($"its {GamePackage.ManifestEntryName} is not valid JSON ({ex.Message}).");
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
            throw new GamePackageException($"its {GamePackage.ManifestEntryName} declares no id.");
        if (!string.Equals(manifest.Id, plan.Id, StringComparison.Ordinal))
            throw new GamePackageException(
                $"its {GamePackage.ManifestEntryName} declares id '{manifest.Id}' but the package header says " +
                $"'{plan.Id}'.");

        return new PackageIdentity(plan.Id, manifest.Name, manifest.Version);
    }

    private static ZipArchive OpenArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            // The overwhelmingly likely upload mistake: a plain folder ZIP rather than a .kbg. The
            // message below is the one an operator can act on.
            throw new GamePackageException($"it is not a readable ZIP archive ({ex.Message}).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GamePackageException($"it could not be read ({ex.Message}).");
        }
    }

    private string? InstalledVersion(string gameId) =>
        catalog.GameLocations.TryGetValue(gameId, out var location) ? location.Manifest.Version : null;

    private static string Describe(Exception ex) => ex switch
    {
        GamePackageException or PackageTooLargeException => ex.Message,
        IOException or UnauthorizedAccessException => ex.Message,
        // Never a stack trace: this string is shown to an operator in the portal.
        _ => "An unexpected error occurred; see the server log.",
    };

    private static bool DirectoryWritable(string directory)
    {
        var probe = Path.Combine(directory, $".kb-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Clears staging leftovers from an interrupted upload or download.</summary>
    public void SweepStaging()
    {
        var staging = ManagedPackageLayout.StagingDir(paths.GamesManagedRoot);
        if (!Directory.Exists(staging)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(staging))
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not sweep the package staging folder {Dir}.", staging);
        }
    }
}
