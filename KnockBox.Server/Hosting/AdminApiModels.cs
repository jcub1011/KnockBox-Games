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
/// <param name="PackageRoot">
/// Which package root holds the source <c>.kbg</c>: "games" for one an operator dropped in by hand,
/// "managed" for one the portal installed, null for a plain game folder. A managed package is the one
/// the server may replace or remove, so this is what tells the portal an update or rollback applies.
/// </param>
/// <param name="BackupBytes">Retained previous versions of a managed package, kept for rollback.</param>
/// <param name="Lifecycle">
/// "ready", "draining" or "updating" — engine state, NOT operator policy. It is deliberately absent
/// from the availability control: that select is a command, and offering a value the server would have
/// to refuse is worse than not offering it.
/// </param>
/// <param name="UpdatePolicy">"manual", "auto", "drain" or "force" — what the scheduled check may do.</param>
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
    string? PackageRoot,
    long DiskBytes,
    long DirectoryBytes,
    long CompressedBytes,
    long PackageBytes,
    long BackupBytes,
    int ActiveLobbies,
    int ActivePlayers,
    bool Deletable,
    string? DeleteBlockedReason,
    string Lifecycle,
    string UpdatePolicy,
    string? PendingJobId
);

public sealed record AdminGamesResponse(
    IReadOnlyList<AdminGameSummary> Games,
    string GamesRoot,
    string PackagesRoot,
    string? ScanError,
    string DiskMeasuredAt,
    long CompressedCacheBytes,
    long LogsBytes,
    string ManagedRoot,
    long ManagedRootBytes
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

// ── Package jobs ─────────────────────────────────────────────────────────────

/// <summary>One install/update/rollback/uninstall operation, as the portal renders it.</summary>
/// <param name="Sequence">Bumped on every change — the cursor to pass back as <c>after</c>.</param>
/// <param name="Kind">"install", "update", "rollback" or "uninstall".</param>
/// <param name="Source">Where the bytes came from: "marketplace", "upload", "backup" or "none".</param>
/// <param name="Status">
/// "queued", "downloading", "verifying", "waitingForLobbies", "applying", "succeeded", "failed" or
/// "cancelled". The last three are terminal.
/// </param>
/// <param name="Phase">A sentence for the operator. Changes far more often than <paramref name="Status"/>.</param>
/// <param name="BytesTotal">0 when unknown — render indeterminate progress, never a confident 0%.</param>
/// <param name="Mode">"auto", "drain" or "force" — what this job is allowed to do to running lobbies.</param>
/// <param name="Cancellable">False from "applying" onwards: a half-swapped game is not worth creating.</param>
public sealed record AdminJobSummary(
    string JobId,
    long Sequence,
    string Kind,
    string Source,
    string GameId,
    string? GameName,
    string? FromVersion,
    string? ToVersion,
    string Status,
    string Phase,
    long BytesDone,
    long BytesTotal,
    string Mode,
    string StartedAt,
    string? EndedAt,
    string? Error,
    string? Warning,
    int LobbiesWaiting,
    bool Cancellable,
    bool Terminal
);

public sealed record AdminJobsResponse(
    IReadOnlyList<AdminJobSummary> Jobs,
    long LastSequence,
    int Active,
    int Retained
);

/// <summary>The reply to any route that starts a job: 202 plus the id to follow it by.</summary>
public sealed record AdminJobResponse(
    bool Success,
    string? Error = null,
    string? JobId = null,
    string? Detail = null,
    string? Warning = null
);

// ── Marketplace ──────────────────────────────────────────────────────────────

/// <param name="Status">
/// A PluginUpdateStatus, camelCase — plus "installedOnly", which is not one of them: it marks a managed
/// game no enabled source offers (an upload, or a withdrawn entry).
/// </param>
/// <param name="ShadowedBy">Another source offered this id first and won. Reported, never silently dropped.</param>
/// <param name="Managed">Its package is in the managed root, so update/rollback/uninstall apply to it.</param>
public sealed record AdminMarketplaceEntry(
    string Id,
    string Name,
    string? Description,
    string? Author,
    IReadOnlyList<string>? Tags,
    string? AvailableVersion,
    string? InstalledVersion,
    string Status,
    string? Reason,
    long? SizeBytes,
    string? PublishedAt,
    string? MinAppVersion,
    string? MaxAppVersion,
    string SourceId,
    string? SourceName,
    string? ShadowedBy,
    bool Managed,
    bool Installed,
    int ActiveLobbies,
    string? PendingJobId,
    IReadOnlyList<AdminRetainedVersion> Backups
);

/// <summary>A retained earlier version of a managed package, available as a rollback target.</summary>
public sealed record AdminRetainedVersion(string? Version, long Bytes, string RetainedAt);

public sealed record AdminMarketplaceSource(
    string Id,
    string Name,
    string CatalogUrl,
    string DownloadBaseUrl,
    bool Enabled,
    bool BuiltIn,
    int Entries,
    string? Error
);

/// <param name="Enabled">KnockBox:MarketplaceEnabled. False ⇒ no source is fetched and install is refused.</param>
/// <param name="AppVersion">This server's version — what every "incompatible" row is judged against.</param>
/// <param name="MaxUploadBytes">KnockBox:MaxPackageBytes, so the upload guard can't drift from the server's.</param>
public sealed record AdminMarketplaceResponse(
    IReadOnlyList<AdminMarketplaceEntry> Entries,
    IReadOnlyList<AdminMarketplaceSource> Sources,
    IReadOnlyList<AdminJobSummary> Jobs,
    long JobsLastSequence,
    bool Enabled,
    string AppVersion,
    string? FetchedAt,
    int MaxSources,
    int BackupRetention,
    long MaxUploadBytes,
    bool CanInstall,
    string? InstallBlockedReason,
    string ManagedRoot
);

// ── Requests ─────────────────────────────────────────────────────────────────

public sealed record AdminCloseLobbiesRequest(string? GameId = null, string? Reason = null);
public sealed record AdminPurgeStaleRequest(int? IdleMinutes = null);
public sealed record AdminKickRequest(string? PlayerId = null);
public sealed record AdminAvailabilityRequest(string? State = null);
public sealed record AdminMaintenanceRequest(bool Enabled = false, string? Message = null);

/// <param name="Version">Which retained version to return to; null takes the most recent.</param>
/// <param name="Mode">"auto", "drain" or "force". Defaults to "drain" — the least disruptive that still happens.</param>
public sealed record AdminRollbackRequest(string? Version = null, string? Mode = null);

/// <param name="SourceId">Which marketplace to take it from; null uses whichever offered it first.</param>
public sealed record AdminInstallRequest(string? SourceId = null, string? Mode = null);

/// <param name="Policy">"manual", "auto", "drain" or "force" — what the scheduled check may do unattended.</param>
public sealed record AdminUpdatePolicyRequest(string? Policy = null);

/// <summary>
/// Registers or updates an extra marketplace. Every member is nullable so a partial body leaves the
/// rest alone, rather than a defaulted <c>false</c> silently disabling a source.
/// </summary>
public sealed record AdminSourceRequest(
    string? Id = null,
    string? Name = null,
    string? CatalogUrl = null,
    string? DownloadBaseUrl = null,
    bool? Enabled = null);
