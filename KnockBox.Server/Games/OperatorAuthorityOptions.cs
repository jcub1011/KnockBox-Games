using System.Text.Json.Serialization;

namespace KnockBox.Server.Games;

/// <summary>
/// An operator's overrides of the runtime-editable half of <see cref="AuthorityOptions"/>, edited from the
/// admin portal and persisted to <c>admin-settings.json</c>.
/// </summary>
/// <remarks>
/// <para>The sibling of <see cref="Networking.OperatorLimits"/> in every respect: every member is nullable
/// so the settings file records only what an operator actually changed, and <see cref="ApplyTo"/> lays them
/// over the configured baseline. A SEPARATE record rather than two more fields on
/// <c>OperatorLimits</c>, because that type's whole contract is <c>ApplyTo(ServerLimits)</c> — members it
/// silently ignored would make its central invariant a lie — and because
/// <see cref="AuthorityOptions.MaxLobbies"/> and <see cref="Networking.ServerLimits.MaxLobbies"/> are
/// <em>different caps read from different config keys</em>, so they can never merge.</para>
/// <para>Only two knobs are here, and the omissions are deliberate. The per-call constraints
/// (memory / timeout / statements / recursion) are baked into the <c>new Engine(...)</c> call in
/// <see cref="JsAuthorityRuntime"/>, so an "edit" would apply only to lobbies started afterwards — a knob
/// that lies about when it takes effect. <c>MaxScriptBytes</c> / <c>MaxWordFileBytes</c> are captured by
/// <see cref="GameCatalog"/>'s constructor and enforced at discovery. <c>Enabled</c>, <c>TickHzMax</c> and
/// <c>QueueCapacity</c> are read at lobby/actor construction for the same reason. The portal reports all of
/// those read-only.</para>
/// <para>Minutes on the wire, <see cref="TimeSpan"/> in the record: the portal edits a number and the
/// settings file is offered for hand-editing, so the persisted unit must be a plain integer — but every
/// consumer wants a duration. <see cref="ApplyTo"/> is where that conversion lives.</para>
/// </remarks>
public sealed record OperatorAuthorityOptions(
    int? MaxLobbies = null,
    int? ModuleCacheIdleMinutes = null)
{
    /// <summary>No overrides at all — the configured values stand. What a fresh deployment has.</summary>
    public static readonly OperatorAuthorityOptions None = new();

    /// <summary>True when nothing is overridden, so the settings file can omit the object entirely.</summary>
    /// <remarks>
    /// <c>[JsonIgnore]</c> because this record IS the persisted shape: a computed property would otherwise be
    /// written into a file an operator is invited to hand-edit, as a field that looks settable and isn't.
    /// </remarks>
    [JsonIgnore]
    public bool IsEmpty => this == None;

    /// <summary>Lays these overrides over the configured options, leaving every unset member alone.</summary>
    public AuthorityOptions ApplyTo(AuthorityOptions configured) => configured with
    {
        MaxLobbies = MaxLobbies ?? configured.MaxLobbies,
        ModuleCacheIdle = ModuleCacheIdleMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : configured.ModuleCacheIdle,
    };

    /// <summary>
    /// Why these overrides can't be accepted, or null when they can. The admin API refuses on this;
    /// <c>AdminSettingsStore</c> uses it to drop a hand-edited object rather than honour it.
    /// </summary>
    /// <remarks>
    /// <para>Takes no baseline argument, unlike <see cref="Networking.OperatorLimits.Validate"/>. That
    /// asymmetry is deliberate rather than an oversight: the dangerous combinations there are properties of
    /// the MERGED limits (a burst of zero against a configured non-zero rate refuses every message), whereas
    /// both knobs here are absolute counts with no relationship to each other or to anything configured.
    /// Carrying an unused parameter for symmetry would imply a rule that doesn't exist.</para>
    /// <para>The ceilings are absurdly high on purpose — the same reasoning as the sibling. They exist to
    /// catch a fat-fingered extra digit and a negative, not to second-guess an operator sizing their own
    /// host. Both knobs treat <b>0</b> as meaningful rather than invalid, and they mean DIFFERENT things:
    /// <c>0</c> lobbies is <em>unlimited</em> (the default), <c>0</c> minutes is <em>never evict</em>.</para>
    /// <para>Messages name the camelCase WIRE key, not the record member, because the portal surfaces them
    /// verbatim beside a field the operator is looking at.</para>
    /// </remarks>
    public string? Validate()
    {
        if (MaxLobbies is < 0 or > 100_000)
            return "authorityMaxLobbies must be between 0 and 100000 (0 = unlimited).";
        // Seven days. Past that the distinction from "never" stops being one anybody is really drawing.
        if (ModuleCacheIdleMinutes is < 0 or > 10_080)
            return "authorityModuleCacheIdleMinutes must be between 0 and 10080 (0 = keep for the process lifetime).";
        return null;
    }
}
