using System.Net.WebSockets;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;

namespace KnockBox.Server.Networking;

/// <summary>
/// Owns a single client's WebSocket lifecycle: identity handshake, message routing, and
/// host-authoritative relay. The server never inspects relayed game payloads — it only routes them.
/// </summary>
public sealed class WebSocketHandler(
    ConnectionManager connections,
    LobbyManager lobbies,
    GameCatalog catalog,
    ILogger<WebSocketHandler> logger)
{
    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        // ── Identity handshake: first frame must be Hello ──
        var first = await ReceiveMessageAsync(socket, ct);
        if (first is not HelloMessage hello)
        {
            await socket.CloseAsync(WebSocketCloseStatus.ProtocolError, "Expected Hello", ct);
            return;
        }

        var playerId = string.IsNullOrWhiteSpace(hello.PlayerId) ? Guid.NewGuid().ToString("N") : hello.PlayerId;
        var displayName = string.IsNullOrWhiteSpace(hello.DisplayName) ? "Player" : hello.DisplayName;
        var connection = new Connection(playerId, displayName, socket);

        connections.Add(connection);
        var sendLoop = connection.SendLoopAsync(ct);
        connection.Send(ConnectionManager.Serialize(new WelcomeMessage(playerId)));
        logger.LogInformation("Player {PlayerId} ({Name}) connected", playerId, displayName);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message is null) break; // close frame
                Dispatch(connection, message);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) { logger.LogDebug(ex, "Socket dropped for {PlayerId}", playerId); }
        finally
        {
            LeaveCurrentLobby(connection);
            connections.Remove(connection);
            connection.CompleteOutbound();
            await sendLoop;
            logger.LogInformation("Player {PlayerId} disconnected", playerId);
        }
    }

    private void Dispatch(Connection conn, Message message)
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

            case RelayMessage m:
                HandleRelay(conn, m);
                break;

            default:
                logger.LogDebug("Ignoring unexpected message {Type} from {PlayerId}", message.GetType().Name, conn.PlayerId);
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

            Broadcast(lobby, new PlayerJoinedMessage(lobby.Id, new Player(conn.PlayerId, conn.DisplayName)),
                exceptPlayerId: conn.PlayerId);
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

    private void HandleRelay(Connection conn, RelayMessage m)
    {
        var lobby = lobbies.Get(m.LobbyId);
        if (lobby is null || !lobby.Contains(conn.PlayerId)) return; // not a member; drop silently

        var outbound = m with { From = conn.PlayerId };
        var bytes = ConnectionManager.Serialize(outbound);

        switch (m.To)
        {
            case "all":
                foreach (var p in lobby.Players) connections.SendRawTo(p.Id, bytes);
                break;
            case "host":
                connections.SendRawTo(lobby.HostId, bytes);
                break;
            default: // a specific playerId, only if they are in this lobby
                if (lobby.Contains(m.To)) connections.SendRawTo(m.To, bytes);
                break;
        }
    }

    private void Broadcast(Lobby lobby, Message message, string? exceptPlayerId = null)
    {
        var bytes = ConnectionManager.Serialize(message);
        foreach (var p in lobby.Players)
            if (p.Id != exceptPlayerId)
                connections.SendRawTo(p.Id, bytes);
    }

    private void LeaveCurrentLobby(Connection conn)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        conn.LobbyId = null;
        if (lobby is null) return;

        if (lobby.Remove(conn.PlayerId))
            Broadcast(lobby, new PlayerLeftMessage(lobby.Id, conn.PlayerId));

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
            Broadcast(lobby, new PlayerLeftMessage(lobby.Id, conn.PlayerId));
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
