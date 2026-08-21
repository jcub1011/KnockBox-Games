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

    [Fact]
    public void Games_origin_does_not_claim_the_admin_port()
    {
        // The game and admin branches must stay disjoint: whichever claims a request handles it alone, so
        // an overlap would serve the admin portal through the untrusted-game pipeline.
        Assert.False(OriginRouting.IsGameOrigin(localPort: 5116, requestHost: "localhost", gamesPort: 5115, gamesHost: null));
    }

    // ── IsAdminOrigin ──────────────────────────────────────────────────────────
    [Theory]
    [InlineData(5116, "localhost", true)]   // dev: the dedicated admin port
    [InlineData(5116, "admin.knockbox.test", true)]
    [InlineData(5114, "localhost", false)]  // shell port
    [InlineData(5115, "localhost", false)]  // games port
    [InlineData(5114, "other.host", false)]
    public void Admin_origin_is_the_admin_port_or_the_admin_host(int localPort, string requestHost, bool expected)
    {
        Assert.Equal(expected, OriginRouting.IsAdminOrigin(localPort, requestHost,
            adminPort: 5116, adminHost: "admin.knockbox.test"));
    }

    [Fact]
    public void Admin_port_alone_identifies_the_admin_origin_when_no_admin_host_is_set()
    {
        Assert.True(OriginRouting.IsAdminOrigin(localPort: 5116, requestHost: "localhost", adminPort: 5116, adminHost: null));
        Assert.False(OriginRouting.IsAdminOrigin(localPort: 5114, requestHost: "localhost", adminPort: 5116, adminHost: null));
    }

    [Fact]
    public void Admin_host_claims_a_request_arriving_on_the_shell_port()
    {
        // Prod routes the admin origin by Host, exactly as the games origin does, because behind a proxy
        // every request shares one local port. Consequence worth pinning: once AdminHost is set, a request
        // carrying that Host reaches the admin app even on the shell's port — the /admin* 404 gate never
        // runs for it, since the branch already claimed the request. So AdminHost must only be set behind a
        // proxy trusted to set Host (with KnockBox:ForwardedHeaders), never on a directly-exposed server.
        Assert.True(OriginRouting.IsAdminOrigin(localPort: 5114, requestHost: "admin.knockbox.test",
            adminPort: 5116, adminHost: "admin.knockbox.test"));
    }

    // No ResolveAdminOrigin cases: the helper was deleted with the tests that were its only callers —
    // see the note at the bottom of OriginRouting for why the admin origin has no resolver.
}
