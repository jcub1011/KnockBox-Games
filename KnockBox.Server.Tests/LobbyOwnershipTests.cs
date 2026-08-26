using KnockBox.Contracts;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Handing the lobby powers to another member.
///
/// The move existed only inside the authority module's <c>kb.setOwner</c> handling until the admin kick
/// needed it: <see cref="Lobby.Kick"/> drops a member without touching <c>HostId</c>, so removing the
/// owner leaves that id naming a non-member. Nothing then passes the handlers' <c>PlayerId == HostId
/// &amp;&amp; Contains(PlayerId)</c> test, which kills SetLobbyOpen and the in-game kick for everyone,
/// and a <c>to:"host"</c> relay finds no game connection and fans out to nobody — the game freezes with
/// no error anywhere. The in-game kick avoids it by refusing to remove the owner; an operator has no
/// other way to remove that person, so the admin path reassigns instead.
/// </summary>
public class LobbyOwnershipTests
{
    private static Connection Register(ConnectionManager connections, string playerId)
    {
        var conn = new Connection(playerId, playerId, new FakeWebSocket(),
            NullLogger<Connection>.Instance, OutboundOverflow.CloseOnFull);
        connections.Add(conn);
        return conn;
    }

    private static Lobby NewLobby(out ConnectionManager connections)
    {
        connections = new ConnectionManager();
        var lobbies = new LobbyManager();
        Assert.True(lobbies.TryCreate("g", "host", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("host", "Host")));
        Assert.True(lobby.TryAdd(new Player("guest", "Guest")));
        return lobby;
    }

    [Fact]
    public void Reassign_moves_the_powers_and_tells_everyone()
    {
        var lobby = NewLobby(out var connections);
        Register(connections, "host");
        Register(connections, "guest");

        Assert.True(LobbyOwnership.Reassign(lobby, connections, "guest"));
        Assert.Equal("guest", lobby.HostId);
    }

    [Fact]
    public void Reassign_refuses_a_non_member_and_changes_nothing()
    {
        // Validated atomically under the lobby lock, so ownership cannot land on someone who left
        // mid-call — the state this whole class exists to avoid.
        var lobby = NewLobby(out var connections);
        Assert.False(LobbyOwnership.Reassign(lobby, connections, "stranger"));
        Assert.Equal("host", lobby.HostId);
    }

    [Fact]
    public void NextOwner_picks_a_member_who_is_actually_connected()
    {
        var lobby = NewLobby(out var connections);
        Register(connections, "host");
        Register(connections, "guest");

        Assert.Equal("guest", LobbyOwnership.NextOwner(lobby, connections, "host"));
    }

    [Fact]
    public void NextOwner_skips_a_member_inside_the_reconnect_grace_window()
    {
        // Present in the roster but holding no socket. Promoting them produces an owner who cannot
        // exercise the powers and may never come back, which is the same dangling state by another name.
        var lobby = NewLobby(out var connections);
        Register(connections, "host");

        Assert.Null(LobbyOwnership.NextOwner(lobby, connections, "host"));
    }

    [Fact]
    public void NextOwner_is_null_when_the_owner_is_the_only_one_left()
    {
        // What the admin kick reads as "there is nobody to hand this to", and closes the lobby instead of
        // kicking into an empty room.
        var lobbies = new LobbyManager();
        var connections = new ConnectionManager();
        Assert.True(lobbies.TryCreate("g", "solo", 4, out var lobby));
        Assert.True(lobby.TryAdd(new Player("solo", "Solo")));
        Register(connections, "solo");

        Assert.Null(LobbyOwnership.NextOwner(lobby, connections, "solo"));
    }
}
