using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnockBox.Server.Admin;

/// <summary>How often this server looks at its marketplaces for updates to enrolled games.</summary>
/// <remarks>
/// Three rates and an off switch, rather than a free interval in minutes. An update check is a
/// housekeeping job an operator wants to happen at a quiet hour, not every N minutes from whenever the
/// process last restarted — and a number in minutes cannot express "3am", which is the thing they
/// actually want. The manual Refresh in the portal is unaffected by any of this; an operator who wants a
/// check right now presses that.
/// </remarks>
[JsonConverter(typeof(UpdateCadenceConverter))]
public enum UpdateCadence
{
    /// <summary>Never checked on a schedule. The portal still reports what is available on demand.</summary>
    Off,

    /// <summary>At the top of every hour. For a server tracking a fast-moving catalog.</summary>
    Hourly,

    /// <summary>Once a day at <see cref="UpdateSchedule.HourUtc"/>. The default.</summary>
    Daily,

    /// <summary>Once a week, on <see cref="UpdateSchedule.DayOfWeek"/> at <see cref="UpdateSchedule.HourUtc"/>.</summary>
    Weekly,
}

/// <summary>camelCase on the wire and in the settings file, like <see cref="UpdatePolicyConverter"/>.</summary>
public sealed class UpdateCadenceConverter()
    : JsonStringEnumConverter<UpdateCadence>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <summary>
/// Days as names rather than numbers, so the settings file an operator opens says <c>"sunday"</c>.
/// Integers are still accepted on read — <see cref="DayOfWeek"/> is a BCL enum and somebody's existing
/// tooling may well write one.
/// </summary>
public sealed class DayOfWeekConverter()
    : JsonStringEnumConverter<DayOfWeek>(JsonNamingPolicy.CamelCase, allowIntegerValues: true);

/// <summary>
/// When the scheduled marketplace check runs. Persisted with the rest of operator policy and editable
/// from the portal; <see cref="UpdateScheduler"/> is what arms a timer from it.
/// </summary>
/// <remarks>
/// <para>Everything here is <b>UTC</b>, deliberately. A server's local zone is a deployment accident —
/// it changes when the image moves host or the container's tzdata does — and a schedule that silently
/// shifts by an hour twice a year is worse than one an operator has to convert once.</para>
/// <para>Both fields are always present even when the cadence ignores them, so switching from daily to
/// weekly and back does not lose the hour the operator had chosen.</para>
/// </remarks>
/// <param name="HourUtc">0-23. Used by <see cref="UpdateCadence.Daily"/> and <see cref="UpdateCadence.Weekly"/>.</param>
/// <param name="DayOfWeek">Used by <see cref="UpdateCadence.Weekly"/> only.</param>
public sealed record UpdateSchedule(
    UpdateCadence Cadence = UpdateCadence.Daily,
    [property: JsonConverter(typeof(DayOfWeekConverter))]
    DayOfWeek DayOfWeek = DayOfWeek.Sunday,
    int HourUtc = 3)
{
    /// <summary>
    /// Daily at 03:00 UTC. Daily rather than hourly because a catalog changes a handful of times a year
    /// and an enrolled game updating within a day of publication is well inside what anyone expects;
    /// 03:00 because a check that finds something ends in a game being swapped, and that should land when
    /// the fewest people are playing.
    /// </summary>
    public static readonly UpdateSchedule Default = new();

    public static UpdateSchedule FromConfiguration(IConfiguration config) => new UpdateSchedule(
        Enum.TryParse<UpdateCadence>(config["KnockBox:MarketplaceUpdateCadence"], ignoreCase: true, out var cadence)
            ? cadence : Default.Cadence,
        Enum.TryParse<DayOfWeek>(config["KnockBox:MarketplaceUpdateDayOfWeek"], ignoreCase: true, out var day)
            ? day : Default.DayOfWeek,
        config.GetValue("KnockBox:MarketplaceUpdateHourUtc", Default.HourUtc)).Normalize();

    /// <summary>
    /// This schedule with any out-of-range field replaced by the default. Applied on load and on save, so
    /// nothing downstream has to defend against an hour of 25 — including <see cref="NextDue"/>, whose
    /// <see cref="DateTimeOffset"/> constructor would throw on one.
    /// </summary>
    public UpdateSchedule Normalize() => this with
    {
        Cadence = Enum.IsDefined(Cadence) ? Cadence : Default.Cadence,
        DayOfWeek = Enum.IsDefined(DayOfWeek) ? DayOfWeek : Default.DayOfWeek,
        HourUtc = HourUtc is >= 0 and <= 23 ? HourUtc : Default.HourUtc,
    };

    /// <summary>
    /// The next moment this schedule is due, or null when it is <see cref="UpdateCadence.Off"/>.
    /// </summary>
    /// <remarks>
    /// <b>Strictly after</b> <paramref name="after"/>, never equal to it. The scheduler re-arms from the
    /// moment it just fired, so an inclusive answer would return that same instant and spin.
    /// </remarks>
    public DateTimeOffset? NextDue(DateTimeOffset after)
    {
        var now = after.ToUniversalTime();
        // Normalized rather than trusted: this is reachable from a hand-edited settings file, and the
        // DateTimeOffset constructor below turns an out-of-range hour into an exception on a timer thread.
        // Through Normalize() rather than a local clamp so there is ONE answer to "what does hour 99 mean"
        // — a clamp here would run at 23:00 while the value the portal reports back said 03:00.
        var (cadence, day, hour) = Normalize();
        var midnight = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        switch (cadence)
        {
            case UpdateCadence.Off:
                return null;

            case UpdateCadence.Hourly:
                // The floor of the current hour is at or before `now`, so one hour on is always after it.
                return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero)
                    .AddHours(1);

            case UpdateCadence.Weekly:
            {
                var due = midnight.AddHours(hour).AddDays(((int)day - (int)now.DayOfWeek + 7) % 7);
                return due > now ? due : due.AddDays(7);
            }

            default: // Daily
            {
                var due = midnight.AddHours(hour);
                return due > now ? due : due.AddDays(1);
            }
        }
    }

    /// <summary>How the portal and the logs describe this schedule in one phrase.</summary>
    /// <remarks>Normalized first, so what it says is what <see cref="NextDue"/> will actually do.</remarks>
    public string Describe()
    {
        var (cadence, day, hour) = Normalize();
        return cadence switch
        {
            UpdateCadence.Off => "never (scheduled checks are off)",
            UpdateCadence.Hourly => "hourly, on the hour",
            UpdateCadence.Weekly => $"weekly, {day}s at {hour:00}:00 UTC",
            _ => $"daily at {hour:00}:00 UTC",
        };
    }
}
