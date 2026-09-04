using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using KnockBox.Server.Games.Blobs;
using KnockBox.Server.Lobbies;
using KnockBox.Server.Security;
using KnockBox.Server.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace KnockBox.Server.Hosting;

/// <summary>What a game sends to point one of its own names at uploaded content.</summary>
/// <remarks>
/// Every member is nullable and defaulted, the discipline every untrusted-input DTO in this server
/// follows: this is parsed from a request body, so a missing or misspelled key must degrade to a
/// validation refusal rather than throw inside the deserializer.
///
/// There is no <c>lobbyId</c>, and its absence is the security property. The lobby comes from the
/// verified ticket, so a client cannot register into someone else's session however it shapes the body.
/// </remarks>
public sealed record BlobRegisterRequest(
    string? LogicalId = null, string? Sha256 = null, string? ContentType = null);

/// <summary>The one response shape for every blob route that answers with a body.</summary>
/// <param name="Url">The read URL, on a successful register only. Headerless by design — see
/// <see cref="IBlobStore.TokenFor"/> for why it is a MAC of the hash rather than the hash.</param>
/// <param name="Error">Game-facing, and never a stack trace: a game shows this to a player.</param>
public sealed record BlobResponse(
    bool Ok, string? Error = null, string? Url = null, long Bytes = 0);

/// <summary>
/// The HTTP surface of the blob store: the side channel for the bytes that cannot cross the relay.
/// </summary>
/// <remarks>
/// <para><b>Middleware, not a route table, and that is a property of where it is mounted rather than a
/// preference.</b> The game-origin branch of the pipeline has no <c>UseRouting</c> and no
/// <c>UseEndpoints</c> — it is a chain of <c>app.Use</c> gates followed by three static-file mounts,
/// ordered 404-first. Bringing a routing pipeline into that branch to host five routes would make this
/// the only endpoint-routed thing on the origin, and it would have to sit either before the gates
/// (bypassing them) or after the static mounts (unreachable). A prefix match plus a method switch fits
/// the branch it lives in.</para>
/// <para><b>Reads are anonymous and writes are fully authenticated</b>, and the split is not
/// laziness — it is what the read path has to satisfy. <c>&lt;img src&gt;</c>, <c>&lt;audio src&gt;</c>
/// and every engine loader fetch a URL without the ability to attach a header, so a header-based scheme
/// would break all of them. The URL is therefore the capability, and
/// <see cref="IBlobStore.TokenFor"/> is what makes it one. Writes are ordinary SDK <c>fetch</c> calls
/// that can carry a header, so they get the full three-check treatment, and so does the existence probe
/// — which is why <c>HEAD</c> takes a ticket even though <c>GET</c> does not.</para>
/// <para><b>Serving is <c>StaticFileMiddleware</c>'s job, not this class's.</b> The read handler
/// verifies the token, resolves it to a shard-relative path, rewrites <c>Request.Path</c> and calls
/// <c>next()</c>; a <c>PhysicalFileProvider</c> mount over the blob root does the rest. That inherits
/// ETag, <c>If-None-Match</c>/304, <c>Range</c>/206, <c>Content-Length</c> and kernel sendfile with
/// framework-guaranteed constant memory — the same reuse <c>GamesCompressedStaticOptions</c> already
/// relies on, and the reason there is no <c>SendFileAsync</c> call anywhere in this server.</para>
/// </remarks>
internal static class BlobApi
{
    /// <summary>
    /// The path prefix, deliberately NOT under <c>/games/</c>. The shell origin 404s every
    /// <c>/games/*</c> path that is not a catalog-declared thumbnail, so <c>/games/blob/…</c> would be
    /// killed there while looking like it should work.
    /// </summary>
    public const string RoutePrefix = "/blob";

    /// <summary>The register sub-path, under the prefix.</summary>
    public const string RegisterSegment = "register";

    /// <summary>
    /// Where the SDK puts the ticket. It lives in the game iframe's URL <em>fragment</em> and is
    /// deliberately never sent to the server by the shell, so the SDK has to attach it explicitly. A
    /// custom header on a same-origin <c>fetch</c> needs no preflight, and
    /// <c>Authorization: Bearer</c> is accepted as well for callers that find it more natural.
    /// </summary>
    public const string TicketHeader = "X-KnockBox-Ticket";

    /// <summary>
    /// Key under which the read handler stashes the content type for <c>OnPrepareResponse</c> to apply.
    /// The same hand-off <c>GameAssetNegotiation</c> uses, and for the same reason: the static-file
    /// middleware decides the response, and the only place to correct it is its own callback.
    /// </summary>
    public const string ContentTypeItem = "kb.blob.contentType";

    public sealed record Options(
        IBlobStore Blobs,
        BlobOptionsProvider Limits,
        TokenService Tokens,
        LobbyManager Lobbies,
        ILogger Logger);

    /// <summary>A verified ticket's claims.</summary>
    internal readonly record struct Caller(string PlayerId, string LobbyId, string GameId);

    // ── The pure decision ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why a write must be refused, or null when it may proceed.
    /// </summary>
    /// <remarks>
    /// <para>Split out and kept free of <see cref="HttpContext"/>, of
    /// <see cref="TokenService"/> and of <see cref="LobbyManager"/>, for the reason
    /// <see cref="AdminApi.WriteGuardRefusal"/> gives: it is a security decision, and there is no test
    /// host in this repo to exercise it through a real request. Taking facts rather than services is
    /// what makes it a function a test can enumerate the cases of.</para>
    /// <para><b>Three checks, in this order, and the middle one is the primary control.</b> The
    /// signature check alone is not this server's bar — <c>WebSocketHandler.RunDataAsync</c> says so
    /// explicitly, treating live membership as the real gate and the ticket's expiry as defence in
    /// depth. A ticket survives a reconnect on purpose, so it must stop working the moment its holder
    /// is no longer in the lobby, and the third check stops one issued for a lobby whose game has since
    /// changed underneath it.</para>
    /// <para>401 for an unusable ticket and 403 for a usable ticket that does not authorise this: the
    /// first invites the SDK to ask for a new one, the second tells it not to bother.</para>
    /// </remarks>
    /// <param name="ticketVerified">Whether the signature and expiry checked out.</param>
    /// <param name="lobby">The lobby the ticket names, or null when it is gone.</param>
    /// <param name="playerIsMember">Whether the ticket's player is still in that lobby.</param>
    /// <param name="ticketGameId">The game the ticket was scoped to.</param>
    internal static (int Status, string Error)? AuthRefusal(
        bool ticketVerified, string? lobbyGameId, bool lobbyExists, bool playerIsMember,
        string? ticketGameId)
    {
        if (!ticketVerified)
            return (StatusCodes.Status401Unauthorized,
                "A valid game ticket is required. Send it as an " + TicketHeader + " header.");

        // Re-validated against LIVE membership, not just against the signature: a ticket is meant to
        // survive a reconnect, so nothing but this stops one working after its holder has left.
        if (!lobbyExists || !playerIsMember)
            return (StatusCodes.Status403Forbidden, "Lobby membership expired.");

        // The ticket is scoped to the game the lobby was created for; refuse if they no longer match.
        if (!string.Equals(ticketGameId, lobbyGameId, StringComparison.OrdinalIgnoreCase))
            return (StatusCodes.Status403Forbidden, "Ticket game mismatch.");

        return null;
    }

    /// <summary>
    /// The status a completed upload answers with. Kept beside <see cref="AuthRefusal"/> and equally
    /// free of <see cref="HttpContext"/>, because the mapping is the part worth pinning: a quota that
    /// answered 400 would have an SDK retrying forever, and a hash mismatch that answered 507 would
    /// have an operator adding disk to fix a client bug.
    /// </summary>
    internal static int StatusFor(BlobIngestOutcome outcome) => outcome switch
    {
        BlobIngestOutcome.Stored or BlobIngestOutcome.AlreadyPresent => StatusCodes.Status200OK,
        // 503, not 404: the feature exists and is switched off, which is a thing an operator can change.
        BlobIngestOutcome.Disabled => StatusCodes.Status503ServiceUnavailable,
        BlobIngestOutcome.HashRejected or BlobIngestOutcome.HashMismatch => StatusCodes.Status400BadRequest,
        BlobIngestOutcome.TooLarge => StatusCodes.Status413PayloadTooLarge,
        // 507 Insufficient Storage says "come back when you have released something", which is exactly
        // the state a full quota is, and is distinguishable by an SDK from a request it should not retry.
        BlobIngestOutcome.LobbyQuotaExceeded or BlobIngestOutcome.TotalQuotaExceeded =>
            StatusCodes.Status507InsufficientStorage,
        // 429 with no Retry-After: the client's own upload finishing is the event to wait for, and only
        // the client knows when that is.
        BlobIngestOutcome.TooManyUploads => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>The status a completed registration answers with.</summary>
    internal static int StatusFor(BlobRegisterOutcome outcome) => outcome switch
    {
        BlobRegisterOutcome.Registered or BlobRegisterOutcome.AlreadyRegistered => StatusCodes.Status200OK,
        BlobRegisterOutcome.Disabled => StatusCodes.Status503ServiceUnavailable,
        // 409, not 404: the id is well-formed and the content simply is not here yet, so "upload it
        // first" is a conflict with the current state rather than a missing resource.
        BlobRegisterOutcome.UnknownHash => StatusCodes.Status409Conflict,
        BlobRegisterOutcome.LobbyQuotaExceeded => StatusCodes.Status507InsufficientStorage,
        _ => StatusCodes.Status400BadRequest,
    };

    // ── The middleware ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles <c>/blob/…</c> and passes everything else through. Insert inside the game-origin branch
    /// <b>after</b> the two 404 gates and <b>before</b> the pre-compression negotiation, which rewrites
    /// paths of its own.
    /// </summary>
    /// <remarks>
    /// Worth recording, because the obvious worry turns out not to apply: <c>ApplyCrossOriginIsolation</c>
    /// is a no-op for these paths — it returns early when <c>GameAssetPath.GameId</c> finds no game id,
    /// which it never will under <c>/blob</c> — so blobs are served without COOP/COEP. That is correct
    /// rather than an oversight. Those are <em>document</em> headers, and a blob is a subresource; a
    /// cross-origin-isolated game page under <c>require-corp</c> may load same-origin subresources
    /// freely, and the blob mount is on the game origin. <c>Cross-Origin-Resource-Policy</c> is set on
    /// the response anyway, as the cheap half of defence in depth.
    /// </remarks>
    public static Func<RequestDelegate, RequestDelegate> Middleware(Options options) => next => async ctx =>
    {
        if (!ctx.Request.Path.StartsWithSegments(RoutePrefix, out var rest))
        {
            await next(ctx);
            return;
        }

        var tail = rest.Value?.Trim('/') ?? "";
        var method = ctx.Request.Method;

        // A read key always carries its MAC after a dot; a bare hash never can. That is the whole
        // discriminator, and it is why the two shapes can share one prefix without ambiguity.
        if (tail.Contains('.') && (HttpMethods.IsGet(method) || HttpMethods.IsHead(method)))
        {
            await Read(ctx, next, options, tail);
            return;
        }

        if (HttpMethods.IsHead(method))
        {
            await Probe(ctx, options, tail);
            return;
        }

        if (HttpMethods.IsPut(method))
        {
            await Upload(ctx, options, tail);
            return;
        }

        if (HttpMethods.IsPost(method) && tail.Equals(RegisterSegment, StringComparison.Ordinal))
        {
            await Register(ctx, options);
            return;
        }

        if (HttpMethods.IsDelete(method)
            && tail.StartsWith(RegisterSegment + "/", StringComparison.Ordinal))
        {
            await Unregister(ctx, options, tail[(RegisterSegment.Length + 1)..]);
            return;
        }

        // 404 rather than 405, matching the rest of this origin: every gate above it answers an
        // unrecognised shape by not existing. An SDK sending the wrong method here has a bug that a
        // status code will not talk it out of.
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    };

    /// <summary>
    /// <c>GET</c>/<c>HEAD</c> <c>/blob/{sha256}.{tag}</c> — no auth, because the URL is the capability.
    /// </summary>
    private static async Task Read(HttpContext ctx, RequestDelegate next, Options options, string token)
    {
        if (!options.Blobs.TryResolveToken(token, out var target))
        {
            // One 404 for every reason: a malformed token, a forged tag, content that was never here,
            // and content still mid-upload. Distinguishing them would hand an attacker the oracle the
            // MAC exists to close.
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Items[ContentTypeItem] = target.ContentType;
        ctx.Request.Path = $"{RoutePrefix}/{target.RelativePath}";
        await next(ctx);
    }

    /// <summary>
    /// <c>HEAD</c> <c>/blob/{sha256}</c> — the dedup probe, and it takes a ticket.
    /// </summary>
    /// <remarks>
    /// Authenticated even though it reveals only one bit, because that bit is the possession oracle: a
    /// hash is derived from the bytes, so anyone owning the same commercial map pack can compute every
    /// hash in it and ask an anonymous probe which ones a DM has uploaded. Spoilers are most of what a
    /// game's asset privacy protects. The client that needs this is an SDK <c>fetch</c>, which can
    /// carry a header, so nothing is lost by asking for one.
    /// </remarks>
    private static async Task Probe(HttpContext ctx, Options options, string hash)
    {
        if (await Authorize(ctx, options, writeBody: false) is null) return;

        // No body: a HEAD response must not have one, so the answer is entirely in the status.
        // Touch extends the grace window so a subsequent register does not race deletion.
        ctx.Response.StatusCode = options.Blobs.Touch(hash)
            ? StatusCodes.Status200OK
            : StatusCodes.Status404NotFound;
    }

    /// <summary><c>PUT</c> <c>/blob/{sha256}</c> — stream, hash, verify, publish.</summary>
    private static async Task Upload(HttpContext ctx, Options options, string hash)
    {
        if (await Authorize(ctx, options) is not { } caller) return;

        var limits = options.Limits.Current;

        // Kestrel's default body cap is 30,000,000 bytes and nothing else in this server overrides it,
        // so a 100 MB blob would be refused by the host long before BlobMaxBytes had anything to say
        // about it. Raised for THIS request only — no other route has any business accepting a body
        // this size. The `> 0` honours the "non-positive disables" convention: read literally, a cap of
        // 0 set a 4096-byte Kestrel limit and refused every upload while reporting a 0-byte limit.
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = limits.MaxBlobBytes > 0 ? limits.MaxBlobBytes + 4096 : null;

        // Refused in one round trip when the client is honest about the size. The real enforcement is
        // still the byte count while streaming, because Content-Length is the client's claim.
        if (limits.MaxBlobBytes > 0 && ctx.Request.ContentLength > limits.MaxBlobBytes)
        {
            await Write(ctx, new BlobResponse(false,
                $"The blob exceeds the {limits.MaxBlobBytes:N0}-byte limit (KnockBox:BlobMaxBytes)."),
                StatusCodes.Status413PayloadTooLarge);
            return;
        }

        // Answered WITHOUT reading the body, which is the point: Kestrel only emits the interim
        // 100 Continue on the first read of the body, so a client that sent `Expect: 100-continue`
        // never puts a byte on the wire for a blob we already have.
        //
        // Browsers cannot use that — `fetch` has no way to request it — so for the SDK the real
        // optimisation is the HEAD probe above, and this path only catches the race where the probe
        // said "no" and someone else uploaded the same bytes in between. Rare, so draining the body
        // (below) rather than resetting the stream is the right trade: a reset produces client-visible
        // broken pipes on some stacks, which is the failure class that works in a test and fails
        // behind a proxy. Touch extends the grace window so a subsequent register does not race deletion.
        if (options.Blobs.Touch(hash))
        {
            await DrainAsync(ctx, limits);
            await Write(ctx, new BlobResponse(true, Bytes: 0), StatusCodes.Status200OK);
            return;
        }

        var result = await options.Blobs.ReceiveAsync(
            caller.LobbyId, caller.GameId, hash, ctx.Request.ContentType, ctx.Request.Body,
            ctx.RequestAborted);

        await Write(ctx,
            new BlobResponse(result.Success, result.Error, Bytes: result.Bytes),
            StatusFor(result.Outcome));
    }

    /// <summary><c>POST</c> <c>/blob/register</c> — point one of the game's own names at content.</summary>
    private static async Task Register(HttpContext ctx, Options options)
    {
        if (await Authorize(ctx, options) is not { } caller) return;

        var body = await ReadJson(ctx, KnockBoxProtocolContext.Default.BlobRegisterRequest);
        if (body is null)
        {
            await Write(ctx, new BlobResponse(false, "Send a JSON body with logicalId and sha256."),
                StatusCodes.Status400BadRequest);
            return;
        }

        var result = options.Blobs.Register(
            caller.LobbyId, caller.GameId, body.LogicalId ?? "", body.Sha256 ?? "", body.ContentType);

        await Write(ctx,
            new BlobResponse(result.Success, result.Error,
                Url: result.Token is null ? null : $"{RoutePrefix}/{result.Token}"),
            StatusFor(result.Outcome));
    }

    /// <summary><c>DELETE</c> <c>/blob/register/{logicalId}</c> — optional, and never required.</summary>
    /// <remarks>
    /// Always 200, even for a name that was never registered. A game must be able to call this
    /// defensively without tracking what it registered, and a lobby close releases everything anyway —
    /// so there is no state in which "that handle does not exist" is a problem worth telling a game
    /// about.
    /// </remarks>
    private static async Task Unregister(HttpContext ctx, Options options, string logicalId)
    {
        if (await Authorize(ctx, options) is not { } caller) return;

        options.Blobs.Unregister(caller.LobbyId, Uri.UnescapeDataString(logicalId));
        await Write(ctx, new BlobResponse(true), StatusCodes.Status200OK);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gathers the facts <see cref="AuthRefusal"/> decides on, and writes the refusal when there is
    /// one. Null return means the response is already written and the caller must stop.
    /// </summary>
    private static async Task<Caller?> Authorize(HttpContext ctx, Options options, bool writeBody = true)
    {
        var verified = options.Tokens.TryVerifyTicket(
            TicketFrom(ctx.Request), out var playerId, out var lobbyId, out var gameId);

        var lobby = verified ? options.Lobbies.Get(lobbyId) : null;
        var refusal = AuthRefusal(
            verified, lobby?.GameId, lobby is not null,
            lobby is not null && lobby.Contains(playerId), gameId);

        if (refusal is not { } r) return new Caller(playerId, lobbyId, gameId);

        ctx.Response.StatusCode = r.Status;
        // A HEAD response carries no body, so its refusal is the status alone.
        if (writeBody) await Write(ctx, new BlobResponse(false, r.Error), r.Status);
        return null;
    }

    /// <summary>
    /// The ticket from the SDK's own header, or from <c>Authorization: Bearer</c> for callers that
    /// prefer it.
    /// </summary>
    private static string? TicketFrom(HttpRequest request)
    {
        if (request.Headers.TryGetValue(TicketHeader, out var header)
            && header.ToString() is { Length: > 0 } ticket)
            return ticket;

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }

    /// <summary>
    /// Reads and discards a body we are not going to store, so responding does not reset the stream
    /// under a client that already committed to sending it.
    /// </summary>
    /// <remarks>
    /// Capped and pooled like every other streaming read here: a client cannot legitimately have more
    /// than <c>BlobMaxBytes</c> to send, and past that we stop reading and let the host close the
    /// connection — at which point a broken pipe is the client's own doing rather than ours.
    /// </remarks>
    private static async Task DrainAsync(HttpContext ctx, BlobOptions limits)
    {
        if (ctx.Request.ContentLength is 0 or null && !ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await ctx.Request.Body
                .ReadAsync(buffer.AsMemory(0, ChunkSize), ctx.RequestAborted)
                .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (limits.MaxBlobBytes > 0 && total > limits.MaxBlobBytes) return;
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The client hung up mid-drain, which is fine — there was nothing to keep.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private const int ChunkSize = 81920;

    private static async Task<BlobRegisterRequest?> ReadJson(
        HttpContext ctx, JsonTypeInfo<BlobRegisterRequest> typeInfo)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            // An untrusted body must never throw out of here. The caller turns null into a 400.
            return null;
        }
    }

    private static Task Write(HttpContext ctx, BlobResponse body, int status)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(
            ctx.Response.Body, body, KnockBoxProtocolContext.Default.BlobResponse);
    }
}
