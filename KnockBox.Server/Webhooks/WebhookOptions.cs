namespace KnockBox.Server.Webhooks;

/// <summary>
/// Operator policy for outbound webhooks (<c>KnockBox:Webhook*</c>), read once at startup like
/// <c>ServerLimits</c> and <c>MarketplaceOptions</c>.
/// </summary>
/// <param name="Enabled">Off ⇒ no dispatcher, no drain task, no HTTP client, and the admin routes refuse
/// with a message naming this key. The same posture <c>MarketplaceEnabled</c> takes, for the same reason:
/// an air-gapped deployment should be able to prove nothing reaches out.</param>
/// <param name="MaxEndpoints">How many endpoints may be registered.</param>
/// <param name="Timeout">Per-delivery deadline. Short: this is a notification, and a slow endpoint must not
/// hold the drain task while the queue behind it fills and starts dropping alerts.</param>
/// <param name="ErrorsPerMinute">Cap on error-log events turned into deliveries, or <c>0</c> to send no
/// error alerts at all. Note that <c>0</c> means OFF here rather than "unlimited" as it does for the
/// connection rate limits: this knob gates outbound traffic to someone else's chat channel, so the value an
/// operator reaches for to quieten it must not be the one that floods it. An error storm is exactly when
/// this feature is most likely to fire and least likely to be useful per-message — see
/// <see cref="WebhookLogSink"/>.</param>
/// <param name="MemoryThresholdMb">Working set that counts as a breach, or 0 to not watch memory.</param>
/// <param name="CpuPercentThreshold">Process CPU (percent of one core-equivalent) that counts as a breach,
/// or 0 to not watch CPU.</param>
public sealed record WebhookOptions(
    bool Enabled,
    int MaxEndpoints,
    TimeSpan Timeout,
    int ErrorsPerMinute,
    int MemoryThresholdMb,
    int CpuPercentThreshold)
{
    public static WebhookOptions FromConfiguration(IConfiguration config) => new(
        config.GetValue("KnockBox:WebhooksEnabled", true),
        config.GetValue("KnockBox:MaxWebhooks", 8),
        TimeSpan.FromSeconds(config.GetValue("KnockBox:WebhookTimeoutSeconds", 10)),
        // Six a minute is enough to notice a problem and far too few to flood a Discord channel. The
        // suppressed count rides the next delivery, so the operator learns there were more.
        config.GetValue("KnockBox:WebhookErrorsPerMinute", 6),
        // Both off by default: a threshold that fits one host is noise on another, and an alert nobody
        // configured is an alert nobody trusts.
        config.GetValue("KnockBox:WebhookMemoryThresholdMb", 0),
        config.GetValue("KnockBox:WebhookCpuPercentThreshold", 0));
}
