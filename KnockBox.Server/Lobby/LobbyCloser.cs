using KnockBox.Contracts;
using KnockBox.Server.Networking;

namespace KnockBox.Server.Lobbies;

/// <summary>
/// Closes a lobby that still has members connected, and tells them why.
///
/// This sequence — detach the authority actor, drop the lobby, push one <see cref="LobbyClosedMessage"/>
/// to every member's shell, abort their game sockets — existed only inside
/// <c>ServerAuthorityManager.HandleFatal</c>. The admin portal needs exactly the same teardown, and two
/// copies of it would drift: whichever one gained the next step (a new message, a new cleanup) would
/// leave the other half-closing lobbies. So it lives here once, and the fatal path calls it too.
/// </summary>
/// <remarks>
/// Distinct from <c>WebSocketHandler.CloseLobbyIfDark</c>, which removes a lobby nobody is connected to
/// and deliberately broadcasts nothing — there is no one left to tell. This one is for closing a
/// <em>live</em> lobby out from under its players.
/// </remarks>
public sealed class LobbyCloser(LobbyManager lobbies, ConnectionManager connections, ILogger<LobbyCloser> logger)
{
    /// <summary>
    /// Called with a lobby id as it closes, so a server-authority lobby's actor is stopped and its Jint
    /// engine released. Wired to <c>ServerAuthorityManager.Stop</c> in Program.cs.
    /// </summary>
    /// <remarks>
    /// A settable hook rather than a constructor dependency because the dependency runs the other way:
    /// <c>ServerAuthorityManager</c> needs this closer for its fatal path, so it cannot also be a
    /// constructor argument to it. Same resolver-callback shape as that manager's own
    /// <c>gameDirectory</c>. Set once during bootstrap, before any request.
    /// </remarks>
    public Action<string>? OnClosing { get; set; }

    /// <summary>
    /// Closes the lobby with this code. Returns false if no such lobby exists (already gone, or a bad
    /// code) — callers surface that as "not found" rather than reporting a close that never happened.
    /// </summary>
    public bool Close(string lobbyId, string reason)
    {
        var lobby = lobbies.Get(lobbyId);
        if (lobby is null) return false;
        Close(lobby, reason);
        return true;
    }

    /// <summary>
    /// Closes a lobby already in hand. Used by the authority fatal path, which holds the lobby via its
    /// actor and must notify members even if the lobby has meanwhile been unregistered by a racing
    /// <c>CloseLobbyIfDark</c>. Idempotent: every step is safe to repeat.
    /// </summary>
    public void Close(Lobby lobby, string reason)
    {
        // Detach the authority first, so nothing further is served for a lobby that is closing: the
        // relay diverts to:"host" on IsServerAuthority and would otherwise keep finding a live actor
        // for the moment between removing the lobby and telling anyone.
        OnClosing?.Invoke(lobby.Id);
        lobbies.Remove(lobby.Id);

        // Serialize once, send per member (the fan-out pattern used everywhere else here).
        var closed = ConnectionManager.Serialize(new LobbyClosedMessage(lobby.Id, reason));
        foreach (var player in lobby.Players)
        {
            connections.SendRawTo(player.Id, closed); // control plane: the shell shows the reason, returns home
            connections.GetGame(player.Id)?.Abort();  // the game is over — cut its socket rather than let it hang
        }

        logger.LogInformation("Lobby {LobbyId} (game {GameId}) closed: {Reason}", lobby.Id, lobby.GameId, reason);
    }

    /// <summary>Closes every lobby running one game. Returns how many were closed. Used by the admin
    /// portal's per-game bulk teardown and by the cascade a game deletion performs first.</summary>
    public int CloseForGame(string gameId, string reason)
    {
        var closed = 0;
        foreach (var lobby in lobbies.Snapshot())
        {
            if (!string.Equals(lobby.GameId, gameId, StringComparison.OrdinalIgnoreCase)) continue;
            Close(lobby, reason);
            closed++;
        }
        return closed;
    }

    /// <summary>Closes every lobby on the server. Returns how many were closed.</summary>
    public int CloseAll(string reason)
    {
        var closed = 0;
        foreach (var lobby in lobbies.Snapshot())
        {
            Close(lobby, reason);
            closed++;
        }
        return closed;
    }
}
