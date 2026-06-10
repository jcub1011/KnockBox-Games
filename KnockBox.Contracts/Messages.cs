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
[JsonDerivedType(typeof(PlayerJoinedMessage), "PlayerJoined")]
[JsonDerivedType(typeof(PlayerLeftMessage), "PlayerLeft")]
[JsonDerivedType(typeof(GameStartingMessage), "GameStarting")]
[JsonDerivedType(typeof(RelayMessage), "Relay")]
[JsonDerivedType(typeof(ErrorMessage), "Error")]
public abstract record Message;

// ── Identity (first exchange after connect) ──────────────────────────────────
public sealed record HelloMessage(string? PlayerId, string DisplayName) : Message;
public sealed record WelcomeMessage(string PlayerId) : Message;

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

// ── Lobby push events (server → client, no cid) ──────────────────────────────
public sealed record PlayerJoinedMessage(string LobbyId, Player Player) : Message;
public sealed record PlayerLeftMessage(string LobbyId, string PlayerId) : Message;
public sealed record GameStartingMessage(
    string LobbyId,
    string GameId,
    string HostId,
    IReadOnlyList<Player> Players) : Message;

// ── Relay (opaque game payload; the server never inspects it) ────────────────
/// <summary>
/// Carries an opaque game payload between lobby members. <c>To</c> is the routing target:
/// <c>"host"</c> (the lobby's authoritative host), <c>"all"</c> (every member incl. sender),
/// or a specific <c>playerId</c>. The server stamps <c>From</c> (sender's id) on the way out.
/// </summary>
public sealed record RelayMessage(
    string LobbyId,
    string To,
    JsonElement Payload,
    string? From = null) : Message;

// ── Errors / rejections ──────────────────────────────────────────────────────
public sealed record ErrorMessage(string? Cid, string Reason) : Message;
