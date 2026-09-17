using System.Security.Cryptography;
using System.Text;

namespace KnockBox.Server.Hosting;

/// <summary>
/// A content-derived version token for one bundle of platform files (the shell, the admin portal).
///
/// Browsers cache aggressively, and the old answer — a hand-bumped <c>?v=N</c> in the page markup —
/// depended on a human remembering to bump it, which is how stale shells shipped. This provider
/// derives the token from the files' own bytes instead: any byte change in the bundle moves the
/// token, and identical bytes always yield the same token (so reinstalls don't churn caches).
/// The token is recomputed lazily — each read stats the files and re-hashes only when something
/// changed — so editing a file in dev is picked up on the next refresh with no restart.
///
/// When any bundle file is missing or unreadable the token is <see cref="UnknownToken"/> ("0") rather
/// than throwing: a half-present web root is a deployment problem the diagnostics page already
/// reports, and serving must degrade, not crash. ("0" is safe as a token because the carrier page
/// is served <c>no-store</c> — see <see cref="VersionedCacheHeaders"/> — so no client can hold a
/// stale carrier pointing at it.)
/// </summary>
internal sealed class ContentHashProvider
{
    /// <summary>Token used when the bundle cannot be hashed. Never collides with a real token.</summary>
    public const string UnknownToken = "0";

    private readonly string _root;
    private readonly string[] _files;
    private readonly object _gate = new();
    private List<(bool Exists, long Ticks, long Length)>? _snapshot;
    private string _token = UnknownToken;

    /// <param name="rootDirectory">Directory holding the bundle files and any carrier pages.</param>
    /// <param name="relativePaths">Bundle files, in a fixed order (order feeds the hash).</param>
    public ContentHashProvider(string rootDirectory, params string[] relativePaths)
    {
        _root = rootDirectory;
        _files = relativePaths.Select(p => Path.Combine(rootDirectory, p)).ToArray();
    }

    /// <summary>The current token, re-hashed on first read after any bundle change.</summary>
    public string Current
    {
        get
        {
            lock (_gate)
            {
                var snapshot = Snapshot();
                if (_snapshot is null || !SnapshotsEqual(snapshot, _snapshot))
                {
                    _snapshot = snapshot;
                    _token = ComputeToken();
                }
                return _token;
            }
        }
    }

    /// <summary>
    /// Reads a carrier page (e.g. <c>index.html</c>) and substitutes every <paramref name="placeholder"/>
    /// with <see cref="Current"/>. Null when the page itself cannot be read, so the caller can fall
    /// through to the next middleware rather than serving a half answer.
    /// </summary>
    public string? TryRenderPage(string pageFileName, string placeholder)
    {
        string text;
        try
        {
            text = File.ReadAllText(Path.Combine(_root, pageFileName));
        }
        catch (Exception)
        {
            return null;
        }
        return text.Replace(placeholder, Current, StringComparison.Ordinal);
    }

    private static bool SnapshotsEqual(
        List<(bool Exists, long Ticks, long Length)> a,
        List<(bool Exists, long Ticks, long Length)> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private List<(bool Exists, long Ticks, long Length)> Snapshot()
    {
        var snapshot = new List<(bool, long, long)>(_files.Length);
        foreach (var file in _files)
        {
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                snapshot.Add(info.Exists
                    ? (true, info.LastWriteTimeUtc.Ticks, info.Length)
                    : (false, 0, 0));
            }
            catch (Exception)
            {
                snapshot.Add((false, 0, 0));
            }
        }
        return snapshot;
    }

    private string ComputeToken()
    {
        try
        {
            using var buffer = new MemoryStream();
            foreach (var file in _files)
            {
                var bytes = File.ReadAllBytes(file);
                var name = Encoding.UTF8.GetBytes(Path.GetFileName(file));
                buffer.Write(BitConverter.GetBytes((long)name.Length));
                buffer.Write(name);
                buffer.Write(BitConverter.GetBytes((long)bytes.Length));
                buffer.Write(bytes);
            }
            // Length-prefixing each part keeps "ab"+"c" distinct from "a"+"bc".
            var digest = SHA256.HashData(buffer.ToArray());
            return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
        }
        catch (Exception)
        {
            return UnknownToken;
        }
    }
}
