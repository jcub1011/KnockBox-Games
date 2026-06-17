using KnockBox.Contracts;
using KnockBox.Server.Lobbies;
using Xunit;

namespace KnockBox.Server.Tests;

public class LobbyTests
{
    private static Lobby New(int max = 2) => new("ABCD", "ttt", "host", max);

    [Fact]
    public void Open_defaults_to_true()
    {
        Assert.True(New().Open);
    }

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
    public void TryAddUnique_keeps_a_non_colliding_name()
    {
        var lobby = New(max: 4);
        Assert.True(lobby.TryAddUnique("p1", "Ann", out var p1));
        Assert.Equal("Ann", p1!.DisplayName);
    }

    [Fact]
    public void TryAddUnique_disambiguates_colliding_names_with_an_ascending_suffix()
    {
        var lobby = New(max: 4);
        Assert.True(lobby.TryAddUnique("p1", "Bob", out var p1));
        Assert.True(lobby.TryAddUnique("p2", "Bob", out var p2));
        Assert.True(lobby.TryAddUnique("p3", "Bob", out var p3));

        Assert.Equal("Bob", p1!.DisplayName);
        Assert.Equal("Bob (2)", p2!.DisplayName);
        Assert.Equal("Bob (3)", p3!.DisplayName);
    }

    [Fact]
    public void TryAddUnique_treats_collisions_case_insensitively()
    {
        var lobby = New(max: 4);
        Assert.True(lobby.TryAddUnique("p1", "Bob", out _));
        Assert.True(lobby.TryAddUnique("p2", "bob", out var p2));
        Assert.Equal("bob (2)", p2!.DisplayName);
    }

    [Fact]
    public void TryAddUnique_keeps_the_assigned_name_on_rejoin()
    {
        var lobby = New(max: 4);
        lobby.TryAddUnique("p1", "Bob", out _);
        Assert.True(lobby.TryAddUnique("p2", "Bob", out var first));
        Assert.Equal("Bob (2)", first!.DisplayName);

        // Same id rejoining with their normal name keeps the name assigned the first time — no compounding.
        Assert.True(lobby.TryAddUnique("p2", "Bob", out var again));
        Assert.Equal("Bob (2)", again!.DisplayName);
        Assert.Equal(2, lobby.Count);
    }

    [Fact]
    public void TryAddUnique_fails_with_null_when_full_or_kicked()
    {
        var lobby = New(max: 1);
        Assert.True(lobby.TryAddUnique("p1", "Ann", out _));
        Assert.False(lobby.TryAddUnique("p2", "Bob", out var full)); // full
        Assert.Null(full);

        var kickLobby = New(max: 4);
        kickLobby.TryAddUnique("p1", "Ann", out _);
        kickLobby.Kick("p1");
        Assert.False(kickLobby.TryAddUnique("p1", "Ann", out var kicked)); // barred
        Assert.Null(kicked);
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

    [Fact]
    public void Kick_removes_member_and_bars_rejoin()
    {
        var lobby = New(max: 4);
        var p = new Player("p1", "Ann");
        Assert.True(lobby.TryAdd(p));

        Assert.False(lobby.IsKicked("p1"));
        Assert.True(lobby.Kick("p1"));        // was a member
        Assert.False(lobby.Contains("p1"));
        Assert.True(lobby.IsKicked("p1"));    // flagged so the join handler can give a clear reason
        Assert.False(lobby.TryAdd(p));        // kicked → cannot rejoin
        Assert.Equal(0, lobby.Count);
    }

    [Fact]
    public void Kick_is_recorded_even_if_target_already_left()
    {
        var lobby = New(max: 4);
        var p = new Player("p1", "Ann");

        Assert.False(lobby.Kick("p1"));       // not currently a member
        Assert.False(lobby.TryAdd(p));        // but the block still stands
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
            Assert.True(mgr.TryCreate("ttt", "host", 2, out var lobby));
            Assert.Equal(4, lobby.Id.Length);
            Assert.True(ids.Add(lobby.Id), "lobby ids must be unique");
        }
    }

    [Fact]
    public void Get_returns_null_for_unknown_lobby_and_after_removal()
    {
        var mgr = new LobbyManager();
        Assert.True(mgr.TryCreate("ttt", "host", 2, out var lobby));

        Assert.NotNull(mgr.Get(lobby.Id));
        mgr.Remove(lobby.Id);
        Assert.Null(mgr.Get(lobby.Id));
        Assert.Null(mgr.Get("ZZZZ"));
    }
}
