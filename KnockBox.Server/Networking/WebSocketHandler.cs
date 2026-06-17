using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;

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
    ServerLimits limits,
    TimeProvider time,
    ILoggerFactory loggerFactory,
    ILogger<WebSocketHandler> logger)
{
    // Per-connection Connection instances are created with `new` (not DI), so they get their logger
    // category from this shared factory.
    private readonly ILogger _connectionLogger = loggerFactory.CreateLogger<Connection>();
    // Game-emitted log lines get their own category ("KnockBox.GameLog") so an operator can filter
    // or re-level untrusted game output independently of the server's own diagnostics.
    private readonly ILogger _gameLogger = loggerFactory.CreateLogger("KnockBox.GameLog");

    // Cap on a single game log line, so a game can't flood the sink with one enormous string. The
    // data plane's token bucket already bounds the RATE of frames; this bounds the SIZE of each.
    // Internal so the sanitization tests reference it directly rather than mirroring the value.
    internal const int MaxGameLogLength = 2000;

    public async Task HandleAsync(WebSocket socket, string gameOrigin, CancellationToken ct)
    {
        try
        {
            // The first frame must arrive within the handshake deadline — an accepted socket that
            // never speaks would otherwise hold its slot (and an IP-gate slot) indefinitely.
            IMessage? first;
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (limits.HandshakeTimeout > TimeSpan.Zero) handshake.CancelAfter(limits.HandshakeTimeout);
                try
                {
                    first = await ReceiveMessageAsync(socket, handshake.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning("Closing connection: no handshake frame within {Timeout}.", limits.HandshakeTimeout);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.ProtocolError, "Handshake timeout", CancellationToken.None);
                    return;
                }
            }

            switch (first)
            {
                // A client speaking a NEWER protocol than this server fails loudly and terminally
                // (1008 stops SDK reconnects) instead of being silently misrouted. Missing/0 means a
                // pre-versioning client and is accepted as version 1.
                case HelloMessage { Proto: > KnockBoxProtocol.Version } or AttachMessage { Proto: > KnockBoxProtocol.Version }:
                    logger.LogWarning("Rejecting client speaking a protocol newer than {Version}.", KnockBoxProtocol.Version);
                    await socket.SendAsync(
                        ConnectionManager.Serialize(new ErrorMessage(null, $"Unsupported protocol version; server speaks {KnockBoxProtocol.Version}")),
                        WebSocketMessageType.Text, endOfMessage: true, ct);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Unsupported protocol version", ct);
                    break;
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
        var displayName = NormalizeDisplayName(hello.DisplayName);
        // Control: lobby events are rare and must not be silently dropped — tear down a stuck socket.
        var connection = new Connection(playerId, displayName, socket, _connectionLogger, OutboundOverflow.CloseOnFull);

        connections.Add(connection);
        var sendLoop = connection.SendLoopAsync(ct);
        connection.Send(ConnectionManager.Serialize(
            new WelcomeMessage(playerId, tokens.IssueIdentity(playerId), gameOrigin)));
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Player {PlayerId} ({Name}) connected (control)", playerId, displayName);

        // Control traffic is rare (lobby ops); sustained spam past the burst is hostile or a bug —
        // either way, terminate. Lobby creation gets its own slower bucket (codes are a shared,
        // guessable namespace) but only rejects the op, it doesn't kill the connection.
        var inboundBucket = new TokenBucket(limits.ControlMessagesPerSecond, limits.ControlMessagesBurst, time);
        var lobbyCreateBucket = new TokenBucket(limits.LobbyCreatesPerMinute / 60.0, limits.LobbyCreatesPerMinute, time);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message is null) break; // close frame
                if (!inboundBucket.TryTake())
                {
                    await CloseRateLimitedAsync(connection, sendLoop, socket);
                    break;
                }
                SafeDispatch(connection, message, () => DispatchControl(connection, message, lobbyCreateBucket));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex, "Control socket dropped for {PlayerId}", playerId); 
        }
        catch (Exception ex) { logger.LogError(ex, "Unexpected error in control loop for {PlayerId}", playerId); }
        finally
        {
            LeaveCurrentLobby(connection);
            connections.Remove(connection);
            connection.CompleteOutbound();
            await sendLoop;
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Player {PlayerId} disconnected (control)", playerId);
        }
    }

    // Runs one message handler with a guard: an unexpected exception is logged and reported to the
    // client, but never tears down the connection's receive loop — one bad message stays contained.
    private void SafeDispatch(Connection conn, IMessage message, Action handle)
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

    // Sends a final rate_limited error, drains it, then closes 1008 (PolicyViolation) — terminal for
    // the SDKs, so a spamming client doesn't immediately reconnect and resume. The send loop must be
    // drained BEFORE the close frame (a WebSocket forbids concurrent sends); CloseOutputAsync rather
    // than CloseAsync so a hostile client that never acks the close can't pin the handler.
    private async Task CloseRateLimitedAsync(Connection conn, Task sendLoop, WebSocket socket)
    {
        logger.LogWarning("Rate limit exceeded by {PlayerId}; closing connection.", conn.PlayerId);
        conn.Send(ConnectionManager.Serialize(new ErrorMessage(null, "rate_limited")));
        conn.CompleteOutbound();
        await sendLoop;
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "rate_limited", CancellationToken.None);
        }
        catch (WebSocketException) { /* already dropped */ }
    }

    private void DispatchControl(Connection conn, IMessage message, TokenBucket lobbyCreates)
    {
        switch (message)
        {
            case SetNameMessage m:
                conn.DisplayName = NormalizeDisplayName(m.DisplayName);
                break;

            case ListGamesMessage m:
                conn.Send(ConnectionManager.Serialize(new GameListMessage(m.Cid, [.. catalog.Games])));
                break;

            case CreateLobbyMessage m:
                if (!lobbyCreates.TryTake())
                {
                    logger.LogWarning("Lobby-create rate limit hit by {PlayerId}.", conn.PlayerId);
                    conn.Send(ConnectionManager.Serialize(new ErrorMessage(m.Cid, "rate_limited")));
                    break;
                }
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
                if (logger.IsEnabled(LogLevel.Debug))
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
        if (logger.IsEnabled(LogLevel.Information))
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

        // Leave any previous lobby BEFORE joining the new one so the player is never momentarily a
        // member of two (one-lobby-at-a-time), mirroring HandleCreateLobby. Switching to a full lobby
        // is rare; the explicit join means leaving the old one is the intended outcome.
        LeaveOtherLobby(conn, lobby.Id);

        // Give the joiner a name unique within this lobby (e.g. "Bob (2)" when "Bob" is taken). The
        // rename lives only on the stored Player — conn.DisplayName is untouched, so the player keeps
        // their normal name when they leave and join another lobby.
        if (!lobby.TryAddUnique(conn.PlayerId, conn.DisplayName, out var joined) || joined is null)
        {
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(cid, "Lobby is full")));
            return;
        }

        conn.LobbyId = lobby.Id;
        conn.Send(ConnectionManager.Serialize(new JoinedMessage(cid, lobby.Id)));

        // Seed the joiner with the existing roster, then announce them to everyone else. Broadcast the
        // stored Player (the possibly-disambiguated name), not conn.DisplayName, so peers see the same
        // name the joiner's own game client gets from the GameStarting roster.
        if (!alreadyMember)
        {
            foreach (var member in lobby.Players.Where(p => p.Id != conn.PlayerId))
                conn.Send(ConnectionManager.Serialize(new PlayerJoinedMessage(lobby.Id, member)));

            Broadcast(lobby, new PlayerJoinedMessage(lobby.Id, joined), exceptPlayerId: conn.PlayerId);
            // Mid-game joins: also tell the other members' game sockets so in-game rosters update.
            BroadcastToGame(lobby, new GamePlayerJoinedMessage(joined), exceptPlayerId: conn.PlayerId);
        }

        // Launch the game for the entering player only — existing members already have it running,
        // and re-sending GameStarting would rebuild their iframe.
        SendEnterGame(conn, lobby);
    }

    // Tells one connection to load the game ("enter the game now"). Sent on create, on join, and on
    // rejoin — once per player, when they enter the lobby. The server no longer has a "started"
    // concept; this is purely "you're in, here's what to load".
    private static void SendEnterGame(Connection conn, Lobby lobby)
    {
        conn.Send(ConnectionManager.Serialize(
            new GameStartingMessage(lobby.Id, lobby.GameId, lobby.HostId, lobby.Players)));
    }

    private void HandleSetLobbyOpen(Connection conn, SetLobbyOpenMessage m)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        // Re-check live membership too: a lobby code can be reused after the original is emptied, so
        // host identity alone isn't enough — the caller must still be a member of THIS lobby instance.
        if (lobby is null || conn.PlayerId != lobby.HostId || !lobby.Contains(conn.PlayerId))
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Ignoring SetLobbyOpen from non-host {PlayerId}", conn.PlayerId);
            return;
        }
        lobby.Open = m.Open;
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Lobby {LobbyId} set {State} by host", lobby.Id, m.Open ? "open" : "closed");
    }

    // A game asked to write a diagnostic to the server log. The server doesn't trust the content, so
    // it stamps its own context (game/lobby/player), clamps the size, and ignores a meaningless level.
    private void HandleLogMessage(Connection conn, LogMessage m)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        if (lobby is null) return;

        // None means "log nothing"; an out-of-range value is a malformed/forged frame — drop both.
        if (m.Level == LogLevel.None || !Enum.IsDefined(m.Level)) return;
        if (!_gameLogger.IsEnabled(m.Level)) return; // e.g. game Trace/Debug below the sink's minimum

        _gameLogger.Log(m.Level, "Game {GameId} lobby {LobbyId} player {PlayerId}: {GameMessage}",
            lobby.GameId, conn.LobbyId, conn.PlayerId, CleanLogText(m.Message));
    }

    // The game's log message is untrusted. Clamp it to a bounded size (without splitting a surrogate
    // pair) and strip control characters — notably CR/LF — so a game can't inject extra lines into the
    // sink and forge entries that look like the server's own. Tab is left as ordinary whitespace.
    // Any unpaired surrogate (from malformed input or the size cut) is replaced so the result is
    // always valid UTF-16.
    internal static string CleanLogText(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;

        var text = message;
        if (text.Length > MaxGameLogLength)
        {
            var cut = MaxGameLogLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--; // don't slice through a surrogate pair
            text = text[..cut];
        }

        char[]? buffer = null; // allocate only if there's actually something to strip (common case: none)
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            bool strip;
            if (char.IsControl(c)) strip = c != '\t';
            else if (char.IsHighSurrogate(c)) strip = i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]);
            else if (char.IsLowSurrogate(c)) strip = i == 0 || !char.IsHighSurrogate(text[i - 1]);
            else strip = false;
            if (!strip) continue;
            buffer ??= text.ToCharArray();
            buffer[i] = ' ';
        }
        return buffer is null ? text : new string(buffer);
    }

    private void HandleKickPlayer(Connection conn, KickPlayerMessage m)
    {
        if (conn.LobbyId is null) return;
        var lobby = lobbies.Get(conn.LobbyId);
        if (lobby is null || conn.PlayerId != lobby.HostId || !lobby.Contains(conn.PlayerId))
        {
            if (logger.IsEnabled(LogLevel.Debug))
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
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Host {HostId} kicked {TargetId} from lobby {LobbyId}",
            conn.PlayerId, m.TargetPlayerId, lobby.Id);
    }

    private void HandleRequestGameTicket(Connection conn, RequestGameTicketMessage m)
    {
        // A ticket is only ever issued for the player's CURRENT lobby — enforce the one-lobby-at-a-time
        // invariant on this path rather than trusting the client-supplied id alone.
        if (m.LobbyId != conn.LobbyId)
        {
            conn.Send(ConnectionManager.Serialize(new ErrorMessage(m.Cid, "Not a member of that lobby")));
            return;
        }
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
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Player {PlayerId} attached game socket to lobby {LobbyId}", playerId, lobbyId);

        // Every relayed frame fans out O(lobby size), so inbound spam multiplies on the way out.
        // The burst absorbs legitimate spikes (a host re-syncing several joiners at once).
        var inboundBucket = new TokenBucket(limits.GameMessagesPerSecond, limits.GameMessagesBurst, time);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, ct);
                if (message is null) break;
                if (!inboundBucket.TryTake())
                {
                    await CloseRateLimitedAsync(connection, sendLoop, socket);
                    break;
                }
                SafeDispatch(connection, message, () =>
                {
                    if (message is GameMessage gm) HandleGameMessage(connection, gm);
                    else if (message is SetLobbyOpenMessage so) HandleSetLobbyOpen(connection, so);
                    else if (message is KickPlayerMessage kp) HandleKickPlayer(connection, kp);
                    else if (message is LogMessage log) HandleLogMessage(connection, log);
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(ex, "Game socket dropped for {PlayerId}", playerId); 
        }
        catch (Exception ex) { logger.LogError(ex, "Unexpected error in data loop for {PlayerId}", playerId); }
        finally
        {
            connections.RemoveGame(connection);
            connection.CompleteOutbound();
            await sendLoop;
            if (logger.IsEnabled(LogLevel.Information))
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
    private void Broadcast(Lobby lobby, IMessage message, string? exceptPlayerId = null)
    {
        var bytes = ConnectionManager.Serialize(message);
        foreach (var p in lobby.Players)
            if (p.Id != exceptPlayerId)
                connections.SendRawTo(p.Id, bytes);
    }

    private void BroadcastToGame(Lobby lobby, IMessage message, string? exceptPlayerId = null)
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
            if (logger.IsEnabled(LogLevel.Information))
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

    // Display names are echoed into every roster/join broadcast, so a giant one lets a single client
    // amplify O(lobby size). Trim, fall back to "Player" when blank, and clamp the length.
    private const int MaxDisplayNameLength = 64;

    private static string NormalizeDisplayName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Player";
        return trimmed.Length <= MaxDisplayNameLength ? trimmed : trimmed[..MaxDisplayNameLength];
    }

    // A single relayed frame should never approach this; the cap stops a malicious/buggy client from
    // growing the reassembly buffer without bound (memory-pressure DoS).
    private const int MaxMessageBytes = 512 * 1024;

    /// <summary>Reassembles one full text message across frames; returns null on a close frame.
    /// Closes the socket and returns null if the message exceeds <see cref="MaxMessageBytes"/>.</summary>
    private async Task<IMessage?> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        // Rent the per-frame read buffer instead of allocating it on every inbound message (hot path).
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            // Fast path: the whole message arrived in one frame (the common case for control/game
            // frames). Deserialize straight from the rented buffer — no MemoryStream, no ToArray copy.
            if (result.EndOfMessage)
                return result.Count == 0 ? null : Deserialize(buffer.AsSpan(0, result.Count));

            // Slow path: a multi-frame / >4 KB message. Accumulate, then deserialize from the stream's
            // own buffer (it's exposable — parameterless ctor) instead of copying it out with ToArray.
            using var ms = new MemoryStream();
            ms.Write(buffer, 0, result.Count);
            while (!result.EndOfMessage)
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

            if (ms.Length == 0) return null;
            var seg = ms.TryGetBuffer(out var b) ? b : new ArraySegment<byte>(ms.ToArray());
            return Deserialize(seg.AsSpan());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        IMessage? Deserialize(ReadOnlySpan<byte> utf8)
        {
            try
            {
                return JsonSerializer.Deserialize(utf8, KnockBoxProtocolContext.Default.IMessage);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Discarding malformed message");
                return new ErrorMessage(null, "Malformed message"); // dispatched → ignored
            }
        }
    }
}
