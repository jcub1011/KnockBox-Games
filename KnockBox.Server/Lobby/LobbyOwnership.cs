using KnockBox.Contracts;
using KnockBox.Server.Networking;

namespace KnockBox.Server.Lobbies;

/// <summary>
/// Moves the lobby owner — the member holding the two lobby powers (SetLobbyOpen, KickPlayer) — and
/// announces it on both planes.
///
/// The sequence (<see cref="Lobby.TrySetHost"/>, then one <see cref="OwnerChangedMessage"/> to every
/// member's shell and one <see cref="GameOwnerChangedMessage"/> to their game socket) existed only
/// inside <c>ServerAuthority</c>'s handling of <c>kb.setOwner</c>. The admin kick needs exactly the same
/// move, for the same reason <c>LobbyCloser</c> exists: two copies drift, and whichever one gained the
/// next step would leave the other announcing half an ownership change.
/// </summary>
/// <remarks>
/// Announcing on BOTH planes is the load-bearing part. The shell reads <c>OwnerChanged</c> to decide
/// whether to offer the owner-only controls; an in-game SDK reads the <c>Game*</c> mirror to update
/// <c>ownerId</c>/<c>isOwner</c>. Sending one without the other leaves a client that believes it holds
/// powers the server will refuse it, which reads to the player as the game having frozen.
/// </remarks>
public static class LobbyOwnership
{
    /// <summary>
    /// Makes <paramref name="playerId"/> the lobby owner and tells every member. False (and nothing
    /// sent) when they are not a current member — <see cref="Lobby.TrySetHost"/> validates that
    /// atomically under the lobby lock, so this cannot hand ownership to someone who left mid-call.
    /// </summary>
    public static bool Reassign(Lobby lobby, ConnectionManager connections, string playerId)
    {
        if (!lobby.TrySetHost(playerId)) return false;

        var control = ConnectionManager.Serialize(new OwnerChangedMessage(lobby.Id, playerId));
        var data = ConnectionManager.Serialize(new GameOwnerChangedMessage(playerId));
        foreach (var member in lobby.Players)
        {
            connections.SendRawTo(member.Id, control);
            connections.SendRawToGame(member.Id, data);
        }
        return true;
    }

    /// <summary>
    /// A member who could take over: someone other than <paramref name="excludingPlayerId"/> who still
    /// holds a live control socket, or null when nobody does.
    /// </summary>
    /// <remarks>
    /// Connected, not merely present. A member inside the reconnect grace window is still in the roster
    /// but has no socket to receive the handover on, so promoting them would produce an owner who cannot
    /// exercise the powers and may never come back — the same dangling-owner state this exists to avoid.
    /// </remarks>
    public static string? NextOwner(Lobby lobby, ConnectionManager connections, string excludingPlayerId) =>
        lobby.Players.FirstOrDefault(p =>
            !string.Equals(p.Id, excludingPlayerId, StringComparison.Ordinal)
            && connections.Get(p.Id) is not null)?.Id;
}
