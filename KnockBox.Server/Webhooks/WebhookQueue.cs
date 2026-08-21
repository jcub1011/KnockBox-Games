using System.Threading.Channels;

namespace KnockBox.Server.Webhooks;

/// <summary>
/// The hand-off between "something happened" and "post it somewhere". A bounded channel with a single
/// reader, drained by <see cref="WebhookDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>It exists as its own object, separate from the dispatcher, for one concrete reason: the Serilog
/// sink that reports error events has to be constructed <b>before</b> <c>builder.Build()</c> — the host
/// isn't built yet when <c>UseSerilog</c> runs — while the dispatcher needs DI singletons (the settings
/// store, the HTTP client). The same split <c>AdminLogBuffer</c> solves by being <c>new</c>ed early and
/// registered later, except here the two halves have genuinely different lifetimes.</para>
/// <para><b>Drop-oldest, and counted.</b> An unbounded queue in front of a network call is a memory leak
/// waiting for a slow endpoint; blocking is worse, since the producer is a log call inside a request. So
/// the oldest pending notification is dropped and the drop is counted, which the portal reports — the same
/// policy and the same reasoning as a game socket's outbound queue.</para>
/// </remarks>
public sealed class WebhookQueue
{
    /// <summary>
    /// Pending notifications held at once. Small on purpose: these are alerts, and a backlog of hundreds
    /// means the endpoint is down, which is not a condition more buffering improves.
    /// </summary>
    public const int Capacity = 256;

    private readonly Channel<WebhookPayload> _channel;
    private long _dropped;
    private long _accepted;

    public WebhookQueue()
    {
        _channel = Channel.CreateBounded<WebhookPayload>(
            new BoundedChannelOptions(Capacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            itemDropped: _ => Interlocked.Increment(ref _dropped));
    }

    /// <summary>Notifications dropped because the queue was full. Cumulative, like every other counter here.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Notifications accepted for delivery. Not the same as delivered — see the dispatcher.</summary>
    public long Accepted => Interlocked.Read(ref _accepted);

    /// <summary>
    /// Queues one notification. Never blocks and never throws: the callers are a Serilog sink and request
    /// handlers, and neither can afford to wait on (or fail because of) an outbound alert.
    /// </summary>
    public void Publish(WebhookPayload payload)
    {
        if (_channel.Writer.TryWrite(payload)) Interlocked.Increment(ref _accepted);
    }

    /// <summary>The dispatcher's read side.</summary>
    internal ChannelReader<WebhookPayload> Reader => _channel.Reader;

    /// <summary>Stops accepting work, so a drain task can finish the backlog and end.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
