using System.Text.Json;
using System.Text.Json.Serialization;
using KnockBox.Server.Games;
using KnockBox.Server.Networking;
using KnockBox.Server.Webhooks;

namespace KnockBox.Server.Admin;

/// <summary>
/// An operator's override of a game's availability. The catalog only knows whether a game was
/// discovered; this is the policy laid over that. Existing lobbies are never affected by a change
/// here — only what players may start and see.
/// </summary>
[JsonConverter(typeof(GameAvailabilityConverter))]
public enum GameAvailability
{
    /// <summary>Listed in the player catalog and startable. The default for any game with no override.</summary>
    Available,

    /// <summary>Hidden from the catalog and new lobbies are refused. Running lobbies play on.</summary>
    Disabled,

    /// <summary>
    /// Hidden from the catalog, but new lobbies are still allowed, so a direct link carrying the game
    /// id still launches it. This is a <b>visibility</b> state, not an authorization boundary: KnockBox
    /// has no player accounts, so there is no identity to check a launch against and the link is a weak
    /// secret at best. It exists to keep a game off the public grid, nothing more.
    /// </summary>
    Staged,
}

/// <summary>
/// The operator policy that has to survive a restart, persisted as JSON by
/// <see cref="AdminSettingsStore"/>. Everything else about this server is deliberately ephemeral, but
/// an admin who disables a game or opens maintenance mode means it to stay that way across the next
/// image update — re-applying policy by hand after every deploy is how a platform ships a game it
/// meant to keep hidden.
/// </summary>
/// <remarks>
/// Every member is optional and defaulted, the same discipline as <c>GamePackageHeader</c> and the
/// marketplace DTOs: this is parsed from a file an operator may have hand-edited, so a missing or
/// misspelled key must degrade to a default rather than throw.
/// </remarks>
/// <param name="OfficialSourceDisabled">
/// Whether the built-in marketplace is switched off. Recorded by its <em>absence</em> when the source is
/// enabled — the same convention <see cref="GameAvailability.Available"/> and
/// <see cref="UpdatePolicy.Manual"/> follow, so the file only ever records what an operator changed. It
/// cannot live in <c>Sources</c>: that list is the <em>extra</em> marketplaces, and the official one is
/// built from <c>MarketplaceOptions</c> rather than stored.
/// </param>
/// <param name="Authority">
/// The operator's overrides of the runtime-editable server-authority knobs (the concurrent-lobby cap and
/// the parsed-module idle window). Null when untouched, the same record-by-absence convention
/// <see cref="Limits"/> follows. Deliberately its OWN key rather than fields inside <c>Limits</c>: that
/// object means "<c>ServerLimits</c> overrides", and the two lobby caps it would collide with are
/// different caps read from different config keys.
/// </param>
/// <param name="Schedule">
/// When the scheduled update check runs. Null means the configured default
/// (<c>KnockBox:MarketplaceUpdate*</c>), the same record-by-absence convention <see cref="Limits"/>
/// follows — the file holds an object here only once an operator has actually chosen a schedule.
/// </param>
public sealed record AdminSettings(
    bool MaintenanceMode = false,
    string? MaintenanceMessage = null,
    IReadOnlyDictionary<string, GameAvailability>? Games = null,
    IReadOnlyList<RegisteredMarketplace>? Sources = null,
    IReadOnlyDictionary<string, UpdatePolicy>? Updates = null,
    OperatorLimits? Limits = null,
    BannedRoomCodes? RoomCodes = null,
    PlatformAnnouncement? Announcement = null,
    IReadOnlyList<WebhookEndpoint>? Webhooks = null,
    bool OfficialSourceDisabled = false,
    UpdateSchedule? Schedule = null,
    OperatorAuthorityOptions? Authority = null);

/// <summary>
/// An outbound endpoint the operator registered, and which events it wants (spec §4.2).
/// </summary>
/// <remarks>
/// Shaped like <see cref="RegisteredMarketplace"/> on purpose — an id that is also a route value, a name
/// for the portal, a URL validated by the downloader's own rule, and an enabled flag so an endpoint can be
/// silenced without losing its configuration. The URL is checked with
/// <c>MarketplaceClient.IsAllowedUrl</c>: HTTPS, or HTTP on loopback (which is what lets a local
/// monitoring agent work, and what CI points at).
/// </remarks>
/// <param name="Events">Which events to post. Empty means every event — an endpoint registered with no
/// subscription is far more likely to mean "tell me things" than "tell me nothing".</param>
public sealed record WebhookEndpoint(
    string Id = "",
    string Name = "",
    string Url = "",
    IReadOnlyList<WebhookEvent>? Events = null,
    bool Enabled = true);

/// <summary>
/// The operator's player-facing banner (spec §4.1), persisted like the rest of policy: an announcement
/// about a maintenance window that vanished on the next deploy would be worse than none.
/// </summary>
/// <remarks>
/// Only one is live at a time. A queue of them was considered and rejected: the shell has one banner slot,
/// and "which of the three do you mean?" is a question an operator posting a notice should never have to
/// answer. Editing is re-posting, which is why <paramref name="Id"/> changes each time — a dismissal is
/// remembered against it, so an edited notice comes back for someone who dismissed the old one.
/// </remarks>
/// <param name="Severity">"info" or "warning". Anything else reads as info rather than being trusted.</param>
/// <param name="GameId">Scopes the notice to one game, or null for the whole platform.</param>
public sealed record PlatformAnnouncement(
    string Id = "",
    string Text = "",
    DateTimeOffset PostedAt = default,
    string Severity = "info",
    string? GameId = null);

/// <summary>
/// The operator's room-code blocklist, as stored. Both lists are optional and an empty one is recorded by
/// absence; <see cref="Lobbies.RoomCodeFilter"/> is what compiles and applies them.
/// </summary>
/// <param name="Words">Blocked as a substring anywhere in a code.</param>
/// <param name="Patterns">Blocked as a whole-code glob (<c>?</c> = one character, <c>*</c> = any run).</param>
public sealed record BannedRoomCodes(
    IReadOnlyList<string>? Words = null,
    IReadOnlyList<string>? Patterns = null);

/// <summary>
/// What the server is allowed to do on its own when a marketplace offers a newer version of a game.
/// </summary>
/// <remarks>
/// Per game, and <see cref="Manual"/> by default: nothing updates itself unless an operator enrolled
/// it. The three automatic policies differ only in what they do about lobbies that are running at the
/// moment the update is found — the same three modes a manual update offers.
/// </remarks>
[JsonConverter(typeof(UpdatePolicyConverter))]
public enum UpdatePolicy
{
    /// <summary>Never updated automatically; the portal reports it and waits. The default.</summary>
    Manual,

    /// <summary>Applied on its own only while the game has no lobbies. Never interrupts anyone.</summary>
    Auto,

    /// <summary>Blocks new lobbies and applies once the running ones end on their own.</summary>
    Drain,

    /// <summary>Closes every lobby running the game, then applies. Disruptive by request.</summary>
    Force,
}

/// <summary>camelCase on the wire and in the settings file, like <see cref="GameAvailabilityConverter"/>.</summary>
public sealed class UpdatePolicyConverter()
    : JsonStringEnumConverter<UpdatePolicy>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <summary>
/// An extra marketplace an operator registered, beyond the built-in official one.
/// </summary>
/// <remarks>
/// Named <c>RegisteredMarketplace</c> rather than the more obvious <c>MarketplaceSource</c> because
/// that name is already the catalog DTO describing where one <em>plugin</em> comes from.
///
/// Only the two URLs are per-source. The byte caps and timeouts stay shared, because those are
/// operator policy about this server, not facts about a catalog.
/// </remarks>
/// <param name="Id">Stable key, also a route value: <c>^[A-Za-z0-9_-]{1,32}$</c>.</param>
/// <param name="Enabled">A disabled source is not fetched and offers nothing. The official source is
/// disable-able but never removable.</param>
public sealed record RegisteredMarketplace(
    string Id = "",
    string Name = "",
    string CatalogUrl = "",
    string DownloadBaseUrl = "",
    bool Enabled = true);

/// <summary>
/// Writes <see cref="GameAvailability"/> as a camelCase string ("disabled", not "Disabled"), so the
/// settings file an operator opens matches the values the admin API reports and accepts. Reading stays
/// case-insensitive, so a hand-edited "Disabled" still loads.
/// </summary>
/// <remarks>
/// A named subclass rather than the converter inline, because a <c>[JsonConverter]</c> attribute can only
/// name a type with a parameterless constructor — there is nowhere to pass the naming policy.
/// </remarks>
public sealed class GameAvailabilityConverter()
    : JsonStringEnumConverter<GameAvailability>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);
