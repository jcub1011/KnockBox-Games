using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

public class OriginRoutingTests
{
    // ── OriginAllowed ──────────────────────────────────────────────────────────
    [Fact]
    public void Empty_allowlist_allows_any_origin()
    {
        Assert.True(OriginRouting.OriginAllowed("https://evil.example", []));
    }

    [Fact]
    public void Empty_origin_is_always_allowed_for_native_clients()
    {
        Assert.True(OriginRouting.OriginAllowed("", ["https://shell.example"]));
        Assert.True(OriginRouting.OriginAllowed(null, ["https://shell.example"]));
    }

    [Theory]
    [InlineData("https://shell.example", true)]
    [InlineData("https://SHELL.example", true)]  // case-insensitive
    [InlineData("https://other.example", false)]
    public void Allowlist_matches_case_insensitively(string origin, bool expected)
    {
        Assert.Equal(expected, OriginRouting.OriginAllowed(origin, ["https://shell.example"]));
    }

    // ── IsGameOrigin ───────────────────────────────────────────────────────────
    [Fact]
    public void Dev_request_on_the_games_port_is_the_game_origin()
    {
        Assert.True(OriginRouting.IsGameOrigin(localPort: 5115, requestHost: "localhost", gamesPort: 5115, gamesHost: null));
    }

    [Fact]
    public void Request_on_the_shell_port_is_not_the_game_origin()
    {
        Assert.False(OriginRouting.IsGameOrigin(localPort: 5114, requestHost: "localhost", gamesPort: 5115, gamesHost: null));
    }

    [Fact]
    public void Prod_request_on_the_games_subdomain_is_the_game_origin()
    {
        // Behind a proxy every request shares one local port; the host header distinguishes origins.
        Assert.True(OriginRouting.IsGameOrigin(localPort: 8080, requestHost: "games.knockbox.example",
            gamesPort: 5115, gamesHost: "games.knockbox.example"));
        Assert.False(OriginRouting.IsGameOrigin(localPort: 8080, requestHost: "knockbox.example",
            gamesPort: 5115, gamesHost: "games.knockbox.example"));
    }

    // ── ResolveGameOrigin ──────────────────────────────────────────────────────
    [Fact]
    public void Explicit_games_origin_wins()
    {
        var origin = OriginRouting.ResolveGameOrigin("https", "knockbox.example", 5115,
            gamesHost: "games.knockbox.example", gamesOrigin: "https://cdn.example/games/");
        Assert.Equal("https://cdn.example/games", origin); // trailing slash trimmed
    }

    [Fact]
    public void Games_host_is_used_when_no_explicit_origin()
    {
        var origin = OriginRouting.ResolveGameOrigin("https", "knockbox.example", 5115,
            gamesHost: "games.knockbox.example", gamesOrigin: null);
        Assert.Equal("https://games.knockbox.example", origin);
    }

    [Fact]
    public void Falls_back_to_host_and_games_port_in_dev()
    {
        var origin = OriginRouting.ResolveGameOrigin("http", "localhost", 5115, gamesHost: null, gamesOrigin: null);
        Assert.Equal("http://localhost:5115", origin);
    }
}
