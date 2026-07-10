using System.Collections.Concurrent;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// Owns the per-lobby authority actors (design §6). Deliberately depends only on
/// <see cref="ConnectionManager"/> and <see cref="LobbyManager"/> — never on WebSocketHandler — so
/// the fatal-teardown path has everything it needs with no dependency cycle.
/// </summary>
public sealed class ServerAuthorityManager(
    string gamesRoot,
    AuthorityOptions options,
    ConnectionManager connections,
    LobbyManager lobbies,
    TimeProvider time,
    bool isDevelopment,
    ILoggerFactory loggerFactory)
{
    private readonly ConcurrentDictionary<string, ServerAuthority> _actors = new(StringComparer.OrdinalIgnoreCase);
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
        var gameDir = Path.GetFullPath(Path.Combine(gamesRoot, manifest.Id));
        var modulePath = Path.GetFullPath(Path.Combine(gameDir, manifest.ServerAuthority));
        var dirPrefix = gameDir.EndsWith(Path.DirectorySeparatorChar) ? gameDir : gameDir + Path.DirectorySeparatorChar;
        if (!modulePath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(modulePath))
        {
            error = "The game's server-authority module is missing.";
            return false;
        }

        var runtime = new JsAuthorityRuntime(modulePath, options, time);
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

    public bool TryGet(string lobbyId, out ServerAuthority authority) => _actors.TryGetValue(lobbyId, out authority!);

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
        _actors.TryRemove(lobby.Id, out _);
        lobbies.Remove(lobby.Id);

        var closed = ConnectionManager.Serialize(new LobbyClosedMessage(lobby.Id, reason));
        foreach (var p in lobby.Players)
        {
            connections.SendRawTo(p.Id, closed);       // control plane: the shell shows the reason
            connections.GetGame(p.Id)?.Abort();        // the game itself is dead — cut its socket
        }
        actor.Stop();

        _logger.LogError("Lobby {LobbyId} (game {GameId}) closed: {Reason}", lobby.Id, lobby.GameId, reason);
    }
}
