using KnockBox.Contracts;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Pins <see cref="LobbyCloser"/>, the one path that closes a lobby with members still connected. It was
/// extracted from <c>ServerAuthorityManager.HandleFatal</c> so the admin portal's manual close and the
/// authority fatal path can't drift; these tests are what would notice if a step went missing from it.
/// </summary>
public class LobbyCloserTests
{
    private static (LobbyCloser Closer, LobbyManager Lobbies, ConnectionManager Connections) SharedRig()
    {
        var lobbies = new LobbyManager();
        var connections = new ConnectionManager();
        return (new LobbyCloser(lobbies, connections, NullLogger<LobbyCloser>.Instance), lobbies, connections);
    }

    private static Connection Attach(ConnectionManager connections, string playerId, out FakeWebSocket socket,
        bool game = false)
    {
        socket = new FakeWebSocket();
        var connection = new Connection(playerId, playerId, socket, NullLogger<Connection>.Instance,
            game ? OutboundOverflow.DropOldest : OutboundOverflow.CloseOnFull);
        if (game) connections.AddGame(connection); else connections.Add(connection);
        return connection;
    }

    private static IMessage? FirstFrame(Connection connection, FakeWebSocket socket)
    {
        // Drain the connection's outbound channel onto its socket, then read what landed.
        connection.CompleteOutbound();
        connection.SendLoopAsync(CancellationToken.None).GetAwaiter().GetResult();
        return socket.Sent.Count == 0
            ? null
            : JsonSerializer.Deserialize(socket.Sent[0], KnockBoxProtocolContext.Default.IMessage);
    }

    [Fact]
    public void Closing_a_lobby_removes_it_and_tells_every_member_why()
    {
        var (closer, lobbies, connections) = SharedRig();
        Assert.True(lobbies.TryCreate("ttt", "p1", 4, out var lobby));
        lobby.TryAdd(new Player("p1", "Ada"));
        lobby.TryAdd(new Player("p2", "Grace"));
        var c1 = Attach(connections, "p1", out var s1);
        var c2 = Attach(connections, "p2", out var s2);

        Assert.True(closer.Close(lobby.Id, "Closed by an administrator."));

        Assert.Null(lobbies.Get(lobby.Id));
        foreach (var (connection, socket) in new[] { (c1, s1), (c2, s2) })
        {
            var closed = Assert.IsType<LobbyClosedMessage>(FirstFrame(connection, socket));
            Assert.Equal((lobby.Id, "Closed by an administrator."), (closed.LobbyId, closed.Reason));
        }
    }

    [Fact]
    public void Closing_a_lobby_aborts_the_members_game_sockets()
    {
        var (closer, lobbies, connections) = SharedRig();
        Assert.True(lobbies.TryCreate("ttt", "p1", 4, out var lobby));
        lobby.TryAdd(new Player("p1", "Ada"));
        Attach(connections, "p1", out _);
        Attach(connections, "p1", out var gameSocket, game: true);

        closer.Close(lobby.Id, "done");

        // The game is over; leaving its socket open leaves the iframe live against a lobby that no longer
        // exists, and its next frame would be dropped silently instead of ending the session.
        Assert.True(gameSocket.Aborted);
    }

    [Fact]
    public void Closing_an_unknown_code_reports_false_rather_than_pretending_it_worked()
    {
        var (closer, _, _) = SharedRig();
        // The API turns this into a 404: an operator must not be told a lobby was closed when no such
        // lobby existed (a typo'd code, or one that had already gone).
        Assert.False(closer.Close("ZZZZ", "nope"));
    }

    [Fact]
    public void Closing_by_lobby_object_still_notifies_when_the_lobby_was_already_unregistered()
    {
        var (closer, lobbies, connections) = SharedRig();
        Assert.True(lobbies.TryCreate("ttt", "p1", 4, out var lobby));
        lobby.TryAdd(new Player("p1", "Ada"));
        var c1 = Attach(connections, "p1", out var s1);

        // What a racing CloseLobbyIfDark leaves behind. The authority fatal path passes the lobby it holds
        // for exactly this reason: members still connected have to be told regardless.
        lobbies.Remove(lobby.Id);
        closer.Close(lobby, "authority-failed");

        Assert.IsType<LobbyClosedMessage>(FirstFrame(c1, s1));
    }

    [Fact]
    public void The_authority_hook_fires_once_per_closed_lobby()
    {
        var (closer, lobbies, _) = SharedRig();
        var stopped = new List<string>();
        closer.OnClosing = stopped.Add;

        Assert.True(lobbies.TryCreate("ttt", "p1", 4, out var a));
        Assert.True(lobbies.TryCreate("ttt", "p2", 4, out var b));
        closer.Close(a.Id, "x");
        closer.Close(b.Id, "x");

        // Without this hook a server-authority lobby leaks its Jint engine for the process lifetime.
        Assert.Equal([a.Id, b.Id], stopped);
    }

    [Fact]
    public void CloseForGame_closes_only_that_games_lobbies()
    {
        var (closer, lobbies, _) = SharedRig();
        Assert.True(lobbies.TryCreate("ttt", "p1", 4, out var ttt));
        Assert.True(lobbies.TryCreate("other", "p2", 4, out var other));

        Assert.Equal(1, closer.CloseForGame("TTT", "removed")); // case-insensitive, like the catalog
        Assert.Null(lobbies.Get(ttt.Id));
        Assert.NotNull(lobbies.Get(other.Id));
    }

    [Fact]
    public void CloseAll_closes_every_lobby_and_reports_the_count()
    {
        var (closer, lobbies, _) = SharedRig();
        for (var i = 0; i < 3; i++) Assert.True(lobbies.TryCreate($"g{i}", $"p{i}", 4, out _));

        Assert.Equal(3, closer.CloseAll("server maintenance"));
        Assert.Equal(0, lobbies.Count);
    }
}
