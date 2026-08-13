using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using KnockBox.Server.Marketplace;
using System.Text.Json.Serialization;
using static KnockBox.Server.Security.AdminAuthService;
using static KnockBox.Server.Security.TokenService;

namespace KnockBox.Server.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IMessage))]
[JsonSerializable(typeof(GameManifest))]
// GameManifest.AuthorityWords: a nested dictionary of records is not auto-covered by source-gen the
// way the flat ServerAuthority string is, so register both explicitly.
[JsonSerializable(typeof(AuthorityWordDeclaration))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, AuthorityWordDeclaration>))]
[JsonSerializable(typeof(TicketPayload))]
[JsonSerializable(typeof(IdentityPayload))]
[JsonSerializable(typeof(AdminSessionPayload))]
[JsonSerializable(typeof(AdminAuthStatusResponse))]
[JsonSerializable(typeof(AdminPasswordRequest))]
[JsonSerializable(typeof(AdminApiResponse))]
[JsonSerializable(typeof(AdminActionResponse))]
[JsonSerializable(typeof(AdminSystemStatusResponse))]
// The dashboard's read models. Each needs its own entry: source generation covers a registered type's
// members, so the nested element types (AdminLobbyMember, AdminLogEntry, …) are pulled in via their
// containing response — but registering the responses is what makes any of it exist at publish time.
[JsonSerializable(typeof(AdminLobbiesResponse))]
[JsonSerializable(typeof(AdminGamesResponse))]
[JsonSerializable(typeof(AdminMetricsResponse))]
[JsonSerializable(typeof(AdminLogsResponse))]
[JsonSerializable(typeof(AdminLogFilesResponse))]
// Package jobs. AdminJobSummary is listed on its own as well as via the response, because
// /admin/api/packages/jobs/{id} serializes one as the root.
[JsonSerializable(typeof(AdminJobsResponse))]
[JsonSerializable(typeof(AdminJobSummary))]
[JsonSerializable(typeof(AdminJobResponse))]
[JsonSerializable(typeof(AdminMarketplaceResponse))]
// Request bodies.
[JsonSerializable(typeof(AdminCloseLobbiesRequest))]
[JsonSerializable(typeof(AdminPurgeStaleRequest))]
[JsonSerializable(typeof(AdminKickRequest))]
[JsonSerializable(typeof(AdminAvailabilityRequest))]
[JsonSerializable(typeof(AdminMaintenanceRequest))]
[JsonSerializable(typeof(AdminRollbackRequest))]
[JsonSerializable(typeof(AdminInstallRequest))]
[JsonSerializable(typeof(AdminUpdatePolicyRequest))]
[JsonSerializable(typeof(AdminSourceRequest))]
// The roster projection handed to an authority module's init(players) (ServerAuthorityManager).
[JsonSerializable(typeof(IReadOnlyList<Player>))]
// Not a wire type, but it goes through the same source-generated serializer for the same reason:
// reflection-based JSON is not Native-AOT-safe. See docs/KBG_FORMAT.md.
[JsonSerializable(typeof(GamePackageHeader))]
// The marketplace catalog index (docs/MARKETPLACE.md). Its camelCase matches the policy above, and
// MarketplaceAuthor carries its own hand-written converter because the schema allows two shapes.
[JsonSerializable(typeof(MarketplaceCatalog))]
[JsonSerializable(typeof(MarketplacePlugin))]
[JsonSerializable(typeof(MarketplaceSource))]
[JsonSerializable(typeof(MarketplaceAuthor))]
public partial class KnockBoxProtocolContext : JsonSerializerContext { }
