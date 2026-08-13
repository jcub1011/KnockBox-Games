using KnockBox.Server.Admin;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The timer around <see cref="UpdateSchedule"/>: which schedule is in force, and that an edit re-arms
/// the current process rather than waiting for the next restart.
/// </summary>
/// <remarks>
/// The schedule arithmetic itself is <see cref="UpdateScheduleTests"/>. Nothing here waits for the timer
/// to fire — the first one is armed 30 seconds out, and what these tests are about is what
/// <see cref="UpdateScheduler.NextRun"/> and <see cref="UpdateScheduler.Current"/> say, which is also
/// what the portal reports back to the operator.
/// </remarks>
public class UpdateSchedulerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kb-sched-{Guid.NewGuid():N}");

    public UpdateSchedulerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private AdminSettingsStore NewStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_dir, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_dir, "admin-settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        return new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
    }

    /// <remarks>
    /// The coordinator's own collaborators are left null deliberately, and it is safe here: every test in
    /// this file leaves the enrolment empty, and <c>RunOnceAsync</c> returns on that check before it
    /// touches the registry, the catalog or the install engine. Building three real ones purely never to
    /// call them would be noise, and the check they'd be testing belongs to
    /// <see cref="GameUpdateCoordinatorTests"/>.
    /// </remarks>
    private UpdateScheduler New(AdminSettingsStore store, UpdateSchedule? configured = null) => new(
        new GameUpdateCoordinator(null!, null!, null!, store, NullLogger<GameUpdateCoordinator>.Instance),
        store,
        configured ?? UpdateSchedule.Default,
        TimeProvider.System,
        NullLogger<UpdateScheduler>.Instance);

    [Fact]
    public void The_configured_schedule_stands_until_an_operator_sets_one()
    {
        var configured = new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Monday, 5);
        using var scheduler = New(NewStore(), configured);

        Assert.Equal(configured, scheduler.Current);
    }

    [Fact]
    public void An_operator_schedule_wins_over_the_configured_one()
    {
        var store = NewStore();
        using var scheduler = New(store, new UpdateSchedule(UpdateCadence.Weekly, DayOfWeek.Monday, 5));

        store.SetUpdateSchedule(new UpdateSchedule(UpdateCadence.Hourly));

        Assert.Equal(UpdateCadence.Hourly, scheduler.Current.Cadence);
    }

    [Fact]
    public void Starting_schedules_a_check_shortly_after_boot()
    {
        // Not at the configured hour: an operator who just restarted a server is exactly the person who
        // wants to know whether anything is out of date, and waiting until 03:00 to find out is useless.
        using var scheduler = New(NewStore());
        var before = DateTimeOffset.UtcNow;

        scheduler.Start(CancellationToken.None);

        Assert.NotNull(scheduler.NextRun);
        Assert.InRange(scheduler.NextRun!.Value,
            before + UpdateScheduler.StartupDelay - TimeSpan.FromSeconds(5),
            DateTimeOffset.UtcNow + UpdateScheduler.StartupDelay + TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void An_edit_re_arms_this_process_rather_than_waiting_for_a_restart()
    {
        var store = NewStore();
        using var scheduler = New(store);
        scheduler.Start(CancellationToken.None);

        store.SetUpdateSchedule(new UpdateSchedule(UpdateCadence.Hourly));
        scheduler.Reschedule();

        // Within the hour plus the jitter ceiling, and strictly ahead of now.
        var now = DateTimeOffset.UtcNow;
        Assert.NotNull(scheduler.NextRun);
        Assert.InRange(scheduler.NextRun!.Value, now,
            now + TimeSpan.FromHours(1) + TimeSpan.FromSeconds(UpdateScheduler.MaxJitterSeconds));
    }

    [Fact]
    public void Switching_checks_off_leaves_nothing_scheduled()
    {
        var store = NewStore();
        using var scheduler = New(store);
        scheduler.Start(CancellationToken.None);
        Assert.NotNull(scheduler.NextRun);

        store.SetUpdateSchedule(new UpdateSchedule(UpdateCadence.Off));
        scheduler.Reschedule();

        Assert.Null(scheduler.NextRun);
    }

    [Fact]
    public void Rescheduling_before_start_does_nothing()
    {
        // The admin endpoint can be reached before Start on no realistic path, but a Reschedule that
        // quietly ARMED an unstarted scheduler would be a timer nobody disposes.
        var store = NewStore();
        using var scheduler = New(store);

        scheduler.Reschedule();

        Assert.Null(scheduler.NextRun);
    }

    [Fact]
    public void Disposing_cancels_the_pending_check()
    {
        var scheduler = New(NewStore());
        scheduler.Start(CancellationToken.None);

        scheduler.Dispose();

        Assert.Null(scheduler.NextRun);
    }

    [Fact]
    public void Starting_twice_is_harmless()
    {
        using var scheduler = New(NewStore());
        scheduler.Start(CancellationToken.None);
        var first = scheduler.NextRun;

        scheduler.Start(CancellationToken.None);

        Assert.Equal(first, scheduler.NextRun);
    }
}
