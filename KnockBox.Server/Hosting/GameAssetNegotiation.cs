using KnockBox.Server.Games;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace KnockBox.Server.Hosting;

/// <summary>
/// Content negotiation for the pre-compressed game-asset cache: when a client accepts an encoding we
/// have a cached variant for, rewrite the request to that variant so the static-file middleware serves
/// the already-compressed bytes instead of compressing the body again on every request.
///
/// The negotiated encoding and the source file's content type are handed to the static-file layer
/// through <see cref="HttpContext.Items"/>, because the response must advertise the encoding while
/// still carrying the <em>decompressed</em> content type. That detail is load-bearing: a browser will
/// only stream-compile WebAssembly it was told is <c>application/wasm</c>, so serving
/// <c>index.wasm.br</c> as its own type would break every WASM game.
/// </summary>
internal static class GameAssetNegotiation
{
    public const string EncodingItem = "kb.precompressed.encoding";
    public const string ContentTypeItem = "kb.precompressed.contentType";

    /// <summary>
    /// Rewrites <paramref name="ctx"/> to the best cached variant, or leaves it untouched when there is
    /// no usable one — in which case serving falls through to the raw file plus on-the-fly compression.
    /// </summary>
    /// <returns>True when the request was rewritten.</returns>
    public static bool Negotiate(
        HttpContext ctx, IFileProvider compressedFiles, IContentTypeProvider contentTypes, bool gzipEnabled)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)) return false;
        // Parse to the CANONICAL id + relative path (no doubled separators, no "." segments) and rebuild
        // the rewritten path from those parts. A directory request or a traversal attempt fails to parse
        // and falls through untouched. Canonicalizing here also keeps this in step with
        // GameOriginAssetGate: both must agree on which file a request names, or a variant could be
        // served for a path the gate declined to recognize.
        if (!GameAssetPath.TryParse(ctx.Request.Path.Value, out var id, out var relative)) return false;

        var encoding = GameAssetPrecompressor.NegotiateEncoding(ctx.Request.Headers.AcceptEncoding.ToString(), gzipEnabled);
        if (encoding is null) return false;

        var ext = encoding == "br" ? ".br" : ".gz";
        // PhysicalFileProvider.GetFileInfo is traversal-safe (blocks "..", rooted paths); the subpath is
        // relative to the provider root, mirroring the "/games" RequestPath the static options use.
        var subpath = $"/{id}/{relative}";
        var variant = compressedFiles.GetFileInfo(subpath + ext);
        if (!variant.Exists || variant.IsDirectory) return false;

        ctx.Items[EncodingItem] = encoding;
        ctx.Items[ContentTypeItem] =
            contentTypes.TryGetContentType(relative, out var contentType) ? contentType : "application/octet-stream";
        ctx.Request.Path = GameAssetPath.Root + subpath + ext;
        return true;
    }
}
