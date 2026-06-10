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
    ILogger<WebSocketHandler> logger)
{
    public async Task HandleAsync(WebSocket socket, string gameOrigin, CancellationToken ct)
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

    // ── Control role (the shell) ──────────────────────────────────────────────
    private async Task RunControlAsync(WebSocket socket, HelloMessage hello, string gameOrigin, CancellationToken ct)
    {
        // Identity is unforgeable: a claimed id is only honoured if its signed token verifies;
        // otherwise we mint a fresh anonymous id. First-time clients arrive with no token.
        var playerId = tokens.TryVerifyIdentity(hello.Token, out var verified)
            ? verified
            : Guid.NewGuid().ToString("N");
        var displayName = string.IsNullOrWhiteSpace(hello.DisplayName) ? "Player" : hello.DisplayName;
        var connection = new Connection(playerId, displayName, socket);

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
                DispatchControl(connection, message);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "Control socket dropped for {PlayerId}", playerId); }
        finally
        {
            LeaveCurrentLobby(connection);
            connections.Remove(connection);
            connection.CompleteOutbound();
            await sendLoop;
            logger.LogInformation("Player {PlayerId} disconnected (control)", playerId);
        }
    }

    private void DispatchControl(Connection conn, Message message)
    {
        switch (message)
        {
            case ListGamesMessage m:
                conn.Send(ConnectionManager.Serialize(new GameListMessage(m.Cid, catalog.Games.ToArray())));
                break;

            case CreateLobbyMessage m:
                HandleCreateLobby(conn, m);
                break;

            case ListLobbiesMessage m:
                HandleListLobbies(conn, m);
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
        var lobby = lobbies.Create(game.Id, hostId: conn.PlayerId, game.MinPlayers, game.MaxPlayers);
        lobby.TryAdd(new Player(conn.PlayerId, conn.DisplayName));
        conn.LobbyId = lobby.Id;

        conn.Send(ConnectionManager.Serialize(new LobbyCreatedMessage(m.Cid, lobby.Id)));
        logger.LogInformation("Lobby {LobbyId} created for '{GameId}' by host {HostId}", lobby.Id, game.Id, conn.PlayerId);
    }

    private void HandleListLobbies(Connection conn, ListLobbiesMessage m)
    {
        var summaries = lobbies.All
            .Where(l => !l.Started && l.Count < l.MaxPlayers)
            .Select(l => new LobbySummary(l.Id, l.GameId, l.Count))
            .ToArray();
        conn.Send(ConnectionManager.Serialize(new LobbyListMessage(m.Cid, summaries)));
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

        var alreadyMember = lobby.Contains(conn.PlayerId);
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

        // Start once enough players are present. Rejoin re-sends GameStarting so the client re-enters.
        if (lobby.Count >= lobby.MinPlayers && (!lobby.Started || rejoin))
        {
            lobby.Started = true;
            var starting = new GameStartingMessage(lobby.Id, lobby.GameId, lobby.HostId, lobby.Players);
            if (rejoin) conn.Send(ConnectionManager.Serialize(starting));
            else Broadcast(lobby, starting);
            logger.LogInformation("Lobby {LobbyId} starting '{GameId}' (host {HostId})", lobby.Id, lobby.GameId, lobby.HostId);
        }
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
        if (!tokens.TryVerifyTicket(attach.Ticket, out var playerId, out var lobbyId, out _))
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

        var displayName = lobby.Players.FirstOrDefault(p => p.Id == playerId)?.DisplayName ?? "Player";
        var connection = new Connection(playerId, displayName, socket) { LobbyId = lobbyId };

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
                if (message is GameMessage gm) HandleGameMessage(connection, gm);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "Game socket dropped for {PlayerId}", playerId); }
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

    /// <summary>Reassembles one full text message across frames; returns null on a close frame.</summary>
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
