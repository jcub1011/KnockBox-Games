using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnockBox.Server.Security;

/// <summary>
/// Issues and verifies HMAC-signed tokens. Two kinds, both signed with the same per-process secret:
/// <list type="bullet">
/// <item><b>Identity token</b> — binds an anonymous, per-tab <c>playerId</c> so a reconnecting
/// client can prove it owns that id (anti-spoof) without any login. Lives only on the shell origin.</item>
/// <item><b>Game ticket</b> — a scoped credential for the data role, carrying
/// <c>(playerId, lobbyId, gameId)</c>. Handed to a game iframe so it can open its own websocket
/// without ever seeing the identity token.</item>
/// </list>
/// The secret is random per process: a restart invalidates all tokens, which is harmless because
/// in-memory lobbies are dropped on restart too. Set <c>KnockBox:TokenSecret</c> in config to keep
/// tokens valid across restarts.
/// </summary>
public sealed class TokenService
{
    private readonly byte[] _secret;

    public TokenService(IConfiguration config)
    {
        var configured = config["KnockBox:TokenSecret"];
        _secret = string.IsNullOrWhiteSpace(configured)
            ? RandomNumberGenerator.GetBytes(32)
            : Encoding.UTF8.GetBytes(configured);
    }

    // ── Identity token: "<playerId>.<sig>" ───────────────────────────────────
    public string IssueIdentity(string playerId) => $"{playerId}.{Sign(playerId)}";

    public bool TryVerifyIdentity(string? token, out string playerId)
    {
        playerId = "";
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;

        var id = token[..dot];
        var sig = token[(dot + 1)..];
        if (!FixedTimeEquals(sig, Sign(id))) return false;

        playerId = id;
        return true;
    }

    // ── Game ticket: base64url(json).<sig> ───────────────────────────────────
    private sealed record Ticket(string PlayerId, string LobbyId, string GameId);

    public string IssueTicket(string playerId, string lobbyId, string gameId)
    {
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new Ticket(playerId, lobbyId, gameId)));
        return $"{payload}.{Sign(payload)}";
    }

    public bool TryVerifyTicket(string? ticket, out string playerId, out string lobbyId, out string gameId)
    {
        playerId = lobbyId = gameId = "";
        if (string.IsNullOrEmpty(ticket)) return false;
        var dot = ticket.LastIndexOf('.');
        if (dot <= 0) return false;

        var payload = ticket[..dot];
        var sig = ticket[(dot + 1)..];
        if (!FixedTimeEquals(sig, Sign(payload))) return false;

        try
        {
            var t = JsonSerializer.Deserialize<Ticket>(Base64UrlDecode(payload));
            if (t is null) return false;
            (playerId, lobbyId, gameId) = (t.PlayerId, t.LobbyId, t.GameId);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return false;
        }
    }

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
