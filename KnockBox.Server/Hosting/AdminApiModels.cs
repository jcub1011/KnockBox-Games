namespace KnockBox.Server.Hosting;

// Wire shapes for /admin/api/*. Serialized camelCase by KnockBoxProtocolContext, which every one of
// these must also be registered in — reflection-based JSON is not Native-AOT-safe, and the `aot` CI job
// fails the build on the trim warnings it produces.
//
// Request records are all-defaulted so a missing or misspelled field degrades to a default instead of
// throwing during deserialization, the same discipline the marketplace DTOs use for untrusted input.

public sealed record AdminAuthStatusResponse(bool Configured, bool Authenticated);
public sealed record AdminPasswordRequest(string Password);
public sealed record AdminApiResponse(bool Success, string? Error = null);

/// <summary>
/// Result of an operator action.
/// </summary>
/// <param name="Affected">How many things the action touched (lobbies closed, for the bulk operations).</param>
/// <param name="Warning">
/// The action succeeded but something about it is worth saying — chiefly that a policy change is live but
/// could not be written to disk, so it will not survive a restart. Distinct from
/// <paramref name="Error"/>: an error means nothing happened.
/// </param>
public sealed record AdminActionResponse(
    bool Success,
    string? Error = null,
    int Affected = 0,
    string? Warning = null,
    string? Detail = null
);

// ── System status & metrics ──────────────────────────────────────────────────

public sealed record AdminSystemStatusResponse(
    string Uptime,
    int ActiveLobbies,
    int RegisteredGames,
    long WorkingSetMb,
    long ManagedHeapMb,
    string HostTime,
    int ConnectedPlayers,
    int GameSockets,
    int AuthorityLobbies,
    bool MaintenanceMode,
    string? MaintenanceMessage,
    // Average CPU across the whole process lifetime (total processor time / wall time / core count).
    // A lifetime average, not an instantaneous rate — the portal derives "right now" from two polls.
    double CpuPercentLifetime,
    double CpuSecondsTotal,
    int ProcessorCount,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    string? ScanError,
    string? SettingsError,
    IReadOnlyList<AdminDiagnosticIssue> Diagnostics
);

/// <summary>
/// A deployment problem from <c>DeploymentDiagnostics</c>. These already replace the shell's home page
/// when blocking, but an operator watching the dashboard should not have to open the player site to find
/// out the games mount is unreadable.
/// </summary>
public sealed record AdminDiagnosticIssue(string Title, string Detail, bool Blocking);

/// <summary>One game's share of the relay cost. See <c>RelayMetrics</c> for why games aren't free.</summary>
public sealed record AdminGameRelayMetrics(
    string GameId,
    long FramesIn,
    long FramesOut,
    long BytesIn,
    long BytesOut,
    long FramesDropped,
    double FanOut,
    int Lobbies,
    int Players,
    long SocketFramesSent,
    long SocketBytesSent,
    long SocketFramesDropped
);

public sealed record AdminMetricsResponse(
    IReadOnlyList<AdminGameRelayMetrics> Games,
    int ControlSockets,
    int GameSockets,
    long OutboundFramesSent,
    long OutboundBytesSent,
    long OutboundFramesDropped,
    int TrackedRateLimitIps,
    string HostTime
);

// ── Lobbies ──────────────────────────────────────────────────────────────────

public sealed record AdminLobbyMember(
    string PlayerId,
    string DisplayName,
    bool IsHost,
    bool Connected,
    long DisconnectedSeconds
);

public sealed record AdminLobbySummary(
    string Code,
    string GameId,
    string? GameName,
    string? GameVersion,
    int Players,
    int MaxPlayers,
    int Disconnected,
    string HostId,
    bool Open,
    bool ServerAuthority,
    string CreatedAt,
    long AgeSeconds,
    long IdleSeconds,
    string Status,
    IReadOnlyList<AdminLobbyMember> Members
);

public sealed record AdminLobbiesResponse(
    IReadOnlyList<AdminLobbySummary> Lobbies,
    int StaleAfterMinutes,
    string HostTime
);

// ── Game catalog ─────────────────────────────────────────────────────────────

/// <param name="Availability">"available", "disabled" or "staged" — the operator override, not a discovery state.</param>
/// <param name="Root">Which root won this id: "games" or "packages".</param>
/// <param name="DeleteBlockedReason">Why Delete is unavailable, e.g. a read-only games mount.</param>
public sealed record AdminGameSummary(
    string Id,
    string Name,
    string? Version,
    string Availability,
    int MaxPlayers,
    bool ServerAuthority,
    string Directory,
    string Root,
    bool PackageBacked,
    long DiskBytes,
    long DirectoryBytes,
    long CompressedBytes,
    long PackageBytes,
    int ActiveLobbies,
    int ActivePlayers,
    bool Deletable,
    string? DeleteBlockedReason
);

public sealed record AdminGamesResponse(
    IReadOnlyList<AdminGameSummary> Games,
    string GamesRoot,
    string PackagesRoot,
    string? ScanError,
    string DiskMeasuredAt,
    long CompressedCacheBytes,
    long LogsBytes
);

// ── Logs ─────────────────────────────────────────────────────────────────────

/// <param name="Category">Serilog's SourceContext — the subsystem. "KnockBox.GameLog" is game-relayed output.</param>
public sealed record AdminLogEntry(
    long Seq,
    string Time,
    string Level,
    string Category,
    string Message,
    string? Exception
);

/// <param name="LastSequence">Highest sequence in the buffer — the cursor to pass back as <c>after</c>.</param>
public sealed record AdminLogsResponse(
    IReadOnlyList<AdminLogEntry> Entries,
    long LastSequence,
    long TotalWritten,
    int Buffered
);

public sealed record AdminLogFile(string Name, long Bytes, string Modified);

public sealed record AdminLogFilesResponse(
    IReadOnlyList<AdminLogFile> Files,
    string LogsRoot,
    string? Error
);

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed record AdminCloseLobbiesRequest(string? GameId = null, string? Reason = null);
public sealed record AdminPurgeStaleRequest(int? IdleMinutes = null);
public sealed record AdminKickRequest(string? PlayerId = null);
public sealed record AdminAvailabilityRequest(string? State = null);
public sealed record AdminMaintenanceRequest(bool Enabled = false, string? Message = null);
