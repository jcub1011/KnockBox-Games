using System.Collections.Concurrent;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// Owns the per-lobby authority actors (design §6). Deliberately depends only on
/// <see cref="ConnectionManager"/> and <see cref="LobbyCloser"/> — never on WebSocketHandler — so the
/// fatal-teardown path has everything it needs with no dependency cycle.
/// </summary>
/// <param name="gameDirectory">
/// Resolves a game id to the directory its files live in, or null when the game is unknown. A
/// delegate rather than a games root, because since the <c>.kbg</c> package format a game's folder
/// is <c>GamesRoot/&lt;id&gt;</c> OR <c>GamesUnpackedRoot/&lt;id&gt;</c> — only the catalog knows
/// which root won. Program.cs wires this to <see cref="GameCatalog.TryGetDirectory"/>.
/// </param>
public sealed class ServerAuthorityManager(
    Func<string, string?> gameDirectory,
    AuthorityOptions options,
    ConnectionManager connections,
    LobbyCloser closer,
    TimeProvider time,
    IAuthorityWordService words,
    bool isDevelopment,
    ILoggerFactory loggerFactory)
{
    private static readonly IReadOnlyDictionary<string, IWordPool> NoWords =
        new Dictionary<string, IWordPool>(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ServerAuthority> _actors = new(StringComparer.OrdinalIgnoreCase);
    // Shared across every lobby engine (this manager is a singleton): one parsed copy of each game's
    // authority module instead of a per-lobby re-parse. See AuthorityModuleCache.
    private readonly AuthorityModuleCache _modules = new();
    private readonly ILogger _logger = loggerFactory.CreateLogger<ServerAuthorityManager>();
    // The module's own kb.log output — its own category (the "KnockBox.GameLog" precedent) so an
    // operator can filter or re-level untrusted module output independently.
    private readonly ILogger _authorityLogger = loggerFactory.CreateLogger("KnockBox.Authority");

    /// <summary>
    /// Loads the lobby's authority module and starts its actor. A false return means lobby creation
    /// must fail loudly (the caller removes the just-created lobby) — never a half-alive lobby,
    /// never a silent downgrade to host mode.
    /// </summary>
    public bool TryStart(Lobby lobby, GameManifest manifest, out string? error)
    {
        if (!options.Enabled)
        {
            error = "Server-authority games are disabled on this server.";
            return false;
        }
        if (options.MaxLobbies > 0 && _actors.Count >= options.MaxLobbies)
        {
            error = "The server has reached its limit of server-authority games; try again later.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(manifest.ServerAuthority))
        {
            error = "The game does not declare a server-authority module.";
            return false;
        }

        // The catalog validated all of this at discovery; re-check cheaply because a hot-reload
        // could have swapped the folder contents between discovery and this lobby's creation.
        // The directory comes from the catalog, never from gamesRoot/<id>: a game installed from a
        // .kbg lives under the unpacked-package cache instead, and guessing the path there would
        // fail every packaged authority game.
        var resolvedDir = gameDirectory(manifest.Id);
        if (string.IsNullOrEmpty(resolvedDir))
        {
            error = "The game's server-authority module is missing.";
            return false;
        }
        var gameDir = Path.GetFullPath(resolvedDir);
        var modulePath = ModulePath(gameDir, manifest.ServerAuthority);
        var dirPrefix = gameDir.EndsWith(Path.DirectorySeparatorChar) ? gameDir : gameDir + Path.DirectorySeparatorChar;
        if (!modulePath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(modulePath))
        {
            error = "The game's server-authority module is missing.";
            return false;
        }

        // Resolve the game's declared word dictionaries into shared pools BEFORE building the runtime,
        // so the kb.words bridge closes over them directly (no per-call lookup). A missing/failed
        // dictionary fails lobby creation loudly (never a silent-half-alive lobby).
        IReadOnlyDictionary<string, IWordPool> wordPools;
        try
        {
            wordPools = LoadWordPools(manifest, gameDir, dirPrefix);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authority word data for game {GameId} failed to load; lobby {LobbyId} not created",
                manifest.Id, lobby.Id);
            error = "The game's word data failed to load.";
            return false;
        }

        var runtime = new JsAuthorityRuntime(modulePath, _modules, options, time, wordPools);
        try
        {
            runtime.Initialize(JsonSerializer.Serialize(lobby.Players, KnockBoxProtocolContext.Default.IReadOnlyListPlayer));
        }
        catch (AuthorityLoadException ex)
        {
            runtime.Dispose();
            _logger.LogError(ex, "Authority module for game {GameId} failed to load; lobby {LobbyId} not created",
                manifest.Id, lobby.Id);
            error = "The game's server logic failed to start.";
            return false;
        }

        var actor = new ServerAuthority(lobby, runtime, options, connections, time,
            _logger, _authorityLogger, relayContainedErrors: isDevelopment, onFatal: HandleFatal);
        _actors[lobby.Id] = actor;
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Server authority started for lobby {LobbyId} (game {GameId})", lobby.Id, manifest.Id);
        error = null;
        return true;
    }

    // Loads each declared authorityWords dictionary (idempotent/cached in the word service) and returns
    // the dictKey -> pool map the runtime's kb.words closes over. The catalog validated these at
    // discovery; re-check the path cheaply against a hot-reload race, exactly like the module path.
    private IReadOnlyDictionary<string, IWordPool> LoadWordPools(GameManifest manifest, string gameDir, string dirPrefix)
    {
        if (manifest.AuthorityWords is not { Count: > 0 } decls) return NoWords;

        var pools = new Dictionary<string, IWordPool>(decls.Count, StringComparer.Ordinal);
        foreach (var (key, decl) in decls)
        {
            var path = Path.GetFullPath(Path.Combine(gameDir, decl.File));
            if (!path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException($"authorityWords '{key}' file is missing.", decl.File);
            words.Load(manifest.Id, key, path, decl.CaseInsensitive);
            pools[key] = words.Get(manifest.Id, key)
                ?? throw new InvalidOperationException($"authorityWords '{key}' failed to register.");
        }
        return pools;
    }

    // Full path of a game's authority module, matching exactly what TryStart passes to the module
    // cache — so PruneModuleCache's live-path set keys identically to Get.
    private string ModulePath(string gameDir, string serverAuthority) =>
        Path.GetFullPath(Path.Combine(gameDir, serverAuthority));

    /// <summary>Keep the shared module cache in lock-step with the catalog (mirrors
    /// <see cref="Words.AuthorityWordService.Prune"/>): drop parsed modules for games no longer
    /// declared, so a removed game doesn't leak a parsed AST for the process lifetime. Wired to
    /// <c>GameCatalog.Discovered</c> in Program.cs; runs inline (trivial in-memory set work).</summary>
    public void PruneModuleCache(IReadOnlyDictionary<string, GameCatalog.GameLocation> games)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var location in games.Values)
            if (!string.IsNullOrWhiteSpace(location.Manifest.ServerAuthority))
                live.Add(ModulePath(Path.GetFullPath(location.Directory), location.Manifest.ServerAuthority));
        _modules.Prune(live);
    }

    public bool TryGet(string lobbyId, out ServerAuthority authority) => _actors.TryGetValue(lobbyId, out authority!);

    /// <summary>Number of live per-lobby authority actors (each holds one Jint engine). Exposed for
    /// the memory diagnostics log (see Program.cs) so operators can correlate footprint with
    /// concurrent server-authority lobbies.</summary>
    public int ActorCount => _actors.Count;

    /// <summary>Normal teardown (lobby closed dark / removed): stop the actor; its drain task
    /// finishes the backlog and disposes the engine.</summary>
    public void Stop(string lobbyId)
    {
        if (_actors.TryRemove(lobbyId, out var actor)) actor.Stop();
    }

    /// <summary>Server shutdown: stop every actor (the ApplicationStopping hook).</summary>
    public void StopAll()
    {
        foreach (var lobbyId in _actors.Keys) Stop(lobbyId);
    }

    // The §7 fatal path. Invoked FROM an actor's drain task, so it must never wait on that task —
    // it only removes/limits state and pushes teardown messages. The lobby is closed LIVE (members
    // may still be connected), which is what LobbyClosed exists for: shells return home with the
    // reason; game sockets are aborted. Races with CloseLobbyIfDark are benign — both removals are
    // idempotent, and the relay drops intents whose actor is gone.
    private void HandleFatal(ServerAuthority actor, string reason)
    {
        var lobby = actor.Lobby;
        // Drop our own actor first. LobbyCloser's OnClosing hook would also do this, but this class owns
        // _actors and must not depend on that hook having been wired to reach its own invariant — Stop is
        // idempotent, so the hook firing again immediately after is a no-op.
        Stop(lobby.Id);
        // The rest of the teardown lives in LobbyCloser, shared with the admin portal's manual close so
        // the two can't drift. Pass the lobby rather than its id: a racing CloseLobbyIfDark may already
        // have unregistered it, and members still connected must be told regardless.
        closer.Close(lobby, reason);
        _logger.LogError("Lobby {LobbyId} (game {GameId}) closed: {Reason}", lobby.Id, lobby.GameId, reason);
    }
}
