namespace KnockBox.Server.Games.Blobs;

/// <summary>
/// The on-disk shape of the blob root — the writable folder games upload shared media into.
/// </summary>
/// <remarks>
/// <code>
/// blobs/
///   &lt;first2&gt;/&lt;sha256&gt;            content-addressed; sharded so one directory never holds them all
///   .staging/upload-&lt;guid&gt;.part    in-flight uploads
/// </code>
/// Sharding on the first two hex characters keeps directory sizes sane at 256 buckets, the same reason
/// <see cref="GameAssetPrecompressor"/> keeps a subdirectory per game rather than flattening everything.
///
/// <b>Staging is inside the blob root on purpose</b>, not in the system temp directory: publishing an
/// upload is a rename, and a rename across volumes is a copy. <c>PackageManager</c> makes the same
/// choice for the same reason, and its test pins it.
///
/// The staging directory is dot-prefixed, so the startup sweep's shard enumeration
/// (<see cref="ShardDirs"/>, two hex characters) never mistakes it for content.
///
/// Every path here is derived from a hash that <see cref="IsValidHash"/> has already accepted, which is
/// what makes them safe to combine with a root: a 64-character lowercase hex string cannot contain a
/// separator, a drive letter or a <c>..</c> segment, so there is no traversal to defend against
/// downstream. Callers must validate first — that is not a suggestion, it is the whole defence.
/// </remarks>
public static class BlobLayout
{
    public const string StagingDirName = ".staging";

    /// <summary>Length of a SHA-256 rendered as lowercase hex.</summary>
    public const int HashLength = 64;

    /// <summary>Characters of the hash used as the shard directory name.</summary>
    public const int ShardLength = 2;

    /// <summary>
    /// True when <paramref name="hash"/> is exactly 64 lowercase hex characters.
    /// </summary>
    /// <remarks>
    /// Lowercase only, and deliberately not case-insensitive: the server writes hashes with
    /// <c>Convert.ToHexStringLower</c>, so accepting uppercase would let the same bytes reach two
    /// different file names on a case-sensitive filesystem — one file per casing, and dedup (R2) quietly
    /// stops working. A client sending uppercase gets a 400 telling it so, which is a better outcome than
    /// a second copy nobody notices.
    /// </remarks>
    public static bool IsValidHash(string? hash)
    {
        if (hash is null || hash.Length != HashLength) return false;
        foreach (var c in hash)
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
                return false;
        return true;
    }

    /// <summary>The shard-relative path of a blob, e.g. <c>ab/abcd…</c>. Forward slash, because the one
    /// caller appends it to a request path rather than a filesystem path.</summary>
    public static string RelativePath(string hash) => $"{hash[..ShardLength]}/{hash}";

    /// <summary>The absolute file path of a blob's content.</summary>
    public static string ContentPath(string blobsRoot, string hash) =>
        Path.Combine(blobsRoot, hash[..ShardLength], hash);

    /// <summary>The absolute directory a blob's content lives in.</summary>
    public static string ShardDir(string blobsRoot, string hash) =>
        Path.Combine(blobsRoot, hash[..ShardLength]);

    public static string StagingDir(string blobsRoot) => Path.Combine(blobsRoot, StagingDirName);

    public static string StagingPath(string blobsRoot, Guid id) =>
        Path.Combine(StagingDir(blobsRoot), $"upload-{id:N}.part");

    /// <summary>
    /// The shard directories under <paramref name="blobsRoot"/>, skipping anything that is not two hex
    /// characters — which is how <c>.staging</c> is excluded without naming it.
    /// </summary>
    public static IEnumerable<string> ShardDirs(string blobsRoot)
    {
        if (!Directory.Exists(blobsRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(blobsRoot))
        {
            var name = Path.GetFileName(dir);
            if (name.Length != ShardLength) continue;
            if (name[0] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) continue;
            if (name[1] is not (>= '0' and <= '9' or >= 'a' and <= 'f')) continue;
            yield return dir;
        }
    }
}

/// <summary>
/// The content types a blob may be served as, and what anything else degrades to.
/// </summary>
/// <remarks>
/// <b>This allowlist exists for a performance reason, not a security one.</b>
/// <c>application/octet-stream</c> is in the response-compression MIME list (see
/// <c>AddResponseCompression</c> in Program.cs), so a PNG served under it is Brotli-compressed at request
/// time — burning CPU to re-compress bytes that are already compressed, on every cache miss. Serving the
/// real image type keeps the response off that list entirely.
///
/// An unrecognised type is not refused, because refusing would make the store useless for a media type
/// nobody anticipated. It falls back to <see cref="Default"/>, and the serving path pairs that fallback
/// with <c>Content-Encoding: identity</c> so the compression middleware skips it anyway — see
/// <c>BlobApi</c>. That is the honest version of the trick: it declines to compress rather than claiming
/// a compression that never happened.
///
/// Types are matched exactly and case-insensitively, with any <c>;charset=…</c> parameter dropped. A
/// wildcard family (<c>audio/*</c>) is not accepted: the point is to name the types whose compressibility
/// we actually know something about.
/// </remarks>
public static class BlobContentTypes
{
    public const string Default = "application/octet-stream";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images — the reason this feature exists, and all already compressed.
        "image/png", "image/jpeg", "image/webp", "image/gif", "image/avif", "image/svg+xml",
        // Audio and video, for a game that shares a soundscape or a cutscene.
        "audio/mpeg", "audio/ogg", "audio/wav", "audio/webm", "audio/mp4",
        "video/mp4", "video/webm",
        // Fonts, which a game may want to ship per-session rather than in its bundle.
        "font/woff2", "font/woff", "font/ttf",
    };

    /// <summary>
    /// <paramref name="declared"/> when it is a type we recognise, otherwise <see cref="Default"/>.
    /// Never throws and never returns null — a missing or malformed header is just the fallback.
    /// </summary>
    public static string Normalize(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared)) return Default;

        var semicolon = declared.IndexOf(';');
        var bare = (semicolon >= 0 ? declared.AsSpan(0, semicolon) : declared.AsSpan()).Trim();
        if (bare.IsEmpty) return Default;

        // Look the trimmed span up without allocating unless it is actually a hit.
        foreach (var allowed in Allowed)
            if (bare.Equals(allowed, StringComparison.OrdinalIgnoreCase))
                return allowed;

        return Default;
    }

    /// <summary>True when <see cref="Normalize"/> would keep <paramref name="contentType"/> as-is.</summary>
    public static bool IsKnown(string contentType) => Allowed.Contains(contentType);
}
