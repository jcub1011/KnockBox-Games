using KnockBox.Server.Networking;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace KnockBox.Server.Webhooks;

/// <summary>
/// Turns error-and-worse log events into webhook notifications. A hand-written
/// <see cref="ILogEventSink"/>, wired alongside <c>AdminLogBuffer</c> in the Serilog setup.
/// </summary>
/// <remarks>
/// <para>A <b>second sink</b> rather than a hook on <c>AdminLogBuffer</c>: that class's contract is "the
/// live view", and bolting an egress side-effect onto it would make one class answer to two owners. This
/// also keeps the loop guard local to the thing that needs it.</para>
/// <para><b>Two guards, both necessary:</b></para>
/// <list type="number">
/// <item>Events from <see cref="WebhookDispatcher"/>'s own category are ignored. A failed delivery logs;
/// if that log became a delivery, a dead endpoint would generate an event per failure forever. (The
/// dispatcher also logs failures at Warning rather than Error, so this guard is the second line, not the
/// only one.)</item>
/// <item>A token bucket caps deliveries per minute, and the number suppressed rides the next one. An error
/// storm is exactly when this fires most and is worth least per message — one alert saying "and 340 more"
/// is strictly more useful than 341 alerts, and it is the difference between notifying an operator and
/// rate-limiting them out of their own chat channel.</item>
/// </list>
/// <para><see cref="Emit"/> only formats a string and writes to a bounded channel. Serilog calls it on
/// whatever thread logged, including inside a request: a sink that blocks is a sink that stalls the server.</para>
/// </remarks>
public sealed class WebhookLogSink : ILogEventSink
{
    // Same formatter and reasoning as AdminLogBuffer: Serilog's own RenderMessage() quotes string
    // properties, so the alert and the log file would disagree about the same event.
    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}", null);

    private const int MaxTextLength = 900;

    private readonly WebhookQueue _queue;
    private readonly TimeProvider _time;
    private readonly TokenBucket? _budget;
    private long _suppressed;
    private long _suppressedSinceLastAlert;

    public WebhookLogSink(WebhookQueue queue, WebhookOptions options, TimeProvider time)
    {
        _queue = queue;
        _time = time;
        // A rate of zero means NO error alerts, not unbounded ones. TokenBucket treats a non-positive
        // rate as "limiting disabled" and lets everything through, which is the right reading for a
        // *throttle* and exactly the wrong one here: an operator setting this to 0 is muting the
        // feature, and handing them every Error event in the process would flood the very channel they
        // were trying to quieten. So the null bucket is the mute, and Emit checks for it.
        _budget = options.ErrorsPerMinute > 0
            // Burst equal to the rate: a first spike gets through in full, then the refill paces it.
            ? new TokenBucket(options.ErrorsPerMinute / 60.0, options.ErrorsPerMinute, time)
            : null;
    }

    /// <summary>
    /// Error events not turned into a notification because of the rate cap. Cumulative — it never
    /// decreases, so the portal can report it as a total. The rider on the next alert uses a separate
    /// since-last-alert counter.
    /// </summary>
    public long Suppressed => Interlocked.Read(ref _suppressed);

    void ILogEventSink.Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error) return;
        // Muted (ErrorsPerMinute <= 0). Not counted as suppressed: nothing was skipped because of
        // pressure, the feature is simply off.
        if (_budget is null) return;

        var category = SourceContext(logEvent);
        // Guard 1: never report our own failures. See the class remarks.
        if (category is not null && category.StartsWith(WebhookDispatcher.OwnLogCategory, StringComparison.Ordinal))
            return;

        // Guard 2: pace it, and remember what we skipped.
        if (!_budget.TryTake())
        {
            Interlocked.Increment(ref _suppressed);
            Interlocked.Increment(ref _suppressedSinceLastAlert);
            return;
        }

        var suppressed = Interlocked.Exchange(ref _suppressedSinceLastAlert, 0);
        var message = Render(logEvent);
        var summary = suppressed > 0
            ? $"{logEvent.Level}: {message} (+{suppressed} more suppressed)"
            : $"{logEvent.Level}: {message}";

        _queue.Publish(WebhookDispatcher.Payload(
            WebhookEvent.LogError,
            summary,
            _time.GetUtcNow(),
            title: category,
            level: logEvent.Level.ToString(),
            detail: logEvent.Exception?.GetType().Name));
    }

    private static string? SourceContext(LogEvent logEvent) =>
        logEvent.Properties.TryGetValue("SourceContext", out var value)
            ? value.ToString().Trim('"')
            : null;

    private static string Render(LogEvent logEvent)
    {
        try
        {
            using var writer = new StringWriter();
            MessageFormatter.Format(logEvent, writer);
            var text = writer.ToString();
            return text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        }
        catch (Exception ex)
        {
            // A property whose ToString throws must not take down the logging call that reported it — the
            // same defence AdminLogBuffer takes, and here it would also mean losing the alert.
            return $"(could not render log message: {ex.GetType().Name})";
        }
    }
}
