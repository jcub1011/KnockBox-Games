using System.Text.Json;
using System.Text.Json.Serialization;

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
public sealed record AdminSettings(
    bool MaintenanceMode = false,
    string? MaintenanceMessage = null,
    IReadOnlyDictionary<string, GameAvailability>? Games = null);

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
