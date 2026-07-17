using System.Net.WebSockets;
using KnockBox.Server.Games;
using KnockBox.Server.Games.Words;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnockBox.Server.Tests;

/// <summary>Builds the ServerAuthorityManager a WebSocketHandler needs, with quiet defaults —
/// host-authoritative flow tests just pass it through and never touch it.</summary>
internal static class TestAuthorities
{
    public static ServerAuthorityManager Manager(
        ConnectionManager connections, LobbyManager lobbies,
        string? gamesRoot = null, IConfiguration? config = null,
        TimeProvider? time = null, bool isDevelopment = false,
        IAuthorityWordService? words = null) =>
        new(gamesRoot ?? Path.GetTempPath(),
            AuthorityOptions.FromConfiguration(config ?? new ConfigurationBuilder().Build()),
            connections, lobbies, time ?? TimeProvider.System,
            words ?? new AuthorityWordService(NullLogger<AuthorityWordService>.Instance),
            isDevelopment, NullLoggerFactory.Instance);
}

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
internal sealed class FakeWebSocket(bool blockSends = false) : WebSocket
{
    private readonly TaskCompletionSource _blockForever = new();
    public List<byte[]> Sent { get; } = [];

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
        bool endOfMessage, CancellationToken cancellationToken)
    {
        if (blockSends) await _blockForever.Task.WaitAsync(cancellationToken);
        Sent.Add([.. buffer]);
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
