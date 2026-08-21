using KnockBox.Server.Admin;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The schedule arithmetic behind the marketplace update check: when the next run is due, and what a
/// hand-edited or misconfigured schedule degrades to.
/// </summary>
/// <remarks>
/// Everything here is UTC by construction, so there is no daylight-saving case to cover — which is the
/// reason the schedule is UTC in the first place.
/// </remarks>
public class UpdateScheduleTests
{
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    // 2026-08-13 is a Thursday.
    private static readonly DateTimeOffset ThursdayNoon = At(2026, 8, 13, 12);

    [Fact]
    public void The_default_is_daily_at_three_utc()
    {
        Assert.Equal(UpdateCadence.Daily, UpdateSchedule.Default.Cadence);
        Assert.Equal(3, UpdateSchedule.Default.HourUtc);
    }

    [Fact]
    public void Off_is_never_due()
    {
        Assert.Null(new UpdateSchedule(UpdateCadence.Off).NextDue(ThursdayNoon));
    }

    [Fact]
    public void Hourly_is_due_at_the_next_top_of_the_hour()
    {
        var due = new UpdateSchedule(UpdateCadence.Hourly).NextDue(At(2026, 8, 13, 12, 34));

        Assert.Equal(At(2026, 8, 13, 13), due);
    }

    [Fact]
    public void Hourly_on_the_hour_moves_to_the_next_one_rather_than_returning_now()
    {
        // The scheduler re-arms from the moment it just fired. An inclusive answer would hand back that
        // same instant and the timer would spin.
        var due = new UpdateSchedule(UpdateCadence.Hourly).NextDue(At(2026, 8, 13, 13));

        Assert.Equal(At(2026, 8, 13, 14), due);
    }

    [Fact]
    public void Daily_is_due_later_today_when_the_hour_has_not_passed()
    {
        var due = new UpdateSchedule(UpdateCadence.Daily, HourUtc: 22).NextDue(ThursdayNoon);

        Assert.Equal(At(2026, 8, 13, 22), due);
    }

    [Fact]
    public void Daily_rolls_to_tomorrow_once_the_hour_has_passed()
    {
        var due = new UpdateSchedule(UpdateCadence.Daily, HourUtc: 3).NextDue(ThursdayNoon);

        Assert.Equal(At(2026, 8, 14, 3), due);
    }

    [Fact]
    public void Weekly_finds_the_next_matching_day()
    {
        var due = new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Sunday, 3).NextDue(ThursdayNoon);

        Assert.Equal(At(2026, 8, 16, 3), due);
        Assert.Equal(DayOfWeek.Sunday, due!.Value.DayOfWeek);
    }

    [Fact]
    public void Weekly_on_the_day_but_past_the_hour_waits_a_full_week()
    {
        // Same day of week, hour already gone: the answer is seven days out, not today.
        var due = new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Thursday, 3).NextDue(ThursdayNoon);

        Assert.Equal(At(2026, 8, 20, 3), due);
    }

    [Fact]
    public void Weekly_on_the_day_before_the_hour_runs_today()
    {
        var due = new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Thursday, 18).NextDue(ThursdayNoon);

        Assert.Equal(At(2026, 8, 13, 18), due);
    }

    [Fact]
    public void A_daily_run_at_midnight_crosses_the_date_boundary()
    {
        var due = new UpdateSchedule(UpdateCadence.Daily, HourUtc: 0).NextDue(At(2026, 12, 31, 23, 30));

        Assert.Equal(At(2027, 1, 1, 0), due);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    [InlineData(9999)]
    public void An_out_of_range_hour_normalizes_to_the_default(int hour)
    {
        Assert.Equal(UpdateSchedule.Default.HourUtc,
            new UpdateSchedule(UpdateCadence.Daily, HourUtc: hour).Normalize().HourUtc);
    }

    [Fact]
    public void An_out_of_range_hour_never_throws_from_NextDue()
    {
        // Reachable from a hand-edited settings file, and this runs on a timer thread where the
        // DateTimeOffset constructor's exception would have nowhere to go.
        var due = new UpdateSchedule(UpdateCadence.Daily, HourUtc: 99).NextDue(ThursdayNoon);

        Assert.Equal(At(2026, 8, 14, 3), due);
    }

    [Fact]
    public void An_undefined_cadence_or_day_normalizes_to_the_default()
    {
        var junk = new UpdateSchedule((UpdateCadence)77, (DayOfWeek)42, 5).Normalize();

        Assert.Equal(UpdateSchedule.Default.Cadence, junk.Cadence);
        Assert.Equal(UpdateSchedule.Default.DayOfWeek, junk.DayOfWeek);
        Assert.Equal(5, junk.HourUtc); // the one field that WAS usable is kept
    }

    [Fact]
    public void Configuration_seeds_the_schedule_and_ignores_unusable_values()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:MarketplaceUpdateCadence"] = "weekly",
            ["KnockBox:MarketplaceUpdateDayOfWeek"] = "Tuesday",
            ["KnockBox:MarketplaceUpdateHourUtc"] = "14",
        }).Build();

        var schedule = UpdateSchedule.FromConfiguration(config);

        Assert.Equal(UpdateCadence.Weekly, schedule.Cadence);
        Assert.Equal(DayOfWeek.Tuesday, schedule.DayOfWeek);
        Assert.Equal(14, schedule.HourUtc);
    }

    [Fact]
    public void An_unreadable_configured_cadence_falls_back_rather_than_failing_to_boot()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:MarketplaceUpdateCadence"] = "fortnightly",
            ["KnockBox:MarketplaceUpdateHourUtc"] = "44",
        }).Build();

        var schedule = UpdateSchedule.FromConfiguration(config);

        Assert.Equal(UpdateSchedule.Default.Cadence, schedule.Cadence);
        Assert.Equal(UpdateSchedule.Default.HourUtc, schedule.HourUtc);
    }
}
