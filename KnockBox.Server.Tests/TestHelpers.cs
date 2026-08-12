using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
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
        IAuthorityWordService? words = null, LobbyCloser? closer = null) =>
        // The manager resolves a game's folder through a delegate (the catalog does it for real, since
        // a packaged game lives under the unpacked-package root); tests keep the simple layout.
        // It tears lobbies down through a LobbyCloser rather than the LobbyManager directly (that
        // teardown is shared with the admin portal), so build one over the manager the caller gave us —
        // callers that don't care about teardown stay unchanged.
        new(id => Path.Combine(gamesRoot ?? Path.GetTempPath(), id),
            AuthorityOptions.FromConfiguration(config ?? new ConfigurationBuilder().Build()),
            connections,
            closer ?? new LobbyCloser(lobbies, connections, NullLogger<LobbyCloser>.Instance),
            time ?? TimeProvider.System,
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
/// An <see cref="HttpMessageHandler"/> that answers from a table instead of the network, so
/// marketplace tests exercise the real <c>MarketplaceClient</c> without a socket.
/// </summary>
/// <remarks>
/// Hand-rolled for the same reason <see cref="FakeWebSocket"/> is: this project fakes its
/// collaborators directly rather than taking on WireMock or a test host, and the parts that need
/// faking here are small — a status, some headers, a body.
///
/// Bodies can be returned as a stream that throws partway through, which is the only way to test the
/// download cap and hash check against a *truncated* transfer rather than a clean one.
/// </remarks>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    /// <summary>Every request the client made, in order — lets a test assert nothing was fetched at all.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Answers <paramref name="url"/> with <paramref name="body"/>.</summary>
    public FakeHttpMessageHandler Map(
        string url, byte[] body, HttpStatusCode status = HttpStatusCode.OK,
        string? etag = null, string contentType = "application/json", long? contentLength = null)
    {
        _routes[url] = _ =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            // Overridable so a test can advertise a length that disagrees with the bytes actually
            // sent — the case that proves the cap is enforced while reading, not from the header.
            if (contentLength is { } length)
            {
                content.Headers.ContentLength = null;
                content.Headers.TryAddWithoutValidation("Content-Length", length.ToString());
            }
            var response = new HttpResponseMessage(status) { Content = content };
            if (etag is not null) response.Headers.TryAddWithoutValidation("ETag", etag);
            return response;
        };
        return this;
    }

    /// <summary>Answers <paramref name="url"/> with a text body and no content of interest.</summary>
    public FakeHttpMessageHandler Map(string url, string body, HttpStatusCode status = HttpStatusCode.OK, string? etag = null) =>
        Map(url, Encoding.UTF8.GetBytes(body), status, etag);

    /// <summary>Answers <paramref name="url"/> with a status and an empty body.</summary>
    public FakeHttpMessageHandler MapStatus(string url, HttpStatusCode status)
    {
        _routes[url] = _ => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        return this;
    }

    /// <summary>Answers <paramref name="url"/> with a body that dies after <paramref name="bytesBeforeFailure"/>.</summary>
    public FakeHttpMessageHandler MapTruncated(string url, byte[] body, int bytesBeforeFailure)
    {
        _routes[url] = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new TruncatingStream(body, bytesBeforeFailure)),
        };
        return this;
    }

    /// <summary>Answers <paramref name="url"/> with 304, as a CDN does for an unchanged catalog.</summary>
    public FakeHttpMessageHandler MapConditional(string url, byte[] body, string etag)
    {
        _routes[url] = request =>
        {
            if (request.Headers.TryGetValues("If-None-Match", out var values) && values.Contains(etag))
                return new HttpResponseMessage(HttpStatusCode.NotModified) { Content = new ByteArrayContent([]) };

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            response.Headers.TryAddWithoutValidation("ETag", etag);
            return response;
        };
        return this;
    }

    /// <summary>Makes <paramref name="url"/> fail the way an unreachable host does.</summary>
    public FakeHttpMessageHandler MapUnreachable(string url)
    {
        _routes[url] = _ => throw new HttpRequestException("No such host is known.");
        return this;
    }

    /// <summary>Makes <paramref name="url"/> hang until the caller's timeout fires.</summary>
    public FakeHttpMessageHandler MapHang(string url)
    {
        _routes[url] = _ => throw new UnreachableException("replaced in SendAsync");
        _hanging.Add(url);
        return this;
    }

    private readonly HashSet<string> _hanging = new(StringComparer.Ordinal);

    public HttpClient Client() => new(this) { Timeout = Timeout.InfiniteTimeSpan };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var url = request.RequestUri!.ToString();

        if (_hanging.Contains(url))
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }

        if (!_routes.TryGetValue(url, out var handler))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };

        return await Task.FromResult(handler(request));
    }

    /// <summary>A response body that stops mid-flight, the way a dropped connection does.</summary>
    private sealed class TruncatingStream(byte[] body, int bytesBeforeFailure) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= bytesBeforeFailure) throw new IOException("The connection was closed unexpectedly.");
            var take = Math.Min(count, Math.Min(bytesBeforeFailure, body.Length) - _position);
            Array.Copy(body, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
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

    /// <summary>True once <see cref="Abort"/> has been called — how a test checks that a teardown path
    /// actually cut the socket rather than merely stopping its send loop.</summary>
    public bool Aborted { get; private set; }

    public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;

    // Unused members for these tests.
    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;
    public override void Abort() => Aborted = true;
    public override Task CloseAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
    public override Task CloseOutputAsync(WebSocketCloseStatus s, string? d, CancellationToken c) => Task.CompletedTask;
    public override void Dispose() { }
    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> b, CancellationToken c) =>
        Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
}
