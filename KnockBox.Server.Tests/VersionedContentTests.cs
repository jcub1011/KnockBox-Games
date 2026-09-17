using System.Text.RegularExpressions;
using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The platform's own files are versioned by content hash, not by a hand-bumped query string:
/// <see cref="ContentHashProvider"/> derives the token from the bundle bytes, and
/// <see cref="VersionedCacheHeaders"/> grants immutability only to a URL carrying the current
/// token. These tests pin both halves, plus the repo-file contract the scheme depends on — the
/// carrier pages must contain the placeholder the middleware substitutes, and no hardcoded
/// <c>?v=N</c> may remain for a human to (not) bump.
/// </summary>
public class VersionedContentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-versioned-" + Guid.NewGuid().ToString("N"));

    public VersionedContentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private void WriteBundle(string a = "shell-bytes", string b = "core-bytes")
    {
        File.WriteAllText(Path.Combine(_root, "shell.js"), a);
        File.WriteAllText(Path.Combine(_root, "kb-core.js"), b);
    }

    private ContentHashProvider Provider() => new(_root, "shell.js", "kb-core.js");

    [Fact]
    public void Identical_bytes_yield_a_stable_token()
    {
        WriteBundle();
        var provider = Provider();
        Assert.Equal(provider.Current, provider.Current);
        Assert.NotEqual(ContentHashProvider.UnknownToken, provider.Current);
    }

    [Fact]
    public void Any_byte_change_in_the_bundle_moves_the_token()
    {
        WriteBundle();
        var provider = Provider();
        var before = provider.Current;

        // A different length is the common case (mtime + length both move).
        File.WriteAllText(Path.Combine(_root, "kb-core.js"), "core-bytes-changed");
        Assert.NotEqual(before, provider.Current);
    }

    [Fact]
    public void Same_length_content_change_moves_the_token_once_the_filesystem_notices_it()
    {
        WriteBundle("aaa", "bbb");
        var provider = Provider();
        var before = provider.Current;

        // Same byte count, so only the mtime distinguishes the write — force it in case the
        // filesystem's granularity would otherwise report the same tick.
        var path = Path.Combine(_root, "shell.js");
        File.WriteAllText(path, "ccc");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.NotEqual(before, provider.Current);
    }

    [Fact]
    public void Order_matters_so_rearranged_bundles_do_not_alias()
    {
        WriteBundle("aaa", "bbb");
        var forward = Provider().Current;
        var swapped = new ContentHashProvider(_root, "kb-core.js", "shell.js").Current;
        // Same files, different declared order — must not share a token.
        Assert.NotEqual(forward, swapped);
    }

    [Fact]
    public void A_missing_bundle_file_yields_the_unknown_token_and_recovery_rehashes()
    {
        var provider = Provider();
        Assert.Equal(ContentHashProvider.UnknownToken, provider.Current);

        WriteBundle();
        Assert.NotEqual(ContentHashProvider.UnknownToken, provider.Current);

        File.Delete(Path.Combine(_root, "shell.js"));
        Assert.Equal(ContentHashProvider.UnknownToken, provider.Current);
    }

    [Fact]
    public void Render_substitutes_every_placeholder_and_falls_back_when_the_page_is_absent()
    {
        WriteBundle();
        File.WriteAllText(Path.Combine(_root, "index.html"), "<a>/shell.js?v=PH</a><b>/home.css?v=PH</b>");
        var provider = Provider();

        var rendered = provider.TryRenderPage("index.html", "PH");
        Assert.NotNull(rendered);
        Assert.DoesNotContain("PH", rendered, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(rendered!, $@"\?v={Regex.Escape(provider.Current)}").Count);

        Assert.Null(provider.TryRenderPage("not-there.html", "PH"));
    }

    [Theory]
    [InlineData("/shell.js", "abc123", "abc123", true)]
    [InlineData("/shell.js", "abc123", "stale99", false)]
    [InlineData("/shell.js", "abc123", "", false)]
    [InlineData("/SHELL.JS", "abc123", "abc123", true)] // path match is case-insensitive
    [InlineData("/kb-core.js", "abc123", "abc123", false)] // never versioned: bare transitive import
    [InlineData("/knockbox.js", "abc123", "abc123", false)] // game SDK: referenced from author HTML
    [InlineData("/shell.js", "0", "0", true)] // degraded token still self-consistent (see provider docs)
    [InlineData(null, "abc123", "abc123", false)]
    public void Immutability_requires_a_versioned_path_and_the_current_token(
        string? path, string current, string query, bool immutable)
    {
        var versioned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/shell.js", "/home.css" };
        Assert.Equal(
            immutable ? VersionedCacheHeaders.Immutable : VersionedCacheHeaders.Revalidate,
            VersionedCacheHeaders.CacheControlFor(path, query, current, versioned));
    }

    [Theory]
    [InlineData("/", new[] { "/", "/index.html" }, true)]
    [InlineData("/index.html", new[] { "/", "/index.html" }, true)]
    [InlineData("/INDEX.HTML", new[] { "/", "/index.html" }, true)]
    [InlineData("/?game=ttt", new[] { "/", "/index.html" }, false)] // path only: query is the client's business
    [InlineData("/shell.js", new[] { "/", "/index.html" }, false)]
    [InlineData("/terminal.html", new[] { "/", "/index.html", "/terminal.html" }, true)]
    [InlineData(null, new[] { "/" }, false)]
    public void Carrier_matching_is_exact_paths_only(string? path, string[] carriers, bool expected)
    {
        // The query string never participates: carriers are matched on Path, which excludes it.
        Assert.Equal(expected, VersionedCacheHeaders.IsCarrierPage(path, carriers));
    }

    /// <summary>
    /// The repo-file contract: the shell carrier contains the placeholder (twice — script and
    /// stylesheet), and no web page carries a hardcoded numeric version for a human to bump.
    /// </summary>
    [Fact]
    public void Shell_and_admin_carriers_use_placeholders_not_hardcoded_versions()
    {
        var shell = RepoFile.Read("web/index.html");
        var admin = RepoFile.Read("web/admin/index.html");
        var terminal = RepoFile.Read("web/admin/terminal.html");
        if (shell is null || admin is null || terminal is null) return; // outside a checkout

        Assert.Equal(2, Regex.Matches(shell, @"__KB_SHELL_HASH__").Count);

        Assert.Contains("__KB_ADMIN_HASH__", admin, StringComparison.Ordinal);
        Assert.Contains("__KB_ADMIN_HASH__", terminal, StringComparison.Ordinal);

        foreach (var (name, page) in new[] { ("web/index.html", shell), ("web/admin/index.html", admin), ("web/admin/terminal.html", terminal) })
        {
            Assert.False(
                Regex.IsMatch(page, @"\?v=\d"),
                $"{name} carries a hardcoded numeric ?v= version — the content-hash provider owns versioning now.");
        }
    }

    /// <summary>
    /// The real shell bundle must hash to a real token: if a bundle file is missing from the
    /// checkout, versioned URLs would all serve under "0" and nothing would ever be immutable.
    /// </summary>
    [Fact]
    public void The_checked_in_shell_bundle_hashes_to_a_real_token()
    {
        if (RepoFile.Path_("web/shell.js") is null) return; // outside a checkout
        var provider = new ContentHashProvider(
            RepoFile.Path_("web")!, "shell.js", "kb-core.js", "kb-protocol.js", "home.css");
        Assert.NotEqual(ContentHashProvider.UnknownToken, provider.Current);
    }
}
