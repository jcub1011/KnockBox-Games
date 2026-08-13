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
    long SocketFramesDropped,
    // Server-authority games only, and 0 for every other game — which is the honest answer, not a gap: a
    // browser-side game runs no code in this process. See AuthorityMetrics.
    long AuthorityCalls,
    double AuthorityCpuSeconds,
    double AuthorityAverageMs,
    double AuthorityMaxMs,
    long AuthorityErrors
);

// ── Metric history (§5.2) ────────────────────────────────────────────────────

/// <summary>One game's row inside a history sample. Cumulative totals; the client differences them.</summary>
public sealed record AdminMetricGame(
    string GameId,
    int Lobbies,
    long FramesOut,
    long BytesOut,
    long FramesDropped,
    double AuthorityCpuSeconds);

/// <summary>One point in the platform time series.</summary>
public sealed record AdminMetricSample(
    long Sequence,
    string At,
    double CpuSeconds,
    long WorkingSetMb,
    long ManagedHeapMb,
    int Lobbies,
    int Players,
    int GameSockets,
    int AuthorityLobbies,
    IReadOnlyList<AdminMetricGame> Games);

/// <summary>
/// Samples newer than the cursor the client sent, oldest first — the fourth use of this house pattern,
/// after the log ring and the package-job feed.
/// </summary>
/// <param name="Enabled">False ⇒ <c>KnockBox:MetricSampleSeconds</c> is 0 and no history is being kept, so
/// the portal says so instead of drawing an empty chart that never fills.</param>
public sealed record AdminMetricHistoryResponse(
    bool Enabled,
    IReadOnlyList<AdminMetricSample> Samples,
    long LastSequence,
    int Retained,
    int Capacity,
    int SampleSeconds,
    int ProcessorCount);

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

// ── Platform limits ──────────────────────────────────────────────────────────

/// <summary>
/// The eight limits an operator may change at runtime. Values only — the labels and hints the portal
/// renders live in <c>admin-core.js</c>, the same split every other control here uses, so the wire
/// carries policy rather than presentation.
/// </summary>
public sealed record AdminLimitValues(
    double GameMessagesPerSecond,
    double GameMessagesBurst,
    double ControlMessagesPerSecond,
    double ControlMessagesBurst,
    int LobbyCreatesPerMinute,
    int MaxConnectionsPerIp,
    int MaxLobbies,
    int MaxLobbiesPerGame);

/// <summary>
/// The default limits, the ones actually in force, and which of them the operator changed.
/// </summary>
/// <param name="Defaults">The values from configuration — what "revert" reverts to. Named for what they
/// ARE to the person reading them rather than for where they come from: nobody calls a default
/// "configured".</param>
/// <param name="Effective">In force right now, i.e. the defaults with the overrides applied.</param>
/// <param name="Overridden">camelCase keys currently overridden. A key here is why its two values differ
/// — reporting the set explicitly means the portal never has to infer an override from an equality test,
/// which would call an override that happens to match the default "not overridden".</param>
/// <param name="ActiveLobbies">So the portal can warn when a new cap is already below what's running:
/// existing lobbies are never torn down by a cap, they just aren't replaced.</param>
public sealed record AdminLimitsResponse(
    AdminLimitValues Defaults,
    AdminLimitValues Effective,
    IReadOnlyList<string> Overridden,
    double HandshakeTimeoutSeconds,
    double DisconnectGraceSeconds,
    int AdminLoginAttemptsPerMinute,
    int AdminLoginAttemptsPerMinuteGlobal,
    int ActiveLobbies,
    int ConnectedPlayers);

// ── Room codes ───────────────────────────────────────────────────────────────

/// <summary>
/// The room-code blocklist, with what it costs. <paramref name="Blocked"/> is counted exactly, by walking
/// the whole code space — reporting a share of it is the difference between an operator adding one more
/// pattern and an operator quietly starving the generator.
/// </summary>
/// <param name="Unreachable">Entries that are legal but can never match, because they use characters the
/// code alphabet leaves out. Not errors — but an entry that looks like it is doing something and isn't is
/// worth saying out loud.</param>
public sealed record AdminRoomCodesResponse(
    IReadOnlyList<string> Words,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Unreachable,
    int Blocked,
    int CodeSpace,
    int MaxEntries,
    int MaxBlockedPercent,
    string Alphabet,
    int CodeLength);

// ── Update schedule ──────────────────────────────────────────────────────────

/// <summary>
/// When the scheduled marketplace check runs, and when it next will.
/// </summary>
/// <param name="Cadence">"off", "hourly", "daily" or "weekly".</param>
/// <param name="DayOfWeek">"sunday"…"saturday". Meaningful for weekly only, but always reported so the
/// portal can round-trip the form without inventing a value when the operator switches cadence.</param>
/// <param name="HourUtc">0-23. Meaningful for daily and weekly.</param>
/// <param name="Overridden">False ⇒ this is the configured default and the settings file records nothing.
/// Reported explicitly rather than inferred from an equality test, exactly like the limits form's
/// <see cref="AdminLimitsResponse.Overridden"/>.</param>
/// <param name="NextRunUtc">
/// When the next check is due, or null when checks are off. This is the field that tells an operator the
/// schedule they just saved is actually live — a form that only echoes back what they typed proves
/// nothing about whether a timer was re-armed.
/// </param>
/// <param name="Enrolled">How many games are enrolled in automatic updates. With none, a pass makes no
/// request at all, so a schedule on its own does nothing and the portal should say so.</param>
public sealed record AdminUpdateScheduleResponse(
    string Cadence,
    string DayOfWeek,
    int HourUtc,
    bool Overridden,
    string Summary,
    string? NextRunUtc,
    int Enrolled);

/// <summary>
/// Sets the schedule. A null <paramref name="Cadence"/> reverts to the configured default, the same way
/// clearing every field of the limits form does.
/// </summary>
public sealed record AdminUpdateScheduleRequest(
    string? Cadence = null,
    string? DayOfWeek = null,
    int? HourUtc = null);

// ── Announcements ────────────────────────────────────────────────────────────

/// <summary>The live announcement (or none), and who is currently connected to see one.</summary>
/// <param name="Games">Ids and names of installed games, so the scope selector can be built without a
/// second request to the catalog endpoint.</param>
public sealed record AdminAnnouncementResponse(
    string? Id,
    string? Text,
    string? Severity,
    string? GameId,
    string? PostedAt,
    int ConnectedPlayers,
    int MaxLength,
    IReadOnlyList<AdminGameName> Games);

/// <summary>A game as the announcement scope selector needs it: an id to send and a name to show.</summary>
public sealed record AdminGameName(string Id, string Name);

// ── Webhooks ─────────────────────────────────────────────────────────────────

/// <summary>One registered endpoint, with how its last delivery went.</summary>
/// <param name="Events">The events it is subscribed to. Empty means every event.</param>
/// <param name="LastStatus">HTTP status of the last attempt, or null when the request never got one
/// (DNS, TLS, timeout) or nothing has been sent yet.</param>
public sealed record AdminWebhookSummary(
    string Id,
    string Name,
    string Url,
    IReadOnlyList<string> Events,
    bool Enabled,
    string? LastAt,
    bool? LastOk,
    int? LastStatus,
    string? LastError,
    string? LastEvent);

/// <summary>
/// The endpoints and the delivery counters. Counters are cumulative since the server started, like every
/// other counter this API reports.
/// </summary>
/// <param name="Enabled">False ⇒ <c>KnockBox:WebhooksEnabled</c> is off: no dispatcher exists and every
/// mutating route refuses. The endpoints already saved are still listed, so an operator can see what would
/// resume if they turned it back on.</param>
public sealed record AdminWebhooksResponse(
    bool Enabled,
    IReadOnlyList<AdminWebhookSummary> Endpoints,
    IReadOnlyList<string> KnownEvents,
    int MaxEndpoints,
    long Delivered,
    long Failed,
    long Dropped,
    long Suppressed,
    int TimeoutSeconds,
    int ErrorsPerMinute);

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

/// <summary>
/// Switches one marketplace on or off. Nullable for the same reason as <see cref="AdminSourceRequest"/>'s
/// members: an omitted field must be refused, not read as <c>false</c> and silently disable a source.
/// </summary>
public sealed record AdminSourceEnabledRequest(bool? Enabled = null);

/// <summary>
/// The complete set of limit overrides. A full <b>replacement</b>, not a patch: a null member means "not
/// overridden, use the default", which is also how the portal reverts one field. A patch shape
/// could not tell "leave this alone" from "clear this", and those are the two things an operator does
/// most on this form.
/// </summary>
/// <summary>
/// Registers or updates a webhook endpoint. Every member nullable so a partial body leaves the rest alone,
/// rather than a defaulted <c>false</c> silently disabling one — the same shape as
/// <see cref="AdminSourceRequest"/>.
/// </summary>
public sealed record AdminWebhookRequest(
    string? Id = null,
    string? Name = null,
    string? Url = null,
    IReadOnlyList<string>? Events = null,
    bool? Enabled = null);

/// <param name="Severity">"info" or "warning". Anything else is treated as info rather than trusted into
/// a CSS class.</param>
/// <param name="GameId">Scopes the notice to one game, or null/blank for the whole platform.</param>
public sealed record AdminAnnouncementRequest(
    string? Text = null,
    string? Severity = null,
    string? GameId = null);

/// <summary>
/// Replaces the room-code blocklist wholesale. Both lists nullable so a body that omits one leaves it
/// empty rather than throwing — and, like the limits form, a full replacement so "remove this entry" needs
/// no second verb.
/// </summary>
public sealed record AdminRoomCodesRequest(
    IReadOnlyList<string>? Words = null,
    IReadOnlyList<string>? Patterns = null);

public sealed record AdminLimitsRequest(
    double? GameMessagesPerSecond = null,
    double? GameMessagesBurst = null,
    double? ControlMessagesPerSecond = null,
    double? ControlMessagesBurst = null,
    int? LobbyCreatesPerMinute = null,
    int? MaxConnectionsPerIp = null,
    int? MaxLobbies = null,
    int? MaxLobbiesPerGame = null);
