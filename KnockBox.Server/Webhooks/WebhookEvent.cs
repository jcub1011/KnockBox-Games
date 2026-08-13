using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnockBox.Server.Webhooks;

/// <summary>
/// The platform events an operator can have posted to an outbound endpoint (spec §4.2).
/// </summary>
/// <remarks>
/// Deliberately few, and every one of them is something an operator would want to hear about at 3am. A
/// firehose of "lobby created" would be a different feature with a different design (and would need
/// batching, ordering and back-pressure guarantees this does not attempt).
/// </remarks>
[JsonConverter(typeof(WebhookEventConverter))]
public enum WebhookEvent
{
    /// <summary>An error-or-worse log event. Rate-limited; see <c>WebhookLogSink</c>.</summary>
    LogError,

    /// <summary>A game finished installing or updating.</summary>
    UpdateApplied,

    /// <summary>An install, update or rollback failed.</summary>
    UpdateFailed,

    /// <summary>Global maintenance mode was turned on or off.</summary>
    MaintenanceChanged,

    /// <summary>Memory or CPU crossed the configured threshold, or came back under it.</summary>
    ResourceThreshold,
}

/// <summary>camelCase on the wire and in the settings file, like <c>GameAvailabilityConverter</c>.</summary>
public sealed class WebhookEventConverter()
    : JsonStringEnumConverter<WebhookEvent>(JsonNamingPolicy.CamelCase, allowIntegerValues: false);

/// <summary>
/// One thing that happened, ready to be posted. Built by whoever noticed it; the dispatcher decides which
/// endpoints care.
/// </summary>
/// <param name="Content">A one-line human summary. <b>The field name matters:</b> a Discord webhook renders
/// <c>content</c> and ignores everything else, so naming it this makes the same payload work with a Discord
/// URL, a Slack URL (see <paramref name="Text"/>) and a real monitoring endpoint, with no per-service
/// formatting in the server. That is the whole reason this shape looks slightly redundant.</param>
/// <param name="Text">The same summary under Slack's field name, for the same reason.</param>
public sealed record WebhookPayload(
    WebhookEvent Event,
    string Content,
    string Text,
    DateTimeOffset At,
    string Server,
    string? Title = null,
    string? GameId = null,
    string? Level = null,
    string? Detail = null);

/// <summary>Source-generated serializer for the webhook payload. Native-AOT-safe, like everything else here.</summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WebhookPayload))]
public partial class WebhookJsonContext : JsonSerializerContext { }
