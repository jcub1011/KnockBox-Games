using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Covers the pre-compressed asset negotiation step. This had no test at all, which mattered most for
/// WebAssembly: a browser only stream-compiles bytes it was told are <c>application/wasm</c>, so if the
/// rewrite to <c>index.wasm.br</c> also changed the advertised content type, every WASM game would break
/// while every header still looked plausible. The gap surfaced only when a real game was installed.
/// </summary>
public class GameAssetNegotiationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-negotiate-" + Guid.NewGuid().ToString("N"));
    private readonly PhysicalFileProvider _compressed;
    private readonly FileExtensionContentTypeProvider _contentTypes = new();

    public GameAssetNegotiationTests()
    {
        Directory.CreateDirectory(_root);
        _compressed = new PhysicalFileProvider(_root);
        // Mirrors the game-origin content-type setup in Program.cs.
        _contentTypes.Mappings[".pck"] = "application/octet-stream";
        _contentTypes.Mappings[".data"] = "application/octet-stream";
    }

    public void Dispose()
    {
        _compressed.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Creates a cached variant, e.g. <c>demo/index.wasm.br</c>, under the compressed root.</summary>
    private void WriteVariant(string relative)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
    }

    private static DefaultHttpContext Request(string path, string? acceptEncoding, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (acceptEncoding is not null) ctx.Request.Headers.AcceptEncoding = acceptEncoding;
        return ctx;
    }

    private bool Negotiate(HttpContext ctx, bool gzipEnabled = true) =>
        GameAssetNegotiation.Negotiate(ctx, _compressed, _contentTypes, gzipEnabled);

    // ── The case that matters most ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_wasm_variant_is_served_as_application_wasm_not_as_the_variants_own_type()
    {
        // The whole point: the body is Brotli, but the content type must still describe the DECOMPRESSED
        // bytes, or instantiateStreaming refuses them.
        WriteVariant("demo/index.wasm.br");
        var ctx = Request("/games/demo/index.wasm", "br, gzip");

        Assert.True(Negotiate(ctx));

        Assert.Equal("/games/demo/index.wasm.br", ctx.Request.Path.Value);
        Assert.Equal("br", ctx.Items[GameAssetNegotiation.EncodingItem]);
        Assert.Equal("application/wasm", ctx.Items[GameAssetNegotiation.ContentTypeItem]);
    }

    [Fact]
    public void Engine_data_files_keep_their_configured_type()
    {
        WriteVariant("demo/index.pck.br");
        var ctx = Request("/games/demo/index.pck", "br");

        Assert.True(Negotiate(ctx));
        Assert.Equal("application/octet-stream", ctx.Items[GameAssetNegotiation.ContentTypeItem]);
    }

    [Fact]
    public void An_unknown_extension_falls_back_to_octet_stream()
    {
        WriteVariant("demo/blob.weird.br");
        var ctx = Request("/games/demo/blob.weird", "br");

        Assert.True(Negotiate(ctx));
        Assert.Equal("application/octet-stream", ctx.Items[GameAssetNegotiation.ContentTypeItem]);
    }

    // ── Encoding selection ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Brotli_is_preferred_over_gzip_when_both_are_cached_and_accepted()
    {
        WriteVariant("demo/app.js.br");
        WriteVariant("demo/app.js.gz");
        var ctx = Request("/games/demo/app.js", "gzip, br");

        Assert.True(Negotiate(ctx));
        Assert.Equal("/games/demo/app.js.br", ctx.Request.Path.Value);
    }

    [Fact]
    public void Gzip_is_used_when_brotli_is_refused()
    {
        WriteVariant("demo/app.js.gz");
        // q=0 means "do not send me this encoding".
        var ctx = Request("/games/demo/app.js", "br;q=0, gzip");

        Assert.True(Negotiate(ctx));
        Assert.Equal("/games/demo/app.js.gz", ctx.Request.Path.Value);
        Assert.Equal("gzip", ctx.Items[GameAssetNegotiation.EncodingItem]);
    }

    [Fact]
    public void Gzip_is_never_chosen_when_the_gzip_cache_is_disabled()
    {
        WriteVariant("demo/app.js.gz");
        var ctx = Request("/games/demo/app.js", "gzip");

        Assert.False(Negotiate(ctx, gzipEnabled: false));
        Assert.Equal("/games/demo/app.js", ctx.Request.Path.Value);
    }

    // ── Leaving the request alone ─────────────────────────────────────────────────────────────────
    // Every negative case must fall through untouched, so serving lands on the raw file plus the
    // on-the-fly compression fallback.

    [Fact]
    public void A_client_that_accepts_nothing_gets_the_raw_file()
    {
        WriteVariant("demo/app.js.br");
        var ctx = Request("/games/demo/app.js", acceptEncoding: null);

        Assert.False(Negotiate(ctx));
        Assert.Equal("/games/demo/app.js", ctx.Request.Path.Value);
        Assert.Empty(ctx.Items);
    }

    [Fact]
    public void A_missing_variant_falls_through_to_the_raw_file()
    {
        // Nothing cached yet — the not-yet-warmed case on a cold boot.
        var ctx = Request("/games/demo/app.js", "br, gzip");

        Assert.False(Negotiate(ctx));
        Assert.Equal("/games/demo/app.js", ctx.Request.Path.Value);
    }

    [Fact]
    public void A_directory_matching_the_variant_name_is_not_served()
    {
        Directory.CreateDirectory(Path.Combine(_root, "demo", "app.js.br"));
        var ctx = Request("/games/demo/app.js", "br");

        Assert.False(Negotiate(ctx));
        Assert.Equal("/games/demo/app.js", ctx.Request.Path.Value);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public void Only_get_and_head_are_negotiated(string method)
    {
        WriteVariant("demo/app.js.br");
        var ctx = Request("/games/demo/app.js", "br", method);

        Assert.False(Negotiate(ctx));
    }

    [Fact]
    public void Head_is_negotiated_so_a_probe_sees_the_same_headers_as_a_get()
    {
        WriteVariant("demo/app.js.br");
        var ctx = Request("/games/demo/app.js", "br", "HEAD");

        Assert.True(Negotiate(ctx));
        Assert.Equal("/games/demo/app.js.br", ctx.Request.Path.Value);
    }

    [Theory]
    [InlineData("/knockbox.js")]          // the SDK, served from the web root
    [InlineData("/games/")]               // directory request
    [InlineData("/")]
    public void Paths_outside_a_game_asset_are_left_alone(string path)
    {
        WriteVariant("demo/app.js.br");
        var ctx = Request(path, "br");

        Assert.False(Negotiate(ctx));
        Assert.Equal(path, ctx.Request.Path.Value);
    }

    [Fact]
    public void A_traversal_attempt_cannot_reach_outside_the_cache_root()
    {
        // The file provider blocks it; assert the request is left untouched rather than rewritten.
        //
        // The bait has to sit OUTSIDE the provider's root for the traversal to have anything to reach,
        // which means the shared temp folder rather than this instance's own directory — so it carries a
        // unique name and is removed afterwards. A fixed name here was a real race: two copies of the
        // suite running at once (a CI matrix, a retry, a developer with two checkouts) collided on the
        // one file, and whichever lost the write failed with "used by another process" — a failure with
        // nothing to do with what the test is about. It also littered a file into temp on every run.
        var bait = Path.Combine(Path.GetTempPath(), $"kb-outside-{Guid.NewGuid():N}.br");
        File.WriteAllBytes(bait, [9]);
        try
        {
            var ctx = Request($"/games/../../{Path.GetFileNameWithoutExtension(bait)}", "br");

            Assert.False(Negotiate(ctx));
        }
        finally
        {
            try { File.Delete(bait); } catch { /* best effort */ }
        }
    }

    // ── The pure helper the above depends on ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("br, gzip", true, "br")]
    [InlineData("gzip", true, "gzip")]
    [InlineData("gzip", false, null)]
    [InlineData("br;q=0, gzip", true, "gzip")]
    [InlineData("br;q=0, gzip;q=0", true, null)]
    [InlineData("identity", true, null)]
    [InlineData("", true, null)]
    [InlineData(null, true, null)]
    public void NegotiateEncoding_picks_the_best_accepted_encoding(string? accept, bool gzip, string? expected)
    {
        Assert.Equal(expected, GameAssetPrecompressor.NegotiateEncoding(accept, gzip));
    }
}
