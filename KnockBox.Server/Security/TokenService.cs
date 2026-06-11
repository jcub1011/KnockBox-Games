using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnockBox.Server.Security;

/// <summary>
/// Issues and verifies HMAC-signed tokens. Two kinds, both signed with the same per-process secret
/// and both carrying an <c>exp</c> (absolute expiry) so a leaked token is bounded in time:
/// <list type="bullet">
/// <item><b>Identity token</b> — binds an anonymous, per-tab <c>playerId</c> so a reconnecting
/// client can prove it owns that id (anti-spoof) without any login. Lives only on the shell origin.</item>
/// <item><b>Game ticket</b> — a lobby-scoped credential for the data role, carrying
/// <c>(playerId, lobbyId, gameId)</c>. Handed to a game iframe so it can open its own websocket
/// without ever seeing the identity token. It is reusable while the holder remains a lobby member
/// (so reconnects work) and until it expires — the live membership check in the handler is the
/// primary control; expiry is defence-in-depth.</item>
/// </list>
/// The secret is random per process unless <c>KnockBox:TokenSecret</c> is configured; a restart then
/// invalidates all tokens, which is harmless because in-memory lobbies are dropped on restart too.
/// </summary>
public sealed class TokenService
{
    private readonly byte[] _secret;
    private readonly TimeProvider _clock;
    private readonly ILogger<TokenService> _logger;
    private readonly TimeSpan _identityTtl;
    private readonly TimeSpan _ticketTtl;

    public TokenService(IConfiguration config, TimeProvider clock, ILogger<TokenService> logger)
    {
        _clock = clock;
        _logger = logger;
        var configured = config["KnockBox:TokenSecret"];
        _secret = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configured);

        _identityTtl = TimeSpan.FromHours(config.GetValue("KnockBox:IdentityTokenTtlHours", 720.0)); // 30 days
        _ticketTtl = TimeSpan.FromHours(config.GetValue("KnockBox:GameTicketTtlHours", 12.0));        // a play session
    }

    // ── Identity token: base64url(json{playerId,exp}).<sig> ──────────────────
    private sealed record IdentityPayload(string PlayerId, long Exp);

    public string IssueIdentity(string playerId) =>
        Encode(new IdentityPayload(playerId, ExpiresAt(_identityTtl)));

    public bool TryVerifyIdentity(string? token, out string playerId)
    {
        playerId = "";
        if (!TryDecode<IdentityPayload>(token, out var p) || IsExpired(p!.Exp)) return false;
        playerId = p.PlayerId;
        return true;
    }

    // ── Game ticket: base64url(json{playerId,lobbyId,gameId,exp}).<sig> ───────
    private sealed record TicketPayload(string PlayerId, string LobbyId, string GameId, long Exp);

    public string IssueTicket(string playerId, string lobbyId, string gameId) =>
        Encode(new TicketPayload(playerId, lobbyId, gameId, ExpiresAt(_ticketTtl)));

    public bool TryVerifyTicket(string? ticket, out string playerId, out string lobbyId, out string gameId)
    {
        playerId = lobbyId = gameId = "";
        if (!TryDecode<TicketPayload>(ticket, out var t) || IsExpired(t!.Exp)) return false;
        (playerId, lobbyId, gameId) = (t.PlayerId, t.LobbyId, t.GameId);
        return true;
    }

    // ── Encode / decode ──────────────────────────────────────────────────────
    private string Encode<T>(T payload)
    {
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{body}.{Sign(body)}";
    }

    private bool TryDecode<T>(string? token, out T? payload) where T : class
    {
        payload = null;
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;

        var body = token[..dot];
        var sig = token[(dot + 1)..];
        if (!FixedTimeEquals(sig, Sign(body))) return false;

        try
        {
            payload = JsonSerializer.Deserialize<T>(Base64UrlDecode(body));
            return payload is not null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            // Signature already verified above, so a malformed body here is unusual (corruption or a
            // forged-but-correctly-signed token). Not Error-worthy, but worth an audit trail.
            _logger.LogDebug(ex, "Discarding token with a valid signature but an undecodable {Payload} payload.", typeof(T).Name);
            return false;
        }
    }

    private long ExpiresAt(TimeSpan ttl) => _clock.GetUtcNow().Add(ttl).ToUnixTimeSeconds();
    private bool IsExpired(long exp) => _clock.GetUtcNow().ToUnixTimeSeconds() >= exp;

    // ── Helpers ──────────────────────────────────────────────────────────────
    private string Sign(string data)
    {
        using var hmac = new HMACSHA256(_secret);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }
}
