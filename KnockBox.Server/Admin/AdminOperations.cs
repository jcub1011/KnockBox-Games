using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;

namespace KnockBox.Server.Admin;

/// <summary>
/// The operator actions that are more than one call: classifying a lobby as stale, purging the stale
/// ones, and deleting a game's files. Kept out of <c>AdminApi</c> so the HTTP handlers stay "parse the
/// request, call this, serialize the answer" — and so this logic is testable without a request.
/// </summary>
public sealed class AdminOperations(
    LobbyManager lobbies,
    LobbyCloser closer,
    GameCatalog catalog,
    ConnectionManager connections,
    ContentPaths.Resolved paths,
    TimeProvider clock,
    ILogger<AdminOperations> logger)
{
    /// <summary>How long a lobby must go without activity before it counts as stale.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(30);

    /// <summary>What state a lobby is in, for the portal's directory and its status filter.</summary>
    public enum LobbyState
    {
        /// <summary>Open to joins and waiting.</summary>
        Waiting,

        /// <summary>Closed to joins — the normal state once a game is under way. Deliberately NOT called
        /// "draining": the game itself closes the lobby when play begins, so this is the healthy case.</summary>
        InGame,

        /// <summary>No members at all. Should be short-lived; the reaper closes these.</summary>
        Empty,

        /// <summary>Nobody's shell is connected, or nothing has happened for the stale threshold. This is
        /// what "purge stale" collects.</summary>
        Stale,
    }

    /// <summary>Outcome of a game deletion.</summary>
    /// <param name="Removed">Paths actually deleted.</param>
    /// <param name="Blocked">
    /// The path that stopped the deletion, when nothing was changed. In production <c>games/</c> is a
    /// read-only mount, so a hand-placed game there simply cannot be deleted over HTTP.
    /// </param>
    public sealed record DeleteResult(
        bool Success,
        string? Error = null,
        int LobbiesClosed = 0,
        IReadOnlyList<string>? Removed = null,
        string? Blocked = null);

    // ── Lobby classification ──────────────────────────────────────────────────

    /// <summary>Whether any member of this lobby currently holds a live control (shell) socket.</summary>
    public bool HasConnectedMember(Lobby lobby)
    {
        foreach (var player in lobby.Players)
            if (connections.Get(player.Id) is not null) return true;
        return false;
    }

    /// <summary>Classifies a lobby for the portal's directory.</summary>
    public LobbyState Classify(Lobby lobby, DateTimeOffset now, TimeSpan staleAfter)
    {
        if (lobby.Count == 0) return LobbyState.Empty;
        // A lobby nobody is connected to is stale regardless of the clock — the reconnect grace is what
        // keeps it alive, and once that elapses the reaper closes it anyway.
        if (!HasConnectedMember(lobby)) return LobbyState.Stale;
        if (staleAfter > TimeSpan.Zero && now - lobby.LastActivityUtc >= staleAfter) return LobbyState.Stale;
        return lobby.Open ? LobbyState.Waiting : LobbyState.InGame;
    }

    /// <summary>
    /// Closes every lobby that is empty or stale. Returns how many were closed.
    /// </summary>
    public int PurgeStale(TimeSpan staleAfter, string reason)
    {
        var now = clock.GetUtcNow();
        var closed = 0;
        foreach (var lobby in lobbies.Snapshot())
        {
            var state = Classify(lobby, now, staleAfter);
            if (state is not (LobbyState.Empty or LobbyState.Stale)) continue;
            closer.Close(lobby, reason);
            closed++;
        }
        if (closed > 0) logger.LogInformation("Admin purged {Count} stale lobby/lobbies.", closed);
        return closed;
    }

    // ── Game deletion ─────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes a game from disk, closing its running lobbies first.
    /// </summary>
    /// <remarks>
    /// Every writable location is checked BEFORE anything is closed or removed, and a failure leaves the
    /// installation untouched. A partial delete is the one genuinely bad outcome here: removing the
    /// unpacked copy while leaving the source <c>.kbg</c> means <c>GamePackageInstaller</c> reinstalls the
    /// game on its next pass, so the operator watches a game they deleted come back, and the lobbies they
    /// tore down were torn down for nothing.
    /// </remarks>
    public DeleteResult DeleteGame(string gameId)
    {
        if (!catalog.GameLocations.TryGetValue(gameId, out var location))
            return new DeleteResult(false, $"No installed game with id '{gameId}'.");

        // Canonical id from the manifest, not the caller's casing — it keys the derived paths below.
        var id = location.Manifest.Id;
        var directory = Path.GetFullPath(location.Directory);

        // Only ever delete inside the roots this server owns. A game directory outside them means a
        // configuration we don't understand, and deleting it would be the wrong kind of surprise.
        if (!IsUnder(directory, paths.GamesRoot) && !IsUnder(directory, paths.GamesUnpackedRoot))
            return new DeleteResult(false,
                $"'{id}' lives at '{directory}', which is outside both the games root and the package cache; " +
                "refusing to delete it. Remove it by hand, or disable the game instead.");

        // Resolved, never derived as GamesRoot/<id>.kbg: the installer accepts any *.kbg file name and
        // takes the id from the header inside, and a portal-installed package lives in the managed root
        // entirely. Guessing the path is what let a delete remove the unpacked copy and leave the package
        // behind, so the installer put the game straight back.
        var package = GamePackageLocations.Find(paths, id);
        var compressed = Path.Combine(paths.GamesCompressedRoot, id);
        var backups = Path.Combine(paths.GamesManagedRoot, ManagedPackageLayout.BackupsDirName, id);

        var targets = new List<string>();
        if (Directory.Exists(directory)) targets.Add(directory);
        if (package is { } found && File.Exists(found.Path)) targets.Add(found.Path);
        if (Directory.Exists(compressed)) targets.Add(compressed);
        if (Directory.Exists(backups)) targets.Add(backups);
        if (targets.Count == 0)
            return new DeleteResult(false, $"Nothing to delete for '{id}' — its files are already gone.");

        // Pre-flight: deleting an entry needs write access to its PARENT directory, so that is what is
        // probed. Doing this first is what makes the operation all-or-nothing.
        foreach (var target in targets)
        {
            if (DirectoryProbe.WhyParentNotWritable(target) is not { } why) continue;
            logger.LogWarning("Refusing to delete game {GameId}: {Reason}", id, why);
            return new DeleteResult(false,
                $"'{id}' can't be deleted: {why} In production the games folder is mounted read-only, so " +
                "disable the game instead — that blocks new lobbies and hides it from players without " +
                "touching the files.",
                Blocked: target);
        }

        // Only now is anything destroyed. Lobbies go first so players get a reason rather than a game that
        // vanishes underneath them.
        var lobbiesClosed = closer.CloseForGame(id, "This game was removed by an administrator.");

        var removed = new List<string>();
        foreach (var target in targets)
        {
            try
            {
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                else File.Delete(target);
                removed.Add(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The probe passed and this still failed (a file locked by another process, a race). Report
                // what did and didn't go, and be explicit that the install is now half-removed — silence
                // here is what would let a reinstall look like a haunting.
                logger.LogError(ex, "Deleting {Path} for game {GameId} failed after the writability check passed.",
                    target, id);
                catalog.ScheduleRescan();
                return new DeleteResult(false,
                    $"'{id}' was only partly deleted: '{target}' could not be removed ({ex.Message}). " +
                    "Remove it by hand — while it is there the game may reinstall itself.",
                    lobbiesClosed, removed, target);
            }
        }

        logger.LogWarning("Admin deleted game {GameId} ({Paths}), closing {Lobbies} lobby/lobbies.",
            id, string.Join(", ", removed), lobbiesClosed);
        // Never Discover(): it has no mutual exclusion, and an older scan winning the publish could bring
        // the deleted game back until the next event.
        catalog.ScheduleRescan();
        return new DeleteResult(true, null, lobbiesClosed, removed);
    }


    // Same containment test the bootstrap overlap check uses: compare canonical paths with a trailing
    // separator, so "games2" is never treated as living under "games".
    private static bool IsUnder(string candidate, string root)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        var candidateFull = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                            + Path.DirectorySeparatorChar;
        return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }
}
