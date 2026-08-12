namespace KnockBox.Server.Hosting;

/// <summary>
/// Pure helpers for deciding which origin a request belongs to and what game origin the shell should
/// be told to use. Kept free of <c>HttpContext</c> so the routing rules are unit-testable.
///
/// Games are served from a SEPARATE ORIGIN from the shell so untrusted game code can't read the
/// shell's identity token or socket. In dev that origin is a second port; in prod it is a subdomain
/// (<c>KnockBox:GamesHost</c>). Either way the game's data socket connects back to <c>/ws</c>.
/// </summary>
public static class OriginRouting
{
    /// <summary>
    /// Origin allowlist for <c>/ws</c> (defense-in-depth; the real auth is the identity token / game
    /// ticket). An empty allowlist allows all (dev). An empty Origin header is always allowed: only
    /// browsers are obliged to send one, and native engine clients (Godot/Unity) send none — the
    /// token/ticket, not this check, is what actually authenticates them.
    /// </summary>
    public static bool OriginAllowed(string? origin, IReadOnlyList<string> allowed) =>
        string.IsNullOrEmpty(origin) || allowed.Count == 0 ||
        allowed.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if the request arrived on the game origin: the dedicated dev port, or (in prod) a request
    /// whose host matches the configured games subdomain.
    /// </summary>
    public static bool IsGameOrigin(int localPort, string? requestHost, int gamesPort, string? gamesHost) =>
        localPort == gamesPort ||
        (!string.IsNullOrEmpty(gamesHost) &&
         string.Equals(requestHost, gamesHost, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The origin the shell should embed game iframes from (and that the game's data socket connects
    /// back to). Precedence: an explicit configured origin, else the configured games subdomain, else
    /// the dev fallback of the same host on the games port.
    /// </summary>
    public static string ResolveGameOrigin(
        string scheme, string requestHost, int gamesPort, string? gamesHost, string? gamesOrigin)
    {
        if (!string.IsNullOrWhiteSpace(gamesOrigin)) return gamesOrigin.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(gamesHost)) return $"{scheme}://{gamesHost}";
        return $"{scheme}://{requestHost}:{gamesPort}";
    }

    /// <summary>
    /// True if the request arrived on the admin origin: the dedicated admin dev port, or (in prod) a request
    /// whose host matches the configured admin subdomain.
    /// </summary>
    public static bool IsAdminOrigin(int localPort, string? requestHost, int adminPort, string? adminHost) =>
        localPort == adminPort ||
        (!string.IsNullOrEmpty(adminHost) &&
         string.Equals(requestHost, adminHost, StringComparison.OrdinalIgnoreCase));

    // There is deliberately no ResolveAdminOrigin twin of ResolveGameOrigin. The shell must be TOLD the
    // game origin (it embeds iframes from it and the game socket dials it), whereas nothing is ever told
    // the admin origin — an operator navigates to it. The only code that needs to name it is the startup
    // log, and it cannot use a shared resolver honestly: when the portal is host-routed the scheme
    // belongs to the fronting proxy, so the log says which HOST serves the portal rather than inventing a
    // URL. A resolver here would be a second, divergent copy of that precedence with tests covering only
    // the copy nothing calls.
}
