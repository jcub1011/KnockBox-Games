using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using KnockBox.Server.Admin;
using KnockBox.Server.Marketplace;

namespace KnockBox.Server.Webhooks;

/// <summary>How the last delivery to one endpoint went, for the portal.</summary>
/// <param name="Status">The HTTP status, or null when the request never got one (DNS, TLS, timeout).</param>
public sealed record WebhookResult(DateTimeOffset At, bool Ok, int? Status, string? Error, WebhookEvent Event);

/// <summary>
/// Posts platform events to the endpoints an operator registered (spec §4.2). One drain task over
/// <see cref="WebhookQueue"/>; the queue is what everything else in the server talks to, so nothing on a
/// request path ever waits for an outbound HTTP call.
/// </summary>
/// <remarks>
/// <para><b>One attempt, no retry.</b> These are notifications, not transactions: a retry policy would need
/// per-endpoint back-off state and would, at the moment an endpoint is down, turn one dead delivery into
/// several — while the interesting information (that delivery failed) is already available, because the last
/// result per endpoint is kept and shown in the portal.</para>
/// <para><b>The HTTP client is the marketplace's factory</b>, and the URL rule is the marketplace's
/// <c>IsAllowedUrl</c>. Not for convenience: a second <c>SocketsHttpHandler</c> configuration would be a
/// second thing to keep AOT-clean and a second answer to "which schemes may this server reach", and the
/// registration path already validates against that one rule.</para>
/// </remarks>
public sealed class WebhookDispatcher(
    WebhookQueue queue,
    AdminSettingsStore settings,
    HttpClient http,
    WebhookOptions options,
    TimeProvider time,
    ILogger<WebhookDispatcher> logger)
{
    /// <summary>
    /// The logger category the error sink must ignore.
    /// </summary>
    /// <remarks>
    /// The load-bearing detail of this whole feature. The error sink turns an error-level log event into a
    /// delivery; a failed delivery logs an error. Without this exclusion those two facts make a loop that
    /// grows one event per failure until the endpoint recovers — which it can't, because the server is now
    /// busy posting to it. Named here rather than in the sink so the two can't drift.
    /// </remarks>
    public static readonly string OwnLogCategory = typeof(WebhookDispatcher).FullName!;

    private readonly ConcurrentDictionary<string, WebhookResult> _lastResults = new(StringComparer.OrdinalIgnoreCase);
    private long _delivered;
    private long _failed;

    /// <summary>Deliveries that got a success status.</summary>
    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary>Deliveries that did not. Includes both a bad status and a transport failure.</summary>
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>Notifications dropped before delivery because the queue was full.</summary>
    public long Dropped => queue.Dropped;

    /// <summary>How the last delivery to this endpoint went, or null if it has never been tried.</summary>
    public WebhookResult? LastResult(string endpointId) =>
        _lastResults.TryGetValue(endpointId, out var result) ? result : null;

    /// <summary>
    /// Queues one event. Cheap and non-blocking — safe from a request handler or a log sink.
    /// </summary>
    /// <remarks>
    /// It publishes even when no endpoint is subscribed: whether anyone cares is the drain task's question,
    /// and answering it here would mean every caller taking a lock on the settings snapshot. The queue is
    /// bounded either way, so the cost of an unsubscribed event is one object that is immediately discarded.
    /// </remarks>
    public void Publish(WebhookPayload payload) => queue.Publish(payload);

    /// <summary>
    /// Drains the queue until cancellation. Started once from the composition root.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in queue.Reader.ReadAllAsync(ct))
            {
                var targets = settings.Webhooks
                    .Where(e => e.Enabled && Subscribes(e, payload.Event))
                    .ToList();
                foreach (var endpoint in targets) await DeliverAsync(endpoint, payload, ct);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            // The drain task dying silently would leave the feature switched off with no symptom. Logged
            // under this class's own category, which the error sink ignores — see OwnLogCategory.
            logger.LogError(ex, "The webhook dispatcher stopped unexpectedly; no further events will be posted.");
        }
    }

    /// <summary>
    /// Posts one payload to one endpoint, right now, and returns the result. Used by the drain loop and by
    /// the portal's "send test" button, so a test exercises the real delivery path rather than a
    /// simplified copy of it.
    /// </summary>
    public async Task<WebhookResult> DeliverAsync(WebhookEndpoint endpoint, WebhookPayload payload,
        CancellationToken ct = default)
    {
        var result = await PostAsync(endpoint, payload, ct);
        _lastResults[endpoint.Id] = result;
        if (result.Ok) Interlocked.Increment(ref _delivered);
        else
        {
            Interlocked.Increment(ref _failed);
            // Warning, not Error: a third-party endpoint being down is not a fault of this server, and at
            // Error it would be an event the error sink would try to post — to the endpoint that just failed.
            logger.LogWarning("Webhook '{Endpoint}' failed ({Status}): {Error}",
                endpoint.Id, result.Status?.ToString() ?? "no response", result.Error);
        }
        return result;
    }

    private async Task<WebhookResult> PostAsync(WebhookEndpoint endpoint, WebhookPayload payload,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.Timeout);
        try
        {
            var content = JsonContent.Create(payload, WebhookJsonContext.Default.WebhookPayload);
            using var response = await http.PostAsync(endpoint.Url, content, timeout.Token);
            var status = (int)response.StatusCode;
            return new WebhookResult(time.GetUtcNow(), response.IsSuccessStatusCode, status,
                response.IsSuccessStatusCode ? null : response.ReasonPhrase, payload.Event);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookResult(time.GetUtcNow(), false, null,
                $"No response within {options.Timeout.TotalSeconds:F0}s.", payload.Event);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            return new WebhookResult(time.GetUtcNow(), false, null, ex.Message, payload.Event);
        }
    }

    /// <summary>An endpoint with no subscription gets everything — see <see cref="WebhookEndpoint.Events"/>.</summary>
    private static bool Subscribes(WebhookEndpoint endpoint, WebhookEvent evt) =>
        endpoint.Events is null || endpoint.Events.Count == 0 || endpoint.Events.Contains(evt);

    /// <summary>
    /// Builds a payload with the two service-specific summary fields filled from one line of text.
    /// </summary>
    public static WebhookPayload Payload(WebhookEvent evt, string summary, DateTimeOffset at,
        string? title = null, string? gameId = null, string? level = null, string? detail = null)
    {
        var line = $"[KnockBox] {summary}";
        return new WebhookPayload(evt, line, line, at, $"KnockBox {Hosting.KnockBoxVersion.Current}",
            title, gameId, level, detail);
    }

    /// <summary>Whether this URL may be registered — the downloader's rule, not a second copy of it.</summary>
    public static bool IsAllowedUrl(string? url) => MarketplaceClient.IsAllowedUrl(url);
}
