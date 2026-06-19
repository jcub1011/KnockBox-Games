using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnockBox.Contracts;

/// <summary>
/// Base of every WebSocket envelope. Serialized polymorphically with a <c>"type"</c>
/// discriminator (e.g. <c>{ "type": "Hello", ... }</c>). The server (de)serializes with a
/// camelCase naming policy, so C# <c>PlayerId</c> ⇄ JSON <c>playerId</c>.
///
/// Request/response ops carry a client-generated <c>cid</c> so the client can await the
/// matching reply. Push events have no <c>cid</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HelloMessage), "Hello")]
[JsonDerivedType(typeof(WelcomeMessage), "Welcome")]
[JsonDerivedType(typeof(SetNameMessage), "SetName")]
[JsonDerivedType(typeof(ListGamesMessage), "ListGames")]
[JsonDerivedType(typeof(GameListMessage), "GameList")]
[JsonDerivedType(typeof(CreateLobbyMessage), "CreateLobby")]
[JsonDerivedType(typeof(LobbyCreatedMessage), "LobbyCreated")]
[JsonDerivedType(typeof(JoinLobbyMessage), "JoinLobby")]
[JsonDerivedType(typeof(JoinedMessage), "Joined")]
[JsonDerivedType(typeof(LeaveLobbyMessage), "LeaveLobby")]
[JsonDerivedType(typeof(RejoinMessage), "Rejoin")]
[JsonDerivedType(typeof(RejoinFailedMessage), "RejoinFailed")]
[JsonDerivedType(typeof(RequestGameTicketMessage), "RequestGameTicket")]
[JsonDerivedType(typeof(GameTicketMessage), "GameTicket")]
[JsonDerivedType(typeof(PlayerJoinedMessage), "PlayerJoined")]
[JsonDerivedType(typeof(PlayerLeftMessage), "PlayerLeft")]
[JsonDerivedType(typeof(PlayerDisconnectedMessage), "PlayerDisconnected")]
[JsonDerivedType(typeof(PlayerConnectedMessage), "PlayerConnected")]
[JsonDerivedType(typeof(KickedMessage), "Kicked")]
[JsonDerivedType(typeof(GameStartingMessage), "GameStarting")]
[JsonDerivedType(typeof(AttachMessage), "Attach")]
[JsonDerivedType(typeof(ReadyMessage), "Ready")]
[JsonDerivedType(typeof(GameMessage), "Game")]
[JsonDerivedType(typeof(SetLobbyOpenMessage), "SetLobbyOpen")]
[JsonDerivedType(typeof(KickPlayerMessage), "KickPlayer")]
[JsonDerivedType(typeof(GamePlayerJoinedMessage), "GamePlayerJoined")]
[JsonDerivedType(typeof(GamePlayerLeftMessage), "GamePlayerLeft")]
[JsonDerivedType(typeof(GamePlayerDisconnectedMessage), "GamePlayerDisconnected")]
[JsonDerivedType(typeof(GamePlayerConnectedMessage), "GamePlayerConnected")]
[JsonDerivedType(typeof(LogMessage), "Log")]
[JsonDerivedType(typeof(GameLogMessage), "GameLog")]
[JsonDerivedType(typeof(ErrorMessage), "Error")]
public interface IMessage;

// ── Identity (first exchange after connect on the CONTROL role) ──────────────
// The signed Token makes the anonymous, per-tab playerId unforgeable: the client resends it on
// reconnect and the server only honours a claimed PlayerId whose Token verifies. The token never
// leaves the shell origin — games authenticate with a scoped ticket instead (see RequestGameTicket).
// Proto declares the wire-protocol version the client speaks (see KnockBoxProtocol); 0 means a
// pre-versioning client and is treated as version 1.
public sealed record HelloMessage(string? PlayerId, string DisplayName, string? Token = null, int Proto = 0) : IMessage;
// GameOrigin is the separate origin (scheme://host:gamesPort) the shell uses to embed game iframes
// and that a game's data socket connects back to. The server derives it from the request, so a
// manager changing the games port needs no client edits.
public sealed record WelcomeMessage(string PlayerId, string Token, string GameOrigin,
    int Proto = KnockBoxProtocol.Version) : IMessage;

// The display name is bound at Hello time; the player can rename themselves later (e.g. after typing
// a name on the home page) by sending this on the control socket — no reconnect needed. It updates
// the connection's name used for subsequent CreateLobby/JoinLobby.
public sealed record SetNameMessage(string DisplayName) : IMessage;

// ── Catalog (over WebSocket) ─────────────────────────────────────────────────
public sealed record ListGamesMessage(string Cid) : IMessage;
public sealed record GameListMessage(string Cid, IReadOnlyList<GameManifest> Games) : IMessage;

// ── Lobby ops (cid-correlated request/response) ──────────────────────────────
public sealed record CreateLobbyMessage(string Cid, string GameId) : IMessage;
public sealed record LobbyCreatedMessage(string Cid, string LobbyId) : IMessage;

public sealed record JoinLobbyMessage(string Cid, string LobbyId) : IMessage;
public sealed record JoinedMessage(string Cid, string LobbyId) : IMessage;

public sealed record LeaveLobbyMessage(string LobbyId) : IMessage;

public sealed record RejoinMessage(string Cid, string LobbyId) : IMessage;
public sealed record RejoinFailedMessage(string Cid) : IMessage;

// ── Game ticket (control role) ───────────────────────────────────────────────
// When a game starts, the shell asks for a lobby-scoped ticket bound to (its player, this lobby).
// It hands the ticket to the game iframe (served from the game origin), and the game's client
// library opens its OWN data-role websocket and authenticates with it — without ever seeing the
// player's identity token. The ticket is reusable while the holder stays a lobby member (so the
// data socket can reconnect) and until it expires; the server re-checks live membership on attach.
public sealed record RequestGameTicketMessage(string Cid, string LobbyId) : IMessage;
public sealed record GameTicketMessage(string Cid, string Ticket) : IMessage;

// ── Lobby push events (server → client, no cid) ──────────────────────────────
public sealed record PlayerJoinedMessage(string LobbyId, Player Player) : IMessage;
public sealed record PlayerLeftMessage(string LobbyId, string PlayerId) : IMessage;
// Pushed when a member's shell (control) socket drops but they're kept in the lobby for the
// reconnect grace window (see KnockBox:DisconnectGraceSeconds) — and again when they reconnect
// within it. The player stays in the roster the whole time; these only signal the transient state
// so peers can show "reconnecting…". A grace that elapses without reconnect ends in PlayerLeft.
public sealed record PlayerDisconnectedMessage(string LobbyId, string PlayerId) : IMessage;
public sealed record PlayerConnectedMessage(string LobbyId, string PlayerId) : IMessage;
// Pushed to a player's CONTROL socket when the host kicks them: the shell leaves the game and
// returns home with a clear message (distinct from a transient drop or a "lobby full" rejection).
public sealed record KickedMessage(string LobbyId) : IMessage;
public sealed record GameStartingMessage(
    string LobbyId,
    string GameId,
    string HostId,
    IReadOnlyList<Player> Players) : IMessage;

// ── Data role (game ⇄ server) ────────────────────────────────────────────────
// The game's first frame is Attach{ticket}. The server validates the ticket against live lobby
// membership, binds the connection to (playerId, lobbyId), and replies Ready. After that the game
// only sends GameMessage{to, payload} — it never names a lobby; the server resolves routing from
// the bound connection. To ∈ { "host", "all", "<playerId>" }; From is stamped on the way out.
public sealed record AttachMessage(string Ticket, int Proto = 0) : IMessage;
public sealed record ReadyMessage(string PlayerId, IReadOnlyList<Player> Players, bool IsHost,
    int Proto = KnockBoxProtocol.Version) : IMessage;
public sealed record GameMessage(string To, JsonElement Payload, string? From = null) : IMessage;
// Game → server control (data role): the host sets whether the lobby accepts new joins. The server
// owns no "started" concept — the game decides this. Open lobbies are listed/joinable; closed ones
// are hidden from the browser and reject new joins (existing members and rejoins are unaffected).
public sealed record SetLobbyOpenMessage(bool Open) : IMessage;
// Game → server control (data role): the host removes a player from the lobby. Host-only; the target
// is dropped, blocked from rejoining (a kick is permanent for this lobby), and their sockets are
// evicted. Ignored from non-hosts or when targeting the host itself.
public sealed record KickPlayerMessage(string TargetPlayerId) : IMessage;
public sealed record GamePlayerJoinedMessage(Player Player) : IMessage;
public sealed record GamePlayerLeftMessage(string PlayerId) : IMessage;
// Game-plane counterparts of PlayerDisconnected/PlayerConnected: pushed to the OTHER members' game
// sockets so in-game clients (the SDK's onPlayerDisconnected/onPlayerConnected) can react to a peer
// dropping and returning during the grace window. The player stays in the roster throughout.
public sealed record GamePlayerDisconnectedMessage(string PlayerId) : IMessage;
public sealed record GamePlayerConnectedMessage(string PlayerId) : IMessage;
// Game → server diagnostic (data role): the game emits a log line that lands in the server's log
// sink so operators can observe deployed games (the player only ever sees their own console). Level
// is the shared Microsoft.Extensions.Logging.LogLevel; it serializes as its NAME on the wire (e.g.
// "Warning") via the string-enum converter so the JS clients can send a readable level, and reads
// case-insensitively. The server stamps the game/lobby/player context — the game supplies only this.
public sealed record LogMessage(
    [property: JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))] LogLevel Level,
    string Message) : IMessage;

// Game → server (data role): the game records a "play log" entry — an arbitrary <string,string>
// bag of match metadata (e.g. placement, playerCount, score). Unlike LogMessage (which lands in the
// server's log sink), the server FORWARDS this to the same player's CONTROL socket so the shell can
// persist it in the browser and show it on the home page's Play Log. The game supplies only Metadata;
// the server stamps the trusted, unforgeable context — GameId (resolved from the lobby), Timestamp
// (server clock), and IsHost (was this player the lobby host). The shell treats a recognized set of
// metadata keys ("placement", "playerCount", …) specially and shows the rest in a details table.
public sealed record GameLogMessage(
    Dictionary<string, string> Metadata,
    string? GameId = null,
    DateTimeOffset? Timestamp = null,
    bool? IsHost = null) : IMessage;

// ── Errors / rejections ──────────────────────────────────────────────────────
public sealed record ErrorMessage(string? Cid, string Reason) : IMessage;
