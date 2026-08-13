using System.Net;
using System.Text.Json;
using KnockBox.Server.Admin;
using KnockBox.Server.Security;
using KnockBox.Server.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Outbound webhooks (spec §4.2): the payload shape that lets one POST serve Discord, Slack and a real
/// monitoring endpoint; event filtering; and the two guards that stop this feature turning an incident into
/// a bigger one — the loop break and the rate cap.
/// </summary>
public class WebhookDispatcherTests : IDisposable
{
    private const string Url = "https://hooks.example.com/abc";

    private readonly string _directory;
    private readonly FakeHttpMessageHandler _http = new();
    private readonly MutableTimeProvider _time = new(new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));

    public WebhookDispatcherTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"kb-webhooks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    private AdminSettingsStore NewStore()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_directory, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_directory, "settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        return new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
    }

    private static WebhookOptions Options(int errorsPerMinute = 6) => new(
        Enabled: true, MaxEndpoints: 8, Timeout: TimeSpan.FromSeconds(10),
        ErrorsPerMinute: errorsPerMinute, MemoryThresholdMb: 0, CpuPercentThreshold: 0);

    private (WebhookDispatcher Dispatcher, WebhookQueue Queue, AdminSettingsStore Store) Build(
        int errorsPerMinute = 6)
    {
        var queue = new WebhookQueue();
        var store = NewStore();
        var dispatcher = new WebhookDispatcher(queue, store, _http.Client(), Options(errorsPerMinute),
            _time, NullLogger<WebhookDispatcher>.Instance);
        return (dispatcher, queue, store);
    }

    private static WebhookEndpoint Endpoint(params WebhookEvent[] events) =>
        new("ops", "Ops channel", Url, events, Enabled: true);

    private async Task DrainOnce(WebhookDispatcher dispatcher, WebhookQueue queue)
    {
        // Complete() first so ReadAllAsync ends once the backlog is posted, rather than waiting forever.
        queue.Complete();
        await dispatcher.RunAsync(CancellationToken.None);
    }

    // ── Payload ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_payload_carries_the_summary_under_both_service_specific_names()
    {
        _http.MapStatus(Url, HttpStatusCode.NoContent);
        var (dispatcher, queue, store) = Build();
        store.UpsertWebhook(Endpoint());

        dispatcher.Publish(WebhookDispatcher.Payload(
            WebhookEvent.MaintenanceChanged, "Maintenance mode ON.", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        var body = await Assert.Single(_http.Requests).Content!.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        // Discord renders `content` and ignores the rest; Slack renders `text`. Carrying both is what lets
        // an operator paste either kind of URL in and have it just work, with no per-service formatting.
        Assert.Contains("Maintenance mode ON.", json.RootElement.GetProperty("content").GetString()!);
        Assert.Contains("Maintenance mode ON.", json.RootElement.GetProperty("text").GetString()!);
        // ...and the structured fields a monitoring endpoint would actually parse.
        Assert.Equal("maintenanceChanged", json.RootElement.GetProperty("event").GetString());
        Assert.StartsWith("KnockBox ", json.RootElement.GetProperty("server").GetString());
    }

    // ── Subscription ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_endpoint_only_gets_the_events_it_subscribed_to()
    {
        _http.MapStatus(Url, HttpStatusCode.OK);
        var (dispatcher, queue, store) = Build();
        store.UpsertWebhook(Endpoint(WebhookEvent.UpdateFailed));

        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "boom", _time.GetUtcNow()));
        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.UpdateFailed, "nope", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        Assert.Single(_http.Requests);
        Assert.Equal(1, dispatcher.Delivered);
    }

    [Fact]
    public async Task An_endpoint_with_no_subscription_gets_everything()
    {
        _http.MapStatus(Url, HttpStatusCode.OK);
        var (dispatcher, queue, store) = Build();
        // An empty selection almost certainly means "tell me things" rather than "tell me nothing", and a
        // registered endpoint that silently receives nothing is the worse failure of the two.
        store.UpsertWebhook(new WebhookEndpoint("ops", "Ops", Url, Events: [], Enabled: true));

        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "a", _time.GetUtcNow()));
        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.UpdateApplied, "b", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task A_disabled_endpoint_receives_nothing()
    {
        _http.MapStatus(Url, HttpStatusCode.OK);
        var (dispatcher, queue, store) = Build();
        store.UpsertWebhook(Endpoint() with { Enabled = false });

        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "a", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        Assert.Empty(_http.Requests);
    }

    // ── Failure handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_bad_status_is_recorded_against_the_endpoint_and_not_retried()
    {
        _http.MapStatus(Url, HttpStatusCode.InternalServerError);
        var (dispatcher, queue, store) = Build();
        store.UpsertWebhook(Endpoint());

        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "a", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        // One attempt: a retry policy would, at the moment an endpoint is down, turn one dead delivery into
        // several — and the useful information (it failed) is already here for the portal to show.
        Assert.Single(_http.Requests);
        Assert.Equal(1, dispatcher.Failed);
        var last = dispatcher.LastResult("ops");
        Assert.NotNull(last);
        Assert.False(last.Ok);
        Assert.Equal(500, last.Status);
    }

    [Fact]
    public async Task An_unreachable_host_is_recorded_with_no_status()
    {
        _http.MapUnreachable(Url);
        var (dispatcher, queue, store) = Build();
        store.UpsertWebhook(Endpoint());

        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "a", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        var last = dispatcher.LastResult("ops");
        Assert.NotNull(last);
        Assert.False(last.Ok);
        Assert.Null(last.Status); // never got one — DNS, TLS or timeout
        Assert.NotNull(last.Error);
    }

    [Fact]
    public async Task One_failing_endpoint_does_not_stop_another()
    {
        const string second = "https://hooks.example.com/xyz";
        _http.MapUnreachable(Url);
        _http.MapStatus(second, HttpStatusCode.OK);
        var (dispatcher, queue, store) = Build();
        store.UpsertWebhook(Endpoint());
        store.UpsertWebhook(new WebhookEndpoint("backup", "Backup", second, Events: null, Enabled: true));

        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "a", _time.GetUtcNow()));
        await DrainOnce(dispatcher, queue);

        Assert.Equal(1, dispatcher.Delivered);
        Assert.Equal(1, dispatcher.Failed);
    }

    // ── The queue ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_overfull_queue_drops_the_oldest_and_counts_it()
    {
        var queue = new WebhookQueue();
        for (var i = 0; i < WebhookQueue.Capacity + 10; i++)
            queue.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, $"event {i}", _time.GetUtcNow()));

        // Bounded and counted rather than unbounded: a queue in front of a network call is a memory leak
        // waiting for a slow endpoint, and a backlog of hundreds of alerts is not improved by buffering.
        Assert.Equal(10, queue.Dropped);
        Assert.Equal(WebhookQueue.Capacity + 10, queue.Accepted);
    }

    [Fact]
    public void Publishing_with_no_endpoints_registered_is_harmless()
    {
        var (dispatcher, _, _) = Build();
        dispatcher.Publish(WebhookDispatcher.Payload(WebhookEvent.LogError, "a", _time.GetUtcNow()));
        Assert.Empty(_http.Requests);
    }

    // ── The log sink: the two guards ──────────────────────────────────────────

    [Fact]
    public void The_error_sink_ignores_its_own_failures()
    {
        var queue = new WebhookQueue();
        var sink = (ILogEventSink)new WebhookLogSink(queue, Options(), _time);

        // THE loop guard. The dispatcher logs a failed delivery; if that log became a delivery, a dead
        // endpoint would generate one event per failure, forever, to the endpoint that is already failing.
        sink.Emit(Event(LogEventLevel.Error, "Webhook 'ops' failed", WebhookDispatcher.OwnLogCategory));
        Assert.Equal(0, queue.Accepted);

        // Anything else at Error is reported as normal.
        sink.Emit(Event(LogEventLevel.Error, "Something else broke", "KnockBox.Server.Games.GameCatalog"));
        Assert.Equal(1, queue.Accepted);
    }

    [Fact]
    public void The_error_sink_ignores_anything_below_error()
    {
        var queue = new WebhookQueue();
        var sink = (ILogEventSink)new WebhookLogSink(queue, Options(), _time);

        sink.Emit(Event(LogEventLevel.Information, "Player connected", "KnockBox"));
        sink.Emit(Event(LogEventLevel.Warning, "Slow socket", "KnockBox"));
        Assert.Equal(0, queue.Accepted);

        sink.Emit(Event(LogEventLevel.Fatal, "Down", "KnockBox"));
        Assert.Equal(1, queue.Accepted);
    }

    [Fact]
    public void An_error_storm_is_capped_and_the_next_alert_says_how_many_were_missed()
    {
        var queue = new WebhookQueue();
        var logSink = new WebhookLogSink(queue, Options(errorsPerMinute: 3), _time);
        var sink = (ILogEventSink)logSink;

        for (var i = 0; i < 50; i++) sink.Emit(Event(LogEventLevel.Error, $"boom {i}", "KnockBox"));

        // One alert saying "and 47 more" beats 50 alerts, and is the difference between notifying an
        // operator and rate-limiting them out of their own chat channel.
        Assert.Equal(3, queue.Accepted);
        Assert.Equal(47, logSink.Suppressed);

        _time.Advance(TimeSpan.FromMinutes(1));
        sink.Emit(Event(LogEventLevel.Error, "and again", "KnockBox"));
        Assert.Equal(4, queue.Accepted);

        var payloads = Drain(queue);
        Assert.Contains("+47 more suppressed", payloads[^1].Content);
    }

    [Fact]
    public void Suppressed_is_cumulative_rather_than_being_reset_by_the_alert_that_reports_it()
    {
        // The rider on an alert is a since-last-alert delta; the property is the running total the portal
        // reports. Serving both from one counter meant reading "3 suppressed" after 47 had been.
        var queue = new WebhookQueue();
        var logSink = new WebhookLogSink(queue, Options(errorsPerMinute: 1), _time);
        var sink = (ILogEventSink)logSink;

        for (var i = 0; i < 5; i++) sink.Emit(Event(LogEventLevel.Error, $"boom {i}", "KnockBox"));
        Assert.Equal(4, logSink.Suppressed);

        _time.Advance(TimeSpan.FromMinutes(1));
        sink.Emit(Event(LogEventLevel.Error, "again", "KnockBox"));      // reports and clears the delta
        _time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < 3; i++) sink.Emit(Event(LogEventLevel.Error, $"more {i}", "KnockBox"));

        Assert.Equal(6, logSink.Suppressed);   // 4 + 2, never going backwards
    }

    [Fact]
    public void Zero_errors_per_minute_means_no_alerts_rather_than_unlimited_ones()
    {
        // TokenBucket reads a non-positive rate as "limiting disabled" and lets everything through, which
        // is right for a throttle and exactly wrong here: 0 is what an operator sets to quieten this, and
        // handing them every Error event would flood the chat channel they were trying to silence.
        var queue = new WebhookQueue();
        var logSink = new WebhookLogSink(queue, Options(errorsPerMinute: 0), _time);
        var sink = (ILogEventSink)logSink;

        for (var i = 0; i < 25; i++) sink.Emit(Event(LogEventLevel.Error, $"boom {i}", "KnockBox"));

        Assert.Equal(0, queue.Accepted);
        // Not counted as suppressed either: nothing was skipped under pressure, the feature is just off.
        Assert.Equal(0, logSink.Suppressed);
    }

    [Fact]
    public void A_log_event_whose_property_throws_still_produces_an_alert()
    {
        var queue = new WebhookQueue();
        var sink = (ILogEventSink)new WebhookLogSink(queue, Options(), _time);

        sink.Emit(new LogEvent(_time.GetUtcNow(), LogEventLevel.Error, null,
            new MessageTemplateParser().Parse("Broke: {Thing}"),
            [new LogEventProperty("Thing", new ThrowingValue())]));

        // Losing the alert because a property's ToString threw would be the worst possible time to lose one.
        Assert.Equal(1, queue.Accepted);
    }

    // The queue's read side is internal and the test project is a friend assembly, so this reads what was
    // actually queued rather than inferring it from HTTP requests.
    private static List<WebhookPayload> Drain(WebhookQueue queue)
    {
        queue.Complete();
        var list = new List<WebhookPayload>();
        while (queue.Reader.TryRead(out var payload)) list.Add(payload);
        return list;
    }

    private static LogEvent Event(LogEventLevel level, string message, string category) =>
        new(DateTimeOffset.UnixEpoch, level, null,
            new MessageTemplateParser().Parse(message),
            [new LogEventProperty("SourceContext", new ScalarValue(category))]);

    private sealed class ThrowingValue : LogEventPropertyValue
    {
        public override void Render(TextWriter output, string? format = null, IFormatProvider? provider = null) =>
            throw new InvalidOperationException("nope");
    }
}
