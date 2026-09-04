using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KnockBox.Contracts;
using KnockBox.Server.Games.Blobs;
using KnockBox.Server.Hosting;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The blob side channel's HTTP surface: the three auth checks, the status mapping, and the middleware's
/// dispatch.
/// </summary>
/// <remarks>
/// <para><b>There is no test host in this repo</b> — no <c>WebApplicationFactory</c>, no
/// <c>TestServer</c>, and <c>Microsoft.AspNetCore.Mvc.Testing</c> is not referenced, because composing
/// the real pipeline needs thirty-odd dependencies and the Docker CI job is the only place a real
/// listener runs. So this file uses the two shapes that <em>are</em> house patterns:
/// <see cref="BlobApi.AuthRefusal"/> tested as a pure function with no <see cref="HttpContext"/> at all
/// (what <c>AdminWriteGuardTests</c> does for <c>WriteGuardRefusal</c>), and
/// <see cref="DefaultHttpContext"/> for the middleware (what <c>GameAssetNegotiationTests</c> does for
/// encoding negotiation).</para>
/// <para>That split is also the reason <see cref="BlobApi.AuthRefusal"/> takes facts rather than a
/// <c>TokenService</c> and a <c>LobbyManager</c>. It is a security decision, and one this server has
/// already had wrong in a way nothing but a real request would have shown — so it is written as
/// something a test can enumerate the cases of rather than something a test has to build a lobby to
/// reach.</para>
/// </remarks>
public class BlobApiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-blobapi-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly LobbyManager _lobbies;
    private readonly TokenService _tokens;
    private readonly BlobOptionsProvider _options;
    private readonly BlobStore _store;
    private readonly BlobApi.Options _api;
    private readonly Lobby _lobby;

    private const string GameId = "dnd-mapper";
    private const string PlayerId = "dm";

    public BlobApiTests()
    {
        Directory.CreateDirectory(_root);
        _lobbies = new LobbyManager(_clock);
        _tokens = new TokenService(
            new ConfigurationBuilder().Build(), _clock, NullLogger<TokenService>.Instance);
        _options = new BlobOptionsProvider(BlobOptions.Default with { Root = _root });
        _store = new BlobStore(_options, _clock, NullLogger<BlobStore>.Instance);
        _api = new BlobApi.Options(_store, _options, _tokens, _lobbies, NullLogger<BlobApiTests>.Instance);

        Assert.True(_lobbies.TryCreate(GameId, PlayerId, 6, out var lobby));
        Assert.True(lobby.TryAdd(new Player(PlayerId, "The DM")));
        _lobby = lobby;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── The three auth checks, as a pure function ────────────────────────────────────────────────────

    [Fact]
    public void An_unverifiable_ticket_is_unauthorized_rather_than_forbidden()
    {
        // 401 invites the SDK to ask the shell for a fresh ticket; 403 tells it not to bother. Getting
        // these the wrong way round turns an expired ticket into a permanent failure.
        var refusal = BlobApi.AuthRefusal(
            ticketVerified: false, lobbyGameId: GameId, lobbyExists: true, playerIsMember: true,
            ticketGameId: GameId);

        Assert.Equal(StatusCodes.Status401Unauthorized, refusal!.Value.Status);
        Assert.Contains(BlobApi.TicketHeader, refusal.Value.Error);
    }

    [Fact]
    public void A_signed_ticket_for_a_lobby_that_is_gone_is_refused()
    {
        // The signature check alone is not this server's bar. A ticket is meant to survive a reconnect,
        // so its own validity says nothing about whether the session it names still exists.
        var refusal = BlobApi.AuthRefusal(
            ticketVerified: true, lobbyGameId: null, lobbyExists: false, playerIsMember: false,
            ticketGameId: GameId);

        Assert.Equal(StatusCodes.Status403Forbidden, refusal!.Value.Status);
    }

    [Fact]
    public void A_signed_ticket_from_someone_who_has_left_the_lobby_is_refused()
    {
        // The live-membership check is the PRIMARY control, with the ticket's expiry as defence in
        // depth — exactly the ordering WebSocketHandler.RunDataAsync documents. Without it a kicked
        // player keeps write access for the rest of the ticket's twelve hours.
        var refusal = BlobApi.AuthRefusal(
            ticketVerified: true, lobbyGameId: GameId, lobbyExists: true, playerIsMember: false,
            ticketGameId: GameId);

        Assert.Equal(StatusCodes.Status403Forbidden, refusal!.Value.Status);
    }

    [Fact]
    public void A_ticket_scoped_to_a_different_game_is_refused()
    {
        var refusal = BlobApi.AuthRefusal(
            ticketVerified: true, lobbyGameId: GameId, lobbyExists: true, playerIsMember: true,
            ticketGameId: "word-game");

        Assert.Equal(StatusCodes.Status403Forbidden, refusal!.Value.Status);
    }

    [Fact]
    public void A_live_member_of_the_right_game_is_allowed()
    {
        Assert.Null(BlobApi.AuthRefusal(
            ticketVerified: true, lobbyGameId: GameId, lobbyExists: true, playerIsMember: true,
            ticketGameId: GameId));

        // Game ids are OrdinalIgnoreCase everywhere else in this server, and a comparison that was
        // ordinal here would refuse a ticket the relay accepts — the same request working over /ws and
        // failing over /blob is a bug report nobody would think to file against a string comparison.
        Assert.Null(BlobApi.AuthRefusal(
            ticketVerified: true, lobbyGameId: "DND-Mapper", lobbyExists: true, playerIsMember: true,
            ticketGameId: GameId));
    }

    // ── The status mapping ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BlobIngestOutcome.Stored, StatusCodes.Status200OK)]
    [InlineData(BlobIngestOutcome.AlreadyPresent, StatusCodes.Status200OK)]
    [InlineData(BlobIngestOutcome.Disabled, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(BlobIngestOutcome.HashRejected, StatusCodes.Status400BadRequest)]
    [InlineData(BlobIngestOutcome.HashMismatch, StatusCodes.Status400BadRequest)]
    [InlineData(BlobIngestOutcome.TooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(BlobIngestOutcome.LobbyQuotaExceeded, StatusCodes.Status507InsufficientStorage)]
    [InlineData(BlobIngestOutcome.TotalQuotaExceeded, StatusCodes.Status507InsufficientStorage)]
    [InlineData(BlobIngestOutcome.TooManyUploads, StatusCodes.Status429TooManyRequests)]
    public void An_upload_outcome_maps_to_a_status_a_client_can_act_on(
        BlobIngestOutcome outcome, int expected)
    {
        // The distinction that matters to an SDK: 400 means "your bytes are wrong, do not retry", 507
        // means "release something and try again", 429 means "wait for your own upload to finish". Fold
        // any two of those together and the client either retries forever or gives up too early.
        Assert.Equal(expected, BlobApi.StatusFor(outcome));
    }

    [Fact]
    public void Every_upload_outcome_has_a_deliberate_status()
    {
        // The switch has a `_ => 500` arm, so a new outcome added later compiles and silently reports an
        // internal error for something that is not one. This is the test that notices.
        foreach (var outcome in Enum.GetValues<BlobIngestOutcome>())
        {
            if (outcome == BlobIngestOutcome.WriteFailed) continue; // genuinely a 500
            Assert.NotEqual(StatusCodes.Status500InternalServerError, BlobApi.StatusFor(outcome));
        }
    }

    [Fact]
    public void Registering_against_content_that_is_not_here_yet_is_a_conflict()
    {
        // 409, not 404. The id is well-formed and the route exists; the content simply has not been
        // uploaded, which is a conflict with current state and tells the SDK to PUT first.
        Assert.Equal(StatusCodes.Status409Conflict,
            BlobApi.StatusFor(BlobRegisterOutcome.UnknownHash));
        Assert.Equal(StatusCodes.Status507InsufficientStorage,
            BlobApi.StatusFor(BlobRegisterOutcome.LobbyQuotaExceeded));
        Assert.Equal(StatusCodes.Status400BadRequest,
            BlobApi.StatusFor(BlobRegisterOutcome.LogicalIdRejected));
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_path_outside_the_blob_prefix_is_passed_through_untouched()
    {
        var ctx = Request("GET", "/games/dnd-mapper/index.html");
        var passedThrough = await Run(ctx);

        Assert.True(passedThrough, "the middleware must be invisible to every other request on the origin");
        Assert.Equal("/games/dnd-mapper/index.html", ctx.Request.Path.Value);
    }

    [Fact]
    public async Task A_recognised_path_with_the_wrong_method_is_not_found()
    {
        Assert.False(await Run(Request("PATCH", $"{BlobApi.RoutePrefix}/{await Store("art")}")));
        var ctx = Request("PATCH", $"{BlobApi.RoutePrefix}/register");
        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    // ── Reads: anonymous, and the URL is the capability ──────────────────────────────────────────────

    [Fact]
    public async Task A_valid_read_token_is_rewritten_onto_the_static_mount_with_no_auth_at_all()
    {
        var hash = await Store("a battlemap", "image/png");
        var url = _store.Register(_lobby.Id, GameId, "map", hash, "image/png").Token;

        // No ticket header anywhere in this request: <img src> cannot attach one, which is the entire
        // reason reads are keyed on a MAC of the hash instead of gated by a header.
        var ctx = Request("GET", $"{BlobApi.RoutePrefix}/{url}");
        var passedThrough = await Run(ctx);

        Assert.True(passedThrough, "the middleware hands off to StaticFileMiddleware rather than serving");
        Assert.Equal($"{BlobApi.RoutePrefix}/{hash[..2]}/{hash}", ctx.Request.Path.Value);
        // Stashed for the mount's OnPrepareResponse, which is the only place the content type can be
        // corrected once the static middleware has decided the response.
        Assert.Equal("image/png", ctx.Items[BlobApi.ContentTypeItem]);
    }

    [Fact]
    public async Task A_forged_read_token_is_not_found_and_never_reaches_the_file_mount()
    {
        var hash = await Store("secret map");
        _store.Register(_lobby.Id, GameId, "map", hash);

        var ctx = Request("GET", $"{BlobApi.RoutePrefix}/{hash}.{new string('0', 32)}");
        var passedThrough = await Run(ctx);

        Assert.False(passedThrough, "a bad token must terminate here, not fall through to a file provider");
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task A_bare_hash_is_not_a_readable_url()
    {
        // The oracle this design has to close: a hash is derived from the bytes, so anyone owning the
        // same map pack can compute it. If GET by bare hash ever worked, owning a popular battlemap
        // would let a player enumerate which of them their DM had uploaded.
        var hash = await Store("secret map");
        _store.Register(_lobby.Id, GameId, "map", hash);

        var ctx = Request("GET", $"{BlobApi.RoutePrefix}/{hash}");
        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    // ── The probe: authenticated, because one bit is enough to leak ──────────────────────────────────

    [Fact]
    public async Task An_unauthenticated_probe_is_refused_with_no_body()
    {
        var ctx = Request("HEAD", $"{BlobApi.RoutePrefix}/{await Store("art")}");
        Assert.False(await Run(ctx));

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        // A HEAD response must carry no body, refusal included.
        Assert.Equal(0, ((MemoryStream)ctx.Response.Body).Length);
    }

    [Fact]
    public async Task An_authenticated_probe_answers_whether_the_bytes_are_here()
    {
        var present = await Store("uploaded");
        var absent = HashOf("never uploaded");

        var found = Request("HEAD", $"{BlobApi.RoutePrefix}/{present}", ticket: Ticket());
        Assert.False(await Run(found));
        Assert.Equal(StatusCodes.Status200OK, found.Response.StatusCode);

        var missing = Request("HEAD", $"{BlobApi.RoutePrefix}/{absent}", ticket: Ticket());
        Assert.False(await Run(missing));
        Assert.Equal(StatusCodes.Status404NotFound, missing.Response.StatusCode);
    }

    [Fact]
    public async Task A_ticket_is_accepted_as_a_bearer_token_too()
    {
        var hash = await Store("art");
        var ctx = Request("HEAD", $"{BlobApi.RoutePrefix}/{hash}");
        ctx.Request.Headers.Authorization = $"Bearer {Ticket()}";

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task An_authenticated_probe_touches_and_extends_grace_window()
    {
        var hash = await Store("probe-touched");
        var path = Path.Combine(_root, hash[..2], hash);

        // Advance time near end of grace
        _clock.Advance(_options.Current.Grace - TimeSpan.FromMinutes(1));

        // Authenticated probe touches the blob
        var found = Request("HEAD", $"{BlobApi.RoutePrefix}/{hash}", ticket: Ticket());
        Assert.False(await Run(found));
        Assert.Equal(StatusCodes.Status200OK, found.Response.StatusCode);

        // Advance past original grace window
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Empty(_store.Sweep());
        Assert.True(File.Exists(path), "probe should have touched blob to extend grace");
    }

    // ── Uploads ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unauthenticated_upload_is_refused_with_a_reason_the_game_can_show()
    {
        var bytes = Encoding.UTF8.GetBytes("art");
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{HashOf(bytes)}", body: bytes);

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);

        var response = ReadResponse(ctx);
        Assert.False(response.Ok);
        Assert.NotNull(response.Error);
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_authenticated_upload_stores_the_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("a battlemap");
        var hash = HashOf(bytes);
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{hash}", body: bytes, ticket: Ticket());
        ctx.Request.ContentType = "image/png";

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(bytes.Length, ReadResponse(ctx).Bytes);
        Assert.True(_store.Has(hash));
    }

    [Fact]
    public async Task An_upload_whose_bytes_do_not_match_its_url_is_refused_and_leaves_nothing_behind()
    {
        var declared = HashOf("the map everyone has");
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{declared}",
            body: Encoding.UTF8.GetBytes("something else"), ticket: Ticket());

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.False(_store.Has(declared));
        // The staging file included: a refusal that leaves a .part behind fills the disk on retry.
        Assert.Empty(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task An_honestly_declared_oversize_body_is_refused_in_one_round_trip()
    {
        _options.Apply(new OperatorBlobOptions(MaxBlobBytes: 8));
        var bytes = Encoding.UTF8.GetBytes("far more than eight bytes");
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{HashOf(bytes)}", body: bytes, ticket: Ticket());

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, ctx.Response.StatusCode);
        // Refused on the header, so not one byte of the body was read. The streaming cap is still the
        // real enforcement — Content-Length is the client's claim — but there is no reason to read
        // 100 MB to learn something the first packet already said.
        Assert.Equal(0, ctx.Request.Body.Position);
    }

    [Fact]
    public async Task The_upload_route_raises_the_hosts_body_cap_off_the_blob_limit()
    {
        // Kestrel's default is 30,000,000 bytes and nothing else in this server overrides it, so
        // without this a 100 MB blob is refused by the host before BlobMaxBytes is consulted at all —
        // and the error names a limit the operator never configured.
        var feature = new FakeBodySizeFeature();
        var bytes = Encoding.UTF8.GetBytes("art");
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{HashOf(bytes)}", body: bytes, ticket: Ticket());
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        await Run(ctx);
        Assert.Equal(BlobOptions.DefaultMaxBlobBytes + 4096, feature.MaxRequestBodySize);
    }

    [Fact]
    public async Task A_cap_of_zero_lifts_the_hosts_body_cap_rather_than_flooring_it()
    {
        // Read literally, a documented "no limit" of 0 set a 4096-byte Kestrel cap and refused every
        // upload while complaining about a 0-byte limit. That happened twice in this repo with
        // MaxPackageBytes, which is why every enforcement point gets its own assertion.
        _options.Apply(new OperatorBlobOptions(MaxBlobBytes: 0));
        var feature = new FakeBodySizeFeature();
        var bytes = Encoding.UTF8.GetBytes("art");
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{HashOf(bytes)}", body: bytes, ticket: Ticket());
        ctx.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        await Run(ctx);
        Assert.Null(feature.MaxRequestBodySize);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task An_upload_of_content_the_server_already_has_succeeds_and_drains_the_body()
    {
        var bytes = Encoding.UTF8.GetBytes("shared art");
        var hash = await Store("shared art");

        // This is the lost-HEAD-probe race: the client asked, was told no, and someone else uploaded the
        // same bytes in between. Answering 200 is what makes the client's probe-then-skip flow robust.
        var ctx = Request("PUT", $"{BlobApi.RoutePrefix}/{hash}", body: bytes, ticket: Ticket());
        Assert.False(await Run(ctx));

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.True(ReadResponse(ctx).Ok);
        // Drained rather than reset. A browser cannot use Expect: 100-continue, so the SDK's body is
        // already on the wire by the time we know we do not want it, and responding without reading it
        // produces client-visible broken pipes behind some proxies.
        Assert.Equal(bytes.Length, ctx.Request.Body.Position);
    }

    // ── Register and unregister ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_returns_a_headerless_url_the_game_can_hand_to_an_img_tag()
    {
        var hash = await Store("art");
        var ctx = Json("POST", $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}",
            $$"""{"logicalId":"map-a","sha256":"{{hash}}","contentType":"image/png"}""", Ticket());

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);

        var url = ReadResponse(ctx).Url;
        Assert.Equal($"{BlobApi.RoutePrefix}/{_store.TokenFor(hash)}", url);
        // And the URL it just handed out actually resolves, which is the round trip that matters.
        Assert.True(_store.TryResolveToken(url![(BlobApi.RoutePrefix.Length + 1)..], out _));
    }

    [Fact]
    public async Task Registering_never_takes_a_lobby_id_from_the_body()
    {
        var hash = await Store("art");
        // A body naming someone else's lobby. There is no lobbyId member to bind to, so the only lobby
        // this can reach is the one the verified ticket names — which is the security property, and the
        // reason the request record has no such field to remove later.
        var ctx = Json("POST", $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}",
            $$"""{"logicalId":"map","sha256":"{{hash}}","lobbyId":"ZZZZ"}""", Ticket());

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(_store.TokenFor(hash), _store.TokenForHandle(_lobby.Id, "map"));
        Assert.Null(_store.TokenForHandle("ZZZZ", "map"));
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_bad_request_rather_than_an_exception()
    {
        var ctx = Json("POST", $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}", "{not json", Ticket());

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status400BadRequest, ctx.Response.StatusCode);
        Assert.False(ReadResponse(ctx).Ok);
    }

    [Fact]
    public async Task Registering_before_uploading_is_a_conflict()
    {
        var ctx = Json("POST", $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}",
            $$"""{"logicalId":"map","sha256":"{{HashOf("never uploaded")}}"}""", Ticket());

        Assert.False(await Run(ctx));
        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Unregistering_succeeds_whether_or_not_the_handle_existed()
    {
        var hash = await Store("art");
        _store.Register(_lobby.Id, GameId, "map", hash);

        var known = Request("DELETE",
            $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}/map", ticket: Ticket());
        Assert.False(await Run(known));
        Assert.Equal(StatusCodes.Status200OK, known.Response.StatusCode);
        Assert.Null(_store.TokenForHandle(_lobby.Id, "map"));

        // Optional and never required (R5), so a game must be able to call it defensively — including
        // twice, and including for something it never registered.
        var unknown = Request("DELETE",
            $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}/map", ticket: Ticket());
        Assert.False(await Run(unknown));
        Assert.Equal(StatusCodes.Status200OK, unknown.Response.StatusCode);
    }

    [Fact]
    public async Task A_percent_encoded_name_is_decoded_before_the_handle_is_looked_up()
    {
        var hash = await Store("art");
        _store.Register(_lobby.Id, GameId, "maps/a b.png", hash);

        var ctx = Request("DELETE",
            $"{BlobApi.RoutePrefix}/{BlobApi.RegisterSegment}/maps%2Fa%20b.png", ticket: Ticket());
        Assert.False(await Run(ctx));

        Assert.Null(_store.TokenForHandle(_lobby.Id, "maps/a b.png"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private string Ticket() => _tokens.IssueTicket(PlayerId, _lobby.Id, GameId);

    private async Task<string> Store(string content, string? contentType = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var result = await _store.ReceiveAsync(
            _lobby.Id, GameId, HashOf(bytes), contentType, new MemoryStream(bytes),
            TestContext.Current.CancellationToken);
        Assert.Equal(BlobIngestOutcome.Stored, result.Outcome);
        return HashOf(bytes);
    }

    private static DefaultHttpContext Request(
        string method, string path, byte[]? body = null, string? ticket = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        if (body is not null)
        {
            ctx.Request.Body = new MemoryStream(body);
            ctx.Request.ContentLength = body.Length;
        }
        if (ticket is not null) ctx.Request.Headers[BlobApi.TicketHeader] = ticket;
        return ctx;
    }

    private static DefaultHttpContext Json(string method, string path, string body, string ticket)
    {
        var ctx = Request(method, path, Encoding.UTF8.GetBytes(body), ticket);
        ctx.Request.ContentType = "application/json";
        return ctx;
    }

    /// <summary>Runs the middleware. True when the request fell through to the next component, which
    /// for a read means the static-file mount is expected to serve it.</summary>
    private async Task<bool> Run(HttpContext ctx)
    {
        var passedThrough = false;
        await BlobApi.Middleware(_api)(_ => { passedThrough = true; return Task.CompletedTask; })(ctx);
        return passedThrough;
    }

    private static BlobResponse ReadResponse(HttpContext ctx)
    {
        var body = (MemoryStream)ctx.Response.Body;
        body.Position = 0;
        return JsonSerializer.Deserialize<BlobResponse>(body, JsonWeb)!;
    }

    // The options the server itself serializes with (camelCase on the wire, via the source-generated
    // context). Reflection here rather than that context because it lives behind an internal namespace
    // this file already reaches — what is being asserted is the wire shape, not the path the server takes.
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private static string HashOf(string content) => HashOf(Encoding.UTF8.GetBytes(content));

    private static string HashOf(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Stands in for Kestrel's body-size feature, which <see cref="DefaultHttpContext"/> does not carry.
    /// Hand-rolled like <c>FakeWebSocket</c> and <c>FakeHttpMessageHandler</c>: this project fakes its
    /// collaborators directly rather than taking on a mocking library.
    /// </summary>
    private sealed class FakeBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 30_000_000;
    }
}
