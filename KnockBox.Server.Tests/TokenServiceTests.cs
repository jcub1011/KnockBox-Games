using KnockBox.Server.Security;
using Xunit;

namespace KnockBox.Server.Tests;

public class TokenServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static (TokenService svc, MutableTimeProvider clock) Make(
        string secret = "test-secret", double identityTtlHours = 720, double ticketTtlHours = 12)
    {
        var clock = new MutableTimeProvider(T0);
        var config = ConfigFactory.FromPairs(
            ("KnockBox:TokenSecret", secret),
            ("KnockBox:IdentityTokenTtlHours", identityTtlHours.ToString()),
            ("KnockBox:GameTicketTtlHours", ticketTtlHours.ToString()));
        return (new TokenService(config, clock), clock);
    }

    [Fact]
    public void Identity_token_round_trips()
    {
        var (svc, _) = Make();
        var token = svc.IssueIdentity("player-abc");

        Assert.True(svc.TryVerifyIdentity(token, out var id));
        Assert.Equal("player-abc", id);
    }

    [Fact]
    public void Ticket_round_trips_all_fields()
    {
        var (svc, _) = Make();
        var ticket = svc.IssueTicket("p1", "LOBBY", "tictactoe");

        Assert.True(svc.TryVerifyTicket(ticket, out var p, out var l, out var g));
        Assert.Equal("p1", p);
        Assert.Equal("LOBBY", l);
        Assert.Equal("tictactoe", g);
    }

    [Fact]
    public void Tampered_signature_is_rejected()
    {
        var (svc, _) = Make();
        var token = svc.IssueIdentity("p1");
        var tampered = token[..^2] + (token[^1] == 'A' ? "B" : "A"); // flip the last sig char

        Assert.False(svc.TryVerifyIdentity(tampered, out _));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var (svc, _) = Make();
        var ticket = svc.IssueTicket("p1", "LOBBY", "ttt");
        // Swap the payload for a different player's; the original signature no longer matches.
        var (other, _) = Make();
        var forgedPayload = other.IssueTicket("attacker", "LOBBY", "ttt").Split('.')[0];
        var forged = forgedPayload + "." + ticket.Split('.')[1];

        Assert.False(svc.TryVerifyTicket(forged, out _, out _, out _));
    }

    [Fact]
    public void Token_signed_with_a_different_secret_is_rejected()
    {
        var (a, _) = Make(secret: "secret-A");
        var (b, _) = Make(secret: "secret-B");
        var token = a.IssueIdentity("p1");

        Assert.False(b.TryVerifyIdentity(token, out _));
    }

    [Fact]
    public void Expired_identity_token_is_rejected()
    {
        var (svc, clock) = Make(identityTtlHours: 1);
        var token = svc.IssueIdentity("p1");

        Assert.True(svc.TryVerifyIdentity(token, out _)); // valid now
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.False(svc.TryVerifyIdentity(token, out _)); // expired
    }

    [Fact]
    public void Expired_ticket_is_rejected()
    {
        var (svc, clock) = Make(ticketTtlHours: 2);
        var ticket = svc.IssueTicket("p1", "L", "ttt");

        clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1));
        Assert.False(svc.TryVerifyTicket(ticket, out _, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-dot")]
    [InlineData(".onlysig")]
    [InlineData("not-base64url!!.sig")]
    public void Malformed_tokens_are_rejected_without_throwing(string? token)
    {
        var (svc, _) = Make();
        Assert.False(svc.TryVerifyIdentity(token, out _));
        Assert.False(svc.TryVerifyTicket(token, out _, out _, out _));
    }

    [Fact]
    public void Issued_token_is_url_safe_base64()
    {
        // base64url never contains '+', '/', or '=' padding — important since it rides in a URL fragment.
        var (svc, _) = Make();
        var ticket = svc.IssueTicket("player with spaces", "LOBBY/слом", "game+id");

        Assert.DoesNotContain('+', ticket);
        Assert.DoesNotContain('/', ticket);
        Assert.DoesNotContain('=', ticket);
        Assert.True(svc.TryVerifyTicket(ticket, out var p, out _, out _));
        Assert.Equal("player with spaces", p);
    }
}
