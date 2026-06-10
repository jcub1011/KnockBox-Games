using KnockBox.Contracts;
using KnockBox.Server.Lobbies;
using Xunit;

namespace KnockBox.Server.Tests;

public class LobbyTests
{
    private static Lobby New(int min = 2, int max = 2) => new("ABCD", "ttt", "host", min, max);

    [Fact]
    public void TryAdd_is_idempotent_for_an_existing_member()
    {
        var lobby = New(max: 2);
        var p = new Player("p1", "Ann");

        Assert.True(lobby.TryAdd(p));
        Assert.True(lobby.TryAdd(p)); // rejoin — still true, no duplicate
        Assert.Equal(1, lobby.Count);
    }

    [Fact]
    public void TryAdd_rejects_when_full()
    {
        var lobby = New(max: 2);
        Assert.True(lobby.TryAdd(new Player("p1", "Ann")));
        Assert.True(lobby.TryAdd(new Player("p2", "Bob")));
        Assert.False(lobby.TryAdd(new Player("p3", "Cy")));
        Assert.Equal(2, lobby.Count);
    }

    [Fact]
    public void Remove_reports_whether_a_member_was_present()
    {
        var lobby = New();
        lobby.TryAdd(new Player("p1", "Ann"));

        Assert.True(lobby.Remove("p1"));
        Assert.False(lobby.Remove("p1")); // already gone
        Assert.False(lobby.Contains("p1"));
    }
}

public class LobbyManagerTests
{
    [Fact]
    public void Create_assigns_unique_4_char_ids()
    {
        var mgr = new LobbyManager();
        var ids = new HashSet<string>();

        for (var i = 0; i < 200; i++)
        {
            var lobby = mgr.Create("ttt", "host", 2, 2);
            Assert.Equal(4, lobby.Id.Length);
            Assert.True(ids.Add(lobby.Id), "lobby ids must be unique");
        }
    }

    [Fact]
    public void Get_returns_null_for_unknown_lobby_and_after_removal()
    {
        var mgr = new LobbyManager();
        var lobby = mgr.Create("ttt", "host", 2, 2);

        Assert.NotNull(mgr.Get(lobby.Id));
        mgr.Remove(lobby.Id);
        Assert.Null(mgr.Get(lobby.Id));
        Assert.Null(mgr.Get("ZZZZ"));
    }
}
