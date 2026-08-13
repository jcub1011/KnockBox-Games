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
    private readonly TokenBucket _budget;
    private long _suppressed;

    public WebhookLogSink(WebhookQueue queue, WebhookOptions options, TimeProvider time)
    {
        _queue = queue;
        _time = time;
        var perMinute = Math.Max(0, options.ErrorsPerMinute);
        // Burst equal to the rate: a first spike gets through in full, then the refill paces it.
        _budget = new TokenBucket(perMinute / 60.0, perMinute, time);
    }

    /// <summary>Error events not turned into a notification because of the rate cap. Cumulative.</summary>
    public long Suppressed => Interlocked.Read(ref _suppressed);

    void ILogEventSink.Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error) return;

        var category = SourceContext(logEvent);
        // Guard 1: never report our own failures. See the class remarks.
        if (category is not null && category.StartsWith(WebhookDispatcher.OwnLogCategory, StringComparison.Ordinal))
            return;

        // Guard 2: pace it, and remember what we skipped.
        if (!_budget.TryTake())
        {
            Interlocked.Increment(ref _suppressed);
            return;
        }

        var suppressed = Interlocked.Exchange(ref _suppressed, 0);
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
