using System.Net.WebSockets;
using Microsoft.Extensions.Configuration;

namespace KnockBox.Server.Tests;

/// <summary>A TimeProvider whose "now" can be set/advanced, for deterministic expiry tests.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal static class ConfigFactory
{
    public static IConfiguration FromPairs(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();
}

/// <summary>
/// Minimal in-memory <see cref="WebSocket"/> for driving <c>Connection.SendLoopAsync</c>: it records
/// every frame written and can be told to block forever on send (to simulate a stuck socket so the
/// outbound channel fills).
/// </summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly bool _blockSends;
    private readonly TaskCompletionSource _blockForever = new();
    public List<byte[]> Sent { get; } = [];

    public FakeWebSocket(bool blockSends = false) => _blockSends = blockSends;

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (_blockSends) await _blockForever.Task.WaitAsync(cancellationToken);
        Sent.Add(buffer.ToArray());
    }

    public override WebSocketState State => WebSocketState.Open;

    // Unused members for these tests.
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() { }
    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c) =>
        Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
}
