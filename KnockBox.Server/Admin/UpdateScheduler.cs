namespace KnockBox.Server.Admin;

/// <summary>
/// Arms a timer from the operator's <see cref="UpdateSchedule"/> and runs
/// <see cref="GameUpdateCoordinator.RunOnceAsync"/> when it fires.
/// </summary>
/// <remarks>
/// <para><b>A one-shot timer re-armed after every fire, not a periodic one.</b> The schedule is
/// wall-clock ("Sundays at 03:00 UTC"), and a periodic timer only ever expresses "every N since the
/// process started" — which drifts away from the chosen hour on every restart. Re-computing the next due
/// moment each time is also what lets an edit in the portal take effect without one.</para>
/// <para><b>It runs once shortly after boot.</b> An operator who has just restarted a server to pick up a
/// change is exactly the person who wants to know whether anything is out of date, and waiting until the
/// small hours to find out is not useful. It costs nothing on a default deployment: with nothing enrolled
/// <see cref="GameUpdateCoordinator.RunOnceAsync"/> returns before touching the network at all.</para>
/// <para>Every due moment carries a jitter of up to <see cref="MaxJitterSeconds"/>. A fleet restarted
/// together would otherwise reach the catalog host at exactly 03:00:00, and unlike an interval schedule
/// there is nothing else to spread them out.</para>
/// </remarks>
public sealed class UpdateScheduler : IDisposable
{
    private readonly GameUpdateCoordinator _coordinator;
    private readonly AdminSettingsStore _settings;
    private readonly UpdateSchedule _configured;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateScheduler> _logger;

    private readonly Lock _gate = new();
    private ITimer? _timer;
    private CancellationToken _stopping = CancellationToken.None;
    private bool _started;
    private bool _disposed;

    /// <summary>How long after startup the boot pass runs.</summary>
    /// <remarks>
    /// Long enough that discovery, extraction and the first precompression reconcile are out of the way —
    /// an update check that starts a download while the server is still unpacking what it already has is
    /// contending for the same disk for no reason.
    /// </remarks>
    internal static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    internal const int MaxJitterSeconds = 300;

    public UpdateScheduler(
        GameUpdateCoordinator coordinator,
        AdminSettingsStore settings,
        UpdateSchedule configured,
        TimeProvider time,
        ILogger<UpdateScheduler> logger)
    {
        _coordinator = coordinator;
        _settings = settings;
        _configured = configured.Normalize();
        _time = time;
        _logger = logger;
    }

    /// <summary>The schedule in force: the operator's if they set one, otherwise the configured default.</summary>
    public UpdateSchedule Current => _settings.UpdateSchedule ?? _configured;

    /// <summary>When the next check is due, or null when checks are off or the scheduler isn't running.</summary>
    public DateTimeOffset? NextRun { get; private set; }

    /// <summary>Starts the boot pass and the schedule. Idempotent.</summary>
    public void Start(CancellationToken stopping)
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
            _stopping = stopping;
            _logger.LogInformation("Marketplace update checks run {Schedule}; first check in {Delay}.",
                Current.Describe(), StartupDelay);
            Arm(StartupDelay, dueAt: _time.GetUtcNow() + StartupDelay);
        }
    }

    /// <summary>
    /// Re-computes the next due moment from the current schedule. Called after an operator edits it, so
    /// the change applies to this process rather than the next one.
    /// </summary>
    public void Reschedule()
    {
        lock (_gate)
        {
            if (!_started || _disposed) return;
            ArmForSchedule();
        }
    }

    // Call under _gate.
    private void ArmForSchedule()
    {
        var schedule = Current;
        var now = _time.GetUtcNow();
        if (schedule.NextDue(now) is not { } due)
        {
            _timer?.Dispose();
            _timer = null;
            NextRun = null;
            _logger.LogInformation("Marketplace update checks are off; no check is scheduled.");
            return;
        }

        due += TimeSpan.FromSeconds(Random.Shared.Next(0, MaxJitterSeconds + 1));
        Arm(due - now, due);
    }

    // Call under _gate.
    private void Arm(TimeSpan delay, DateTimeOffset dueAt)
    {
        NextRun = dueAt;
        // Period Infinite: this is a one-shot, re-armed by the callback. See the class remarks.
        if (_timer is null)
            _timer = _time.CreateTimer(_ => Fire(), null, delay, Timeout.InfiniteTimeSpan);
        else
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void Fire()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var pass = await _coordinator.RunOnceAsync(_stopping).ConfigureAwait(false);
                if (pass.Started > 0)
                    _logger.LogInformation("Scheduled marketplace check started {Started} update(s).", pass.Started);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Scheduled marketplace check failed.");
            }
            finally
            {
                // In the finally, so a failed pass still schedules the next one. A check that stops
                // happening because one of them threw is the kind of outage nobody notices for months.
                if (!_stopping.IsCancellationRequested)
                {
                    lock (_gate)
                    {
                        if (!_disposed) ArmForSchedule();
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            NextRun = null;
        }
    }
}
