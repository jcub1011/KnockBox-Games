using System.IO.Compression;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

public class GameAssetPrecompressorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-precompress-" + Guid.NewGuid().ToString("N"));
    private readonly string _gamesRoot;
    private readonly string _compressedRoot;

    public GameAssetPrecompressorTests()
    {
        _gamesRoot = Path.Combine(_root, "games");
        _compressedRoot = Path.Combine(_root, "games-compressed");
        Directory.CreateDirectory(_gamesRoot);
        Directory.CreateDirectory(_compressedRoot);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private GameAssetPrecompressor New(bool gzip = true, int minBytes = 16) =>
        new(_gamesRoot, _compressedRoot, gzip, minBytes, NullLogger<GameAssetPrecompressor>.Instance);

    private static GameManifest Manifest(string id) => new(id, id, "index.html", null, 2);

    // Writes a file under games/<id>/<relative> and returns its full path.
    private string WriteGameFile(string id, string relative, string content)
    {
        var path = Path.Combine(_gamesRoot, id, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // Highly compressible payload comfortably above the min-size threshold.
    private static string Filler() => string.Concat(Enumerable.Repeat("hello knockbox world ", 500));

    private static string ReadBrotli(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BrotliStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(br);
        return sr.ReadToEnd();
    }

    private static string ReadGzip(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        return sr.ReadToEnd();
    }

    [Fact]
    public void Compresses_a_compressible_file_to_br_and_gz()
    {
        var content = Filler();
        var src = WriteGameFile("ttt", "game.wasm", content);

        New().ReconcileAll([Manifest("ttt")]);

        var br = Path.Combine(_compressedRoot, "ttt", "game.wasm.br");
        var gz = Path.Combine(_compressedRoot, "ttt", "game.wasm.gz");
        Assert.True(File.Exists(br));
        Assert.True(File.Exists(gz));
        Assert.Equal(content, ReadBrotli(br));
        Assert.Equal(content, ReadGzip(gz));
        // The point of the cache: the variant is meaningfully smaller than the source.
        Assert.True(new FileInfo(br).Length < new FileInfo(src).Length);
    }

    [Fact]
    public void Does_not_emit_gz_when_gzip_disabled()
    {
        WriteGameFile("ttt", "game.js", Filler());

        New(gzip: false).ReconcileAll([Manifest("ttt")]);

        Assert.True(File.Exists(Path.Combine(_compressedRoot, "ttt", "game.js.br")));
        Assert.False(File.Exists(Path.Combine(_compressedRoot, "ttt", "game.js.gz")));
    }

    [Fact]
    public void Skips_incompressible_extension()
    {
        WriteGameFile("ttt", "thumb.png", Filler()); // .png is on the denylist

        New().ReconcileAll([Manifest("ttt")]);

        Assert.False(File.Exists(Path.Combine(_compressedRoot, "ttt", "thumb.png.br")));
    }

    [Fact]
    public void Skips_files_below_min_size()
    {
        WriteGameFile("ttt", "tiny.js", "x");

        New(minBytes: 1024).ReconcileAll([Manifest("ttt")]);

        Assert.False(File.Exists(Path.Combine(_compressedRoot, "ttt", "tiny.js.br")));
    }

    [Fact]
    public void Discards_variant_that_is_not_smaller_than_the_source()
    {
        // Deterministic pseudo-random bytes are effectively incompressible, so .br would be >= source.
        var bytes = new byte[8192];
        new Random(12345).NextBytes(bytes);
        var path = Path.Combine(_gamesRoot, "ttt", "blob.dat"); // .dat is compressible by extension
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        New().ReconcileAll([Manifest("ttt")]);

        Assert.False(File.Exists(Path.Combine(_compressedRoot, "ttt", "blob.dat.br")));
    }

    [Fact]
    public void Regenerates_when_the_source_changes()
    {
        var src = WriteGameFile("ttt", "game.js", Filler());
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);

        var updated = Filler() + "// changed";
        File.WriteAllText(src, updated);
        File.SetLastWriteTimeUtc(src, File.GetLastWriteTimeUtc(src).AddMinutes(5)); // ensure a newer mtime
        pre.ReconcileAll([Manifest("ttt")]);

        Assert.Equal(updated, ReadBrotli(Path.Combine(_compressedRoot, "ttt", "game.js.br")));
    }

    [Fact]
    public void Leaves_an_unchanged_source_alone_on_a_second_reconcile()
    {
        WriteGameFile("ttt", "game.js", Filler());
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);

        // Overwrite the variant with a sentinel; with the source unchanged the index marks it fresh, so a
        // second pass must skip it and leave the sentinel in place rather than recompressing.
        var br = Path.Combine(_compressedRoot, "ttt", "game.js.br");
        File.WriteAllText(br, "SENTINEL");
        pre.ReconcileAll([Manifest("ttt")]);

        Assert.Equal("SENTINEL", File.ReadAllText(br));
    }

    [Fact]
    public void Regenerates_when_content_changes_but_the_timestamp_is_preserved()
    {
        var src = WriteGameFile("ttt", "game.js", Filler());
        var originalMtime = File.GetLastWriteTimeUtc(src);
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);

        // Simulate an in-place/offline edit that changes content (and length) while a timestamp-preserving
        // tool resets the mtime to its old value — the length check must still catch it.
        var updated = Filler() + " // a longer body";
        File.WriteAllText(src, updated);
        File.SetLastWriteTimeUtc(src, originalMtime);
        pre.ReconcileAll([Manifest("ttt")]);

        Assert.Equal(updated, ReadBrotli(Path.Combine(_compressedRoot, "ttt", "game.js.br")));
    }

    [Fact]
    public void Rebuilds_a_hand_deleted_variant_even_when_the_source_is_unchanged()
    {
        WriteGameFile("ttt", "game.js", Filler());
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);

        var br = Path.Combine(_compressedRoot, "ttt", "game.js.br");
        File.Delete(br);
        pre.ReconcileAll([Manifest("ttt")]); // source unchanged, but the variant is gone

        Assert.True(File.Exists(br));
    }

    [Fact]
    public void Removes_the_directory_for_a_deleted_game()
    {
        WriteGameFile("ttt", "game.js", Filler());
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);
        Assert.True(Directory.Exists(Path.Combine(_compressedRoot, "ttt")));

        // Game removed from games/ and the catalog.
        Directory.Delete(Path.Combine(_gamesRoot, "ttt"), recursive: true);
        pre.ReconcileAll([]);

        Assert.False(Directory.Exists(Path.Combine(_compressedRoot, "ttt")));
    }

    [Fact]
    public void Removes_an_orphan_variant_when_its_source_file_is_deleted()
    {
        WriteGameFile("ttt", "a.js", Filler());
        WriteGameFile("ttt", "b.js", Filler());
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);

        File.Delete(Path.Combine(_gamesRoot, "ttt", "a.js"));
        pre.ReconcileAll([Manifest("ttt")]);

        Assert.False(File.Exists(Path.Combine(_compressedRoot, "ttt", "a.js.br")));
        Assert.True(File.Exists(Path.Combine(_compressedRoot, "ttt", "b.js.br")));
    }

    [Fact]
    public void Prunes_gz_variants_once_gzip_is_disabled()
    {
        WriteGameFile("ttt", "game.js", Filler());
        New(gzip: true).ReconcileAll([Manifest("ttt")]);
        Assert.True(File.Exists(Path.Combine(_compressedRoot, "ttt", "game.js.gz")));

        New(gzip: false).ReconcileAll([Manifest("ttt")]);

        Assert.False(File.Exists(Path.Combine(_compressedRoot, "ttt", "game.js.gz")));
        Assert.True(File.Exists(Path.Combine(_compressedRoot, "ttt", "game.js.br")));
    }

    [Fact]
    public void Sweeps_a_stray_temp_file_left_by_a_crashed_write()
    {
        WriteGameFile("ttt", "game.js", Filler());
        var pre = New();
        pre.ReconcileAll([Manifest("ttt")]);

        // Simulate a process killed mid-write: a leftover .tmp next to the real variant.
        var stray = Path.Combine(_compressedRoot, "ttt", "game.js.br.tmp");
        File.WriteAllText(stray, "partial");
        pre.ReconcileAll([Manifest("ttt")]);

        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(Path.Combine(_compressedRoot, "ttt", "game.js.br")));
    }

    [Theory]
    [InlineData("game.js", 5000, 16, true)]
    [InlineData("game.wasm", 5000, 16, true)]
    [InlineData("unknown.xyz", 5000, 16, true)]   // unknown extension → compress (denylist, not allowlist)
    [InlineData("thumb.png", 5000, 16, false)]    // already-compressed format
    [InlineData("sound.mp3", 5000, 16, false)]
    [InlineData("tiny.js", 4, 16, false)]         // below min size
    public void ShouldCompress_applies_min_size_and_denylist(string name, long size, int minBytes, bool expected) =>
        Assert.Equal(expected, GameAssetPrecompressor.ShouldCompress(name, size, minBytes));

    [Theory]
    [InlineData("br, gzip", true, "br")]
    [InlineData("gzip, deflate", true, "gzip")]
    [InlineData("gzip", false, null)]             // gzip disabled and br not offered
    [InlineData("br;q=0, gzip", true, "gzip")]    // br explicitly refused
    [InlineData("identity", true, null)]
    [InlineData("", true, null)]
    [InlineData(null, true, null)]
    public void NegotiateEncoding_prefers_brotli_and_honors_q0(string? accept, bool gzipEnabled, string? expected) =>
        Assert.Equal(expected, GameAssetPrecompressor.NegotiateEncoding(accept, gzipEnabled));
}
