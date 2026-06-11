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
[JsonDerivedType(typeof(ListGamesMessage), "ListGames")]
[JsonDerivedType(typeof(GameListMessage), "GameList")]
[JsonDerivedType(typeof(CreateLobbyMessage), "CreateLobby")]
[JsonDerivedType(typeof(LobbyCreatedMessage), "LobbyCreated")]
[JsonDerivedType(typeof(ListLobbiesMessage), "ListLobbies")]
[JsonDerivedType(typeof(LobbyListMessage), "LobbyList")]
[JsonDerivedType(typeof(JoinLobbyMessage), "JoinLobby")]
[JsonDerivedType(typeof(JoinedMessage), "Joined")]
[JsonDerivedType(typeof(LeaveLobbyMessage), "LeaveLobby")]
[JsonDerivedType(typeof(RejoinMessage), "Rejoin")]
[JsonDerivedType(typeof(RejoinFailedMessage), "RejoinFailed")]
[JsonDerivedType(typeof(RequestGameTicketMessage), "RequestGameTicket")]
[JsonDerivedType(typeof(GameTicketMessage), "GameTicket")]
[JsonDerivedType(typeof(PlayerJoinedMessage), "PlayerJoined")]
[JsonDerivedType(typeof(PlayerLeftMessage), "PlayerLeft")]
[JsonDerivedType(typeof(GameStartingMessage), "GameStarting")]
[JsonDerivedType(typeof(AttachMessage), "Attach")]
[JsonDerivedType(typeof(ReadyMessage), "Ready")]
[JsonDerivedType(typeof(GameMessage), "Game")]
[JsonDerivedType(typeof(SetLobbyOpenMessage), "SetLobbyOpen")]
[JsonDerivedType(typeof(GamePlayerJoinedMessage), "GamePlayerJoined")]
[JsonDerivedType(typeof(GamePlayerLeftMessage), "GamePlayerLeft")]
[JsonDerivedType(typeof(ErrorMessage), "Error")]
public abstract record Message;

// ── Identity (first exchange after connect on the CONTROL role) ──────────────
// The signed Token makes the anonymous, per-tab playerId unforgeable: the client resends it on
// reconnect and the server only honours a claimed PlayerId whose Token verifies. The token never
// leaves the shell origin — games authenticate with a scoped ticket instead (see RequestGameTicket).
public sealed record HelloMessage(string? PlayerId, string DisplayName, string? Token = null) : Message;
// GameOrigin is the separate origin (scheme://host:gamesPort) the shell uses to embed game iframes
// and that a game's data socket connects back to. The server derives it from the request, so a
// manager changing the games port needs no client edits.
public sealed record WelcomeMessage(string PlayerId, string Token, string GameOrigin) : Message;

// ── Catalog (over WebSocket) ─────────────────────────────────────────────────
public sealed record ListGamesMessage(string Cid) : Message;
public sealed record GameListMessage(string Cid, IReadOnlyList<GameManifest> Games) : Message;

// ── Lobby ops (cid-correlated request/response) ──────────────────────────────
public sealed record CreateLobbyMessage(string Cid, string GameId) : Message;
public sealed record LobbyCreatedMessage(string Cid, string LobbyId) : Message;

public sealed record ListLobbiesMessage(string Cid) : Message;
public sealed record LobbySummary(string LobbyId, string GameId, int Players);
public sealed record LobbyListMessage(string Cid, IReadOnlyList<LobbySummary> Lobbies) : Message;

public sealed record JoinLobbyMessage(string Cid, string LobbyId) : Message;
public sealed record JoinedMessage(string Cid, string LobbyId) : Message;

public sealed record LeaveLobbyMessage(string LobbyId) : Message;

public sealed record RejoinMessage(string Cid, string LobbyId) : Message;
public sealed record RejoinFailedMessage(string Cid) : Message;

// ── Game ticket (control role) ───────────────────────────────────────────────
// When a game starts, the shell asks for a lobby-scoped ticket bound to (its player, this lobby).
// It hands the ticket to the game iframe (served from the game origin), and the game's client
// library opens its OWN data-role websocket and authenticates with it — without ever seeing the
// player's identity token. The ticket is reusable while the holder stays a lobby member (so the
// data socket can reconnect) and until it expires; the server re-checks live membership on attach.
public sealed record RequestGameTicketMessage(string Cid, string LobbyId) : Message;
public sealed record GameTicketMessage(string Cid, string Ticket) : Message;

// ── Lobby push events (server → client, no cid) ──────────────────────────────
public sealed record PlayerJoinedMessage(string LobbyId, Player Player) : Message;
public sealed record PlayerLeftMessage(string LobbyId, string PlayerId) : Message;
public sealed record GameStartingMessage(
    string LobbyId,
    string GameId,
    string HostId,
    IReadOnlyList<Player> Players) : Message;

// ── Data role (game ⇄ server) ────────────────────────────────────────────────
// The game's first frame is Attach{ticket}. The server validates the ticket against live lobby
// membership, binds the connection to (playerId, lobbyId), and replies Ready. After that the game
// only sends GameMessage{to, payload} — it never names a lobby; the server resolves routing from
// the bound connection. To ∈ { "host", "all", "<playerId>" }; From is stamped on the way out.
public sealed record AttachMessage(string Ticket) : Message;
public sealed record ReadyMessage(string PlayerId, IReadOnlyList<Player> Players, bool IsHost) : Message;
public sealed record GameMessage(string To, JsonElement Payload, string? From = null) : Message;
// Game → server control (data role): the host sets whether the lobby accepts new joins. The server
// owns no "started" concept — the game decides this. Open lobbies are listed/joinable; closed ones
// are hidden from the browser and reject new joins (existing members and rejoins are unaffected).
public sealed record SetLobbyOpenMessage(bool Open) : Message;
public sealed record GamePlayerJoinedMessage(Player Player) : Message;
public sealed record GamePlayerLeftMessage(string PlayerId) : Message;

// ── Errors / rejections ──────────────────────────────────────────────────────
public sealed record ErrorMessage(string? Cid, string Reason) : Message;
