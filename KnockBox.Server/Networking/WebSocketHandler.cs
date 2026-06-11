using System.Net.WebSockets;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Security;

namespace KnockBox.Server.Networking;

/// <summary>
/// Owns a single client's WebSocket lifecycle. One <c>/ws</c> endpoint serves two roles, chosen by
/// the first frame:
/// <list type="bullet">
/// <item><b>Control</b> (first frame <see cref="HelloMessage"/>) — the shell: identity handshake,
/// discovery, lobby ops, and issuing game tickets.</item>
/// <item><b>Data</b> (first frame <see cref="AttachMessage"/>) — a game iframe (separate origin):
/// authenticates with a scoped ticket, binds to its lobby, then exchanges opaque game messages.
/// The server resolves routing from the bound connection — the game never names a lobby.</item>
/// </list>
/// The server never inspects game payloads — it only routes them.
/// </summary>
public sealed class WebSocketHandler(
    ConnectionManager connections,
    LobbyManager lobbies,
    GameCatalog catalog,
    TokenService tokens,
    ILoggerFactory loggerFactory,
    ILogger<WebSocketHandler> logger)
{
    // Per-connection Connection instances are created with `new` (not DI), so they get their logger
    // category from this shared factory.
    private readonly ILogger _connectionLogger = loggerFactory.CreateLogger<Connection>();

    public async Task HandleAsync(WebSocket socket, string gameOrigin, CancellationToken ct)
    {
        try
        {
            var first = await ReceiveMessageAsync(socket, ct);
            switch (first)
            {
                case HelloMessage hello:
                    await RunControlAsync(socket, hello, gameOrigin, ct);
                    break;
                case AttachMessage attach:
                    await RunDataAsync(socket, attach, ct);
                    break;
                default:
                    await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Expected Hello or Attach", ct);
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "Socket dropped during handshake."); }
        catch (Exception ex) { logger.LogError(ex, "Unexpected error handling a connection."); }
    }

    // ── Control role (the shell) ──────────────────────────────────────────────
    private async Task RunControlAsync(WebSocket socket, HelloMessage hello, string gameOrigin, CancellationToken ct)
    {
        // Identity is unforgeable: a claimed id is only honoured if its signed token verifies;
        // otherwise we mint a fresh anonymous id. First-time clients arrive with no token.
        var playerId = tokens.TryVerifyIdentity(hello.Token, out var verified)
            ? verified
            : Guid.NewGuid().ToString("N");
        var displayName = string.IsNullOrWhiteSpace(hello.DisplayName) ? "Player" : hello.DisplayName;
        // Control: lobby events are rare and must not be silently dropped — tear down a stuck socket.
        var connection = new Connection(playerId, displayName, socket, _connectionLogger, OutboundOverflow.CloseOnFull);

        connections.Add(connection);
        var sendLoop = connection.SendLoopAsync(ct);
        connection.Send(ConnectionManager.Serialize(
            new WelcomeMessage(playerId, tokens.IssueIdentity(playerId), gameOrigin)));
        logger.LogInformation("Player {PlayerId} ({Name}) connected (control)", playerId, displayName);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message is null) break; // close frame
                SafeDispatch(connection, message, () => DispatchControl(connection, message));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "Control socket dropped for {PlayerId}", playerId); }
        catch (Exception ex) { logger.LogError(ex, "Unexpected error in control loop for {PlayerId}", playerId); }
        finally
        {
            LeaveCurrentLobby(connection);
            connections.Remove(connection);
            connection.CompleteOutbound();
            await sendLoop;
            logger.LogInformation("Player {PlayerId} disconnected (control)", playerId);
        }
    }

    // Runs one message handler with a guard: an unexpected exception is logged and reported to the
    // client, but never tears down the connection's receive loop — one bad message stays contained.
    private void SafeDispatch(Connection conn, Message message, Action handle)
    {
        try
        {
            handle();
        }
        catch (Exception ex)
        {
            var type = message.GetType().Name;
            logger.LogError(ex, "Unhandled error dispatching {Type} for {PlayerId}", type, conn.PlayerId);
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(null, $"Internal error handling {type}")));
        }
    }

    private void DispatchControl(Connection conn, Message message)
    {
        switch (message)
        {
            case SetNameMessage m:
                conn.DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? "Player" : m.DisplayName.Trim();
                break;

            case ListGamesMessage m:
                conn.Send(ConnectionManager.Serialize(new GameListMessage(m.Cid, catalog.Games.ToArray())));
                break;

            case CreateLobbyMessage m:
                HandleCreateLobby(conn, m);
                break;

            case JoinLobbyMessage m:
                HandleJoin(conn, m.Cid, m.LobbyId, rejoin: false);
                break;

            case RejoinMessage m:
                HandleJoin(conn, m.Cid, m.LobbyId, rejoin: true);
                break;

            case LeaveLobbyMessage:
                LeaveCurrentLobby(conn);
                break;

            case RequestGameTicketMessage m:
                HandleRequestGameTicket(conn, m);
                break;

            default:
                logger.LogDebug("Ignoring unexpected control message {Type} from {PlayerId}", message.GetType().Name, conn.PlayerId);
                break;
        }
    }

    private void HandleCreateLobby(Connection conn, CreateLobbyMessage m)
    {
        if (!catalog.TryGet(m.GameId, out var game))
        {
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(m.Cid, $"Unknown game '{m.GameId}'")));
            return;
        }

        LeaveCurrentLobby(conn); // one lobby at a time

        if (!lobbies.TryCreate(game.Id, conn.PlayerId, game.MaxPlayers, out var lobby))
        {
            logger.LogError("Failed to create a lobby with game {id} for user {playerId}.", game.Id, conn.PlayerId);
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(m.Cid, "Could not create a lobby, please try again.")));
            return;
        }

        if (!lobby.TryAdd(new Player(conn.PlayerId, conn.DisplayName)))
        {
            logger.LogError("Failed to add host {playerId} to lobby {lobbyId}.", conn.PlayerId, lobby.Id);
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(m.Cid, "Could not join the lobby you created.")));
            return;
        }

        conn.LobbyId = lobby.Id;
        conn.Send(ConnectionManager.Serialize(new LobbyCreatedMessage(m.Cid, lobby.Id)));
        logger.LogInformation("Lobby {LobbyId} created for '{GameId}' by host {HostId}", lobby.Id, game.Id, conn.PlayerId);

        // The game loads as soon as a player is in a lobby — the game itself owns "waiting for
        // players" and decides when play begins. The lobby is open by default; the host closes it
        // (SetLobbyOpen) when it should stop accepting joins.
        SendEnterGame(conn, lobby);
    }

    private void HandleJoin(Connection conn, string cid, string lobbyId, bool rejoin)
    {
        var lobby = lobbies.Get(lobbyId);
        if (lobby is null)
        {
            conn.Send(ConnectionManager.Serialize(
                rejoin ? new RejoinFailedMessage(cid) : new ErrorMessage(cid, $"Lobby '{lobbyId}' not found")));
            return;
        }

        // A kicked player is barred from this lobby. Tell a rejoin to give up (the shell clears its
        // saved lobby and returns home); give a fresh join a clear reason (not "lobby full").
        if (lobby.IsKicked(conn.PlayerId))
        {
            conn.Send(ConnectionManager.Serialize(
                rejoin ? new RejoinFailedMessage(cid) : new ErrorMessage(cid, "You were kicked from this lobby.")));
            return;
        }

        var alreadyMember = lobby.Contains(conn.PlayerId);
        // A fresh join needs the lobby to be open; an existing member rejoining (reconnect) is
        // always allowed back in, regardless of the game's join policy.
        if (!rejoin && !alreadyMember && !lobby.Open)
        {
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(cid, "Lobby is closed")));
            return;
        }

        if (!lobby.TryAdd(new Player(conn.PlayerId, conn.DisplayName)))
        {
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(cid, "Lobby is full")));
            return;
        }

        LeaveOtherLobby(conn, lobby.Id);
        conn.LobbyId = lobby.Id;
        conn.Send(ConnectionManager.Serialize(new JoinedMessage(cid, lobby.Id)));

        // Seed the joiner with the existing roster, then announce them to everyone else.
        if (!alreadyMember)
        {
            foreach (var member in lobby.Players.Where(p => p.Id != conn.PlayerId))
                conn.Send(ConnectionManager.Serialize(new PlayerJoinedMessage(lobby.Id, member)));

            var player = new Player(conn.PlayerId, conn.DisplayName);
            Broadcast(lobby, new PlayerJoinedMessage(lobby.Id, player), exceptPlayerId: conn.PlayerId);
            // Mid-game joins: also tell the other members' game sockets so in-game rosters update.
            BroadcastToGame(lobby, new GamePlayerJoinedMessage(player), exceptPlayerId: conn.PlayerId);
        }

        // Launch the game for the entering player only — existing members already have it running,
        // and re-sending GameStarting would rebuild their iframe.
        SendEnterGame(conn, lobby);
    }

    // Tells one connection to load the game ("enter the game now"). Sent on create, on join, and on
    // rejoin — once per player, when they enter the lobby. The server no longer has a "started"
    // concept; this is purely "you're in, here's what to load".
    private void SendEnterGame(Connection conn, Lobby lobby)
    {
        conn.Send(ConnectionManager.Serialize(
            new GameStartingMessage(lobby.Id, lobby.GameId, lobby.HostId, lobby.Players)));
    }

    private void HandleSetLobbyOpen(Connection conn, SetLobbyOpenMessage m)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        if (lobby is null || conn.PlayerId != lobby.HostId)
        {
            logger.LogDebug("Ignoring SetLobbyOpen from non-host {PlayerId}", conn.PlayerId);
            return;
        }
        lobby.Open = m.Open;
        logger.LogInformation("Lobby {LobbyId} set {State} by host", lobby.Id, m.Open ? "open" : "closed");
    }

    private void HandleKickPlayer(Connection conn, KickPlayerMessage m)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        if (lobby is null || conn.PlayerId != lobby.HostId)
        {
            logger.LogDebug("Ignoring KickPlayer from non-host {PlayerId}", conn.PlayerId);
            return;
        }
        if (m.TargetPlayerId == lobby.HostId) return; // the host can't kick itself
        if (!lobby.Kick(m.TargetPlayerId)) return;     // not a member (kick still recorded)

        // Announce on both planes so shells and in-game rosters drop the player.
        Broadcast(lobby, new PlayerLeftMessage(lobby.Id, m.TargetPlayerId));
        BroadcastToGame(lobby, new GamePlayerLeftMessage(m.TargetPlayerId));
        // Tell the kicked player on their CONTROL socket so the shell leaves the game and shows a
        // clear message — don't abort that socket (the push must deliver). Their game (data) socket
        // is evicted, and the kicked-set bars any rejoin.
        connections.SendTo(m.TargetPlayerId, new KickedMessage(lobby.Id));
        connections.GetGame(m.TargetPlayerId)?.Abort();
        logger.LogInformation("Host {HostId} kicked {TargetId} from lobby {LobbyId}",
            conn.PlayerId, m.TargetPlayerId, lobby.Id);
    }

    private void HandleRequestGameTicket(Connection conn, RequestGameTicketMessage m)
    {
        var lobby = lobbies.Get(m.LobbyId);
        if (lobby is null || !lobby.Contains(conn.PlayerId))
        {
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(m.Cid, "Not a member of that lobby")));
            return;
        }

        var ticket = tokens.IssueTicket(conn.PlayerId, lobby.Id, lobby.GameId);
        conn.Send(ConnectionManager.Serialize(new GameTicketMessage(m.Cid, ticket)));
    }

    // ── Data role (a game iframe's own socket) ────────────────────────────────
    private async Task RunDataAsync(WebSocket socket, AttachMessage attach, CancellationToken ct)
    {
        if (!tokens.TryVerifyTicket(attach.Ticket, out var playerId, out var lobbyId, out var gameId))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Invalid ticket", ct);
            return;
        }

        // Re-validate against live membership: a ticket only works while the player is still in the
        // lobby (so it survives reconnects but fails once they've left or the lobby is gone).
        var lobby = lobbies.Get(lobbyId);
        if (lobby is null || !lobby.Contains(playerId))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Lobby membership expired", ct);
            return;
        }

        // The ticket is scoped to the game the lobby was created for; reject if they no longer match.
        if (!string.Equals(gameId, lobby.GameId, StringComparison.OrdinalIgnoreCase))
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Ticket game mismatch", ct);
            return;
        }

        // A re-attach (game reload / reconnect) supersedes any prior game socket for this player —
        // tear the old one down so it doesn't linger draining into a dead socket.
        connections.GetGame(playerId)?.CompleteOutbound();

        var displayName = lobby.Players.FirstOrDefault(p => p.Id == playerId)?.DisplayName ?? "Player";
        // Data: host-authoritative state broadcasts — newest snapshot supersedes, so drop oldest.
        var connection = new Connection(playerId, displayName, socket, _connectionLogger, OutboundOverflow.DropOldest)
        {
            LobbyId = lobbyId,
        };

        connections.AddGame(connection);
        var sendLoop = connection.SendLoopAsync(ct);
        connection.Send(ConnectionManager.Serialize(
            new ReadyMessage(playerId, lobby.Players, IsHost: playerId == lobby.HostId)));
        logger.LogInformation("Player {PlayerId} attached game socket to lobby {LobbyId}", playerId, lobbyId);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message is null) break;
                SafeDispatch(connection, message, () =>
                {
                    if (message is GameMessage gm) HandleGameMessage(connection, gm);
                    else if (message is SetLobbyOpenMessage so) HandleSetLobbyOpen(connection, so);
                    else if (message is KickPlayerMessage kp) HandleKickPlayer(connection, kp);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "Game socket dropped for {PlayerId}", playerId); }
        catch (Exception ex) { logger.LogError(ex, "Unexpected error in data loop for {PlayerId}", playerId); }
        finally
        {
            connections.RemoveGame(connection);
            connection.CompleteOutbound();
            await sendLoop;
            logger.LogInformation("Player {PlayerId} detached game socket", playerId);
        }
    }

    private void HandleGameMessage(Connection conn, GameMessage m)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        if (lobby is null || !lobby.Contains(conn.PlayerId)) return; // not a member; drop silently

        var bytes = ConnectionManager.Serialize(m with { From = conn.PlayerId });

        switch (m.To)
        {
            case "all":
                foreach (var p in lobby.Players) connections.SendRawToGame(p.Id, bytes);
                break;
            case "host":
                connections.SendRawToGame(lobby.HostId, bytes);
                break;
            default: // a specific playerId, only if they are in this lobby
                if (lobby.Contains(m.To)) connections.SendRawToGame(m.To, bytes);
                break;
        }
    }

    // ── Shared lobby helpers ──────────────────────────────────────────────────
    private void Broadcast(Lobby lobby, Message message, string? exceptPlayerId = null)
    {
        var bytes = ConnectionManager.Serialize(message);
        foreach (var p in lobby.Players)
            if (p.Id != exceptPlayerId)
                connections.SendRawTo(p.Id, bytes);
    }

    private void BroadcastToGame(Lobby lobby, Message message, string? exceptPlayerId = null)
    {
        var bytes = ConnectionManager.Serialize(message);
        foreach (var p in lobby.Players)
            if (p.Id != exceptPlayerId)
                connections.SendRawToGame(p.Id, bytes);
    }

    private void LeaveCurrentLobby(Connection conn)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        conn.LobbyId = null;
        if (lobby is null) return;

        if (lobby.Remove(conn.PlayerId))
        {
            Broadcast(lobby, new PlayerLeftMessage(lobby.Id, conn.PlayerId));
            BroadcastToGame(lobby, new GamePlayerLeftMessage(conn.PlayerId));
        }

        if (lobby.Count == 0)
        {
            lobbies.Remove(lobby.Id);
            logger.LogInformation("Lobby {LobbyId} emptied and removed", lobby.Id);
        }
    }

    // Leave any lobby other than the one just joined (used when switching lobbies).
    private void LeaveOtherLobby(Connection conn, string keepLobbyId)
    {
        if (conn.LobbyId is null || conn.LobbyId == keepLobbyId) return;
        var prev = conn.LobbyId;
        conn.LobbyId = null;
        var lobby = lobbies.Get(prev);
        if (lobby is null) return;
        if (lobby.Remove(conn.PlayerId))
        {
            Broadcast(lobby, new PlayerLeftMessage(lobby.Id, conn.PlayerId));
            BroadcastToGame(lobby, new GamePlayerLeftMessage(conn.PlayerId));
        }
        if (lobby.Count == 0) lobbies.Remove(lobby.Id);
    }

    // A single relayed frame should never approach this; the cap stops a malicious/buggy client from
    // growing the reassembly buffer without bound (memory-pressure DoS).
    private const int MaxMessageBytes = 512 * 1024;

    /// <summary>Reassembles one full text message across frames; returns null on a close frame.
    /// Closes the socket and returns null if the message exceeds <see cref="MaxMessageBytes"/>.</summary>
    private async Task<Message?> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxMessageBytes)
            {
                logger.LogWarning("Message exceeded {Max} bytes; closing socket.", MaxMessageBytes);
                await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                return null;
            }
        }
        while (!result.EndOfMessage);

        if (ms.Length == 0) return null;
        try
        {
            return JsonSerializer.Deserialize<Message>(ms.ToArray(), ConnectionManager.SerializerOptions);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarding malformed message");
            return new ErrorMessage(null, "Malformed message"); // dispatched → ignored
        }
    }
}
