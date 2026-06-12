using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Networking;
using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Drives the REAL <see cref="WebSocketHandler"/> over fake sockets to prove the abuse-protection
/// paths: handshake deadline, protocol-version rejection, and per-connection rate limiting.
/// </summary>
public class HandshakeHardeningTests
{
    private static ServerLimits Defaults => ServerLimits.FromConfiguration(new ConfigurationBuilder().Build());

    private static WebSocketHandler NewHandler(ServerLimits limits) => new(
        new ConnectionManager(),
        new LobbyManager(),
        new GameCatalog(Path.GetTempPath(), NullLogger<GameCatalog>.Instance),
        new TokenService(new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<TokenService>.Instance),
        limits,
        TimeProvider.System,
        NullLoggerFactory.Instance,
        NullLogger<WebSocketHandler>.Instance);

    [Fact]
    public async Task A_socket_that_never_sends_a_first_frame_is_closed_at_the_handshake_deadline()
    {
        var limits = Defaults with { HandshakeTimeout = TimeSpan.FromMilliseconds(100) };
        var socket = new ScriptedSocket(hangWhenEmpty: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await NewHandler(limits).HandleAsync(socket, "http://game.local", cts.Token);

        Assert.Equal(WebSocketCloseStatus.ProtocolError, socket.OutputClosedWith);
        Assert.Equal("Handshake timeout", socket.OutputClosedReason);
    }

    [Theory]
    [InlineData("""{ "type": "Hello", "displayName": "Ann", "proto": 99 }""")]
    [InlineData("""{ "type": "Attach", "ticket": "whatever", "proto": 99 }""")]
    public async Task A_client_speaking_a_newer_protocol_is_rejected_terminally(string firstFrame)
    {
        var socket = new ScriptedSocket([Encoding.UTF8.GetBytes(firstFrame)]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await NewHandler(Defaults).HandleAsync(socket, "http://game.local", cts.Token);

        var error = Assert.IsType<ErrorMessage>(
            JsonSerializer.Deserialize<Message>(Assert.Single(socket.Sent), ConnectionManager.SerializerOptions));
        Assert.Contains("protocol", error.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.OutputClosedWith);
    }

    [Fact]
    public async Task A_pre_versioning_client_with_no_proto_field_is_accepted()
    {
        var socket = new ScriptedSocket([Encoding.UTF8.GetBytes("""{ "type": "Hello", "displayName": "Ann" }""")]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await NewHandler(Defaults).HandleAsync(socket, "http://game.local", cts.Token);

        var welcome = Assert.IsType<WelcomeMessage>(
            JsonSerializer.Deserialize<Message>(socket.Sent[0], ConnectionManager.SerializerOptions));
        Assert.Equal(KnockBoxProtocol.Version, welcome.Proto);
        Assert.Null(socket.OutputClosedWith); // graceful end-of-script close, not a rejection
    }

    [Fact]
    public async Task Control_spam_past_the_burst_gets_a_rate_limited_error_and_a_terminal_close()
    {
        // Effectively no refill during the test; the burst of 2 is the whole allowance.
        var limits = Defaults with { ControlMessagesPerSecond = 0.0001, ControlMessagesBurst = 2 };
        var frames = new List<byte[]>
        {
            ConnectionManager.Serialize(new HelloMessage(null, "Ann")),
            ConnectionManager.Serialize(new ListGamesMessage("c1")),
            ConnectionManager.Serialize(new ListGamesMessage("c2")),
            ConnectionManager.Serialize(new ListGamesMessage("c3")), // third take fails
        };
        var socket = new ScriptedSocket(frames);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await NewHandler(limits).HandleAsync(socket, "http://game.local", cts.Token);

        var received = socket.Sent
            .Select(b => JsonSerializer.Deserialize<Message>(b, ConnectionManager.SerializerOptions))
            .ToList();
        Assert.Contains(received, m => m is ErrorMessage { Reason: "rate_limited" });
        // 1008 — the SDKs treat it as terminal and stop reconnecting.
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.OutputClosedWith);
        Assert.Equal("rate_limited", socket.OutputClosedReason);
    }

    /// <summary>Replays scripted inbound frames, captures outbound frames and the close status.
    /// Optionally hangs (cancellable) when the script runs out, to exercise the handshake deadline.</summary>
    private sealed class ScriptedSocket : WebSocket
    {
        private readonly Queue<byte[]> _inbound;
        private readonly bool _hangWhenEmpty;
        private WebSocketState _state = WebSocketState.Open;

        public List<byte[]> Sent { get; } = new();
        public WebSocketCloseStatus? OutputClosedWith { get; private set; }
        public string? OutputClosedReason { get; private set; }

        public ScriptedSocket(IEnumerable<byte[]>? inbound = null, bool hangWhenEmpty = false)
        {
            _inbound = new Queue<byte[]>(inbound ?? []);
            _hangWhenEmpty = hangWhenEmpty;
        }

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            if (_inbound.Count == 0)
            {
                if (_hangWhenEmpty) await Task.Delay(Timeout.Infinite, ct);
                if (_state == WebSocketState.Open) _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }
            var msg = _inbound.Dequeue();
            msg.CopyTo(buffer.Array!, buffer.Offset);
            return new WebSocketReceiveResult(msg.Length, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            Sent.Add(buffer.ToArray());
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
        {
            _state = WebSocketState.Closed; OutputClosedWith = s; OutputClosedReason = d;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken ct)
        {
            _state = WebSocketState.Closed; OutputClosedWith = s; OutputClosedReason = d;
            return Task.CompletedTask;
        }
        public override void Dispose() { }
    }
}
