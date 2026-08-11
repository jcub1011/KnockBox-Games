namespace KnockBox.Server.Hosting;

public sealed record AdminAuthStatusResponse(bool Configured, bool Authenticated);
public sealed record AdminPasswordRequest(string Password);
public sealed record AdminApiResponse(bool Success, string? Error = null);
public sealed record AdminSystemStatusResponse(
    string Uptime,
    int ActiveLobbies,
    int RegisteredGames,
    long WorkingSetMb,
    long ManagedHeapMb,
    string HostTime
);
