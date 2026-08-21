using System.Text;
using KnockBox.Server.Games;
using Xunit;
using File = KnockBox.Server.Tests.PackageFixture.File;

namespace KnockBox.Server.Tests;

/// <summary>
/// Validation-level tests for the <c>.kbg</c> reader. A package is untrusted input, so these cover the
/// hostile cases: escaping the destination, lying about sizes, smuggling entries past the header, and
/// decompression bombs. Lifecycle behaviour lives in <see cref="GamePackageInstallerTests"/>.
/// </summary>
public class GamePackageReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kb-pkgread-" + Guid.NewGuid().ToString("N"));

    public GamePackageReaderTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ } }

    private static readonly GamePackageLimits Generous = new(100L * 1024 * 1024, 1000, 10_000);

    private static GamePackageReader.PackagePlan Read(byte[] package, GamePackageLimits? limits = null)
    {
        using var archive = PackageFixture.Open(package);
        return GamePackageReader.Read(archive, limits ?? Generous);
    }

    private static string Message(byte[] package, GamePackageLimits? limits = null) =>
        Assert.Throws<GamePackageException>(() => Read(package, limits)).Message;

    /// <summary>Reads a package all the way to disk and returns the destination directory.</summary>
    private string Extract(byte[] package, GamePackageLimits? limits = null)
    {
        using var archive = PackageFixture.Open(package);
        var plan = GamePackageReader.Read(archive, limits ?? Generous);
        var dest = Path.Combine(_root, "out");
        GamePackageReader.Extract(plan, dest, limits ?? Generous);
        return dest;
    }

    // ── Happy path ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_a_valid_package()
    {
        var plan = Read(PackageFixture.Valid("demo", "Demo", "1.2.3"));

        Assert.Equal("demo", plan.Id);
        Assert.Equal(1, plan.Header.FormatVersion);
        Assert.Equal("1.2.3", plan.Header.Version);
        Assert.Equal(["GAME.json", "index.html"], plan.Files.Select(f => f.LogicalPath).Order());
    }

    [Fact]
    public void Extracts_identity_and_brotli_payloads_to_identical_bytes()
    {
        var payload = PackageFixture.Filler();
        var dest = Extract(PackageFixture.Valid("demo", null, null,
            new File("assets/big.js", payload, Brotli: true),
            new File("assets/small.txt", PackageFixture.Bytes("plain"))));

        Assert.Equal(payload, System.IO.File.ReadAllBytes(Path.Combine(dest, "assets", "big.js")));
        Assert.Equal("plain", System.IO.File.ReadAllText(Path.Combine(dest, "assets", "small.txt")));
        // The .br entry name is an archive detail; the extracted file carries the logical name.
        Assert.False(System.IO.File.Exists(Path.Combine(dest, "assets", "big.js.br")));
    }

    [Fact]
    public void Extraction_preserves_package_timestamps()
    {
        // Deterministic mtimes are what let the pre-compressed cache skip an unchanged reinstalled file
        // instead of recompressing it at maximum effort — so extraction must carry the package's own
        // timestamp across rather than stamping "now".
        //
        // Compared against the archive entry's value rather than a literal: ZIP stores DOS wall-clock
        // with no timezone, so the absolute instant depends on the host's zone. Determinism is the
        // property that matters, not any particular UTC value.
        var package = PackageFixture.Valid();
        DateTime expected;
        using (var archive = PackageFixture.Open(package))
            expected = archive.GetEntry("index.html")!.LastWriteTime.UtcDateTime;

        var dest = Extract(package);
        var written = System.IO.File.GetLastWriteTimeUtc(Path.Combine(dest, "index.html"));

        Assert.Equal(expected, written);
        Assert.NotEqual(DateTime.UtcNow.Date, written.Date); // definitely not "now"
    }

    [Fact]
    public void Preserves_an_empty_file()
    {
        var dest = Extract(PackageFixture.Valid("demo", null, null, new File("empty.dat", [])));
        Assert.Equal(0, new FileInfo(Path.Combine(dest, "empty.dat")).Length);
    }

    // ── Format version ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_a_newer_format_version_with_an_upgrade_hint()
    {
        var package = PackageFixture.Build("demo", "Demo",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], formatVersion: 2);

        Assert.Contains("packed by a newer version of KnockBox", Message(package));
    }

    [Fact]
    public void Rejects_a_missing_or_zero_format_version()
    {
        var package = PackageFixture.Build("demo", "Demo",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))],
            headerJson: """{"id":"demo","name":"Demo","files":[]}""");

        Assert.Contains("no valid 'formatVersion'", Message(package));
    }

    [Fact]
    public void Rejects_a_package_with_no_header()
    {
        var package = PackageFixture.Build("demo", "Demo",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))], omitHeader: true);

        Assert.Contains("not a KnockBox game package", Message(package));
    }

    [Fact]
    public void Rejects_a_header_that_is_not_json()
    {
        var package = PackageFixture.Zip([
            (GamePackage.HeaderEntryName, PackageFixture.Bytes("{ this is not json"), 0),
            ("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest), 0),
        ]);

        Assert.Contains("not valid JSON", Message(package));
    }

    // ── The file list is closed in both directions ────────────────────────────────────────────────

    [Fact]
    public void Rejects_an_archive_entry_that_the_header_does_not_list()
    {
        // Without this check, entries appended after packing would land in the served game folder.
        var package = PackageFixture.Valid("demo", null, null, new File("sneaky.js", PackageFixture.Bytes("evil()"), OmitFromHeader: true));

        Assert.Contains("not listed in KBG.json", Message(package));
    }

    [Fact]
    public void Rejects_a_listed_file_with_no_matching_entry()
    {
        var package = PackageFixture.Valid("demo", null, null, new File("ghost.js", PackageFixture.Bytes("gone"), OmitFromArchive: true));

        Assert.Contains("has no 'ghost.js' entry", Message(package));
    }

    [Fact]
    public void Rejects_a_package_with_no_manifest()
    {
        var package = PackageFixture.Build("demo", "Demo", [new File("index.html", PackageFixture.Bytes("<html>"))]);

        Assert.Contains("no root GAME.json", Message(package));
    }

    // ── Path rules ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("../evil.txt", "'.' or '..'")]
    [InlineData("a/../../evil.txt", "'.' or '..'")]
    [InlineData("/etc/passwd", "must be relative")]
    [InlineData("a\\b.txt", "backslash")]
    [InlineData("foo:bar", "must not contain ':'")]
    [InlineData("C:/windows/x", "must not contain ':'")]
    [InlineData("a//b.txt", "empty segment")]
    [InlineData("a/./b.txt", "'.' or '..'")]
    [InlineData("trailing.", "dot or space")]
    [InlineData("NUL", "reserved device name")]
    [InlineData("com1.txt", "reserved device name")]
    [InlineData("star*.txt", "invalid filename character")]
    [InlineData("pipe|.txt", "invalid filename character")]
    public void Rejects_an_unsafe_path(string path, string expected)
    {
        var package = PackageFixture.Valid("demo", null, null, new File(path, PackageFixture.Bytes("x")));
        Assert.Contains(expected, Message(package));
    }

    [Fact]
    public void A_rejected_package_writes_nothing_at_all()
    {
        // The traversal target is placed OUTSIDE the destination; if any byte escaped, this file would
        // be overwritten. Validation completing before extraction begins is what guarantees it isn't.
        var canary = Path.Combine(_root, "canary.txt");
        System.IO.File.WriteAllText(canary, "untouched");
        var dest = Path.Combine(_root, "out");

        var package = PackageFixture.Valid("demo", null, null, new File("../canary.txt", PackageFixture.Bytes("OVERWRITTEN")));
        Assert.Throws<GamePackageException>(() => Read(package));

        Assert.Equal("untouched", System.IO.File.ReadAllText(canary));
        Assert.False(Directory.Exists(dest));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("NUL")]
    public void Rejects_an_unusable_id(string id)
    {
        var package = PackageFixture.Build(id, "Demo",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))]);

        Assert.Throws<GamePackageException>(() => Read(package));
    }

    [Fact]
    public void Rejects_paths_differing_only_by_case()
    {
        // Distinct entries in the archive, but the same file on Windows and macOS.
        var package = PackageFixture.Valid("demo", null, null,
            new File("Script.js", PackageFixture.Bytes("a")),
            new File("script.js", PackageFixture.Bytes("b")));

        Assert.Contains("more than once", Message(package));
    }

    [Fact]
    public void Rejects_a_symlink_entry()
    {
        // Unix mode S_IFLNK in the high 16 bits of the external attributes.
        var package = PackageFixture.Valid("demo", null, null,
            new File("link", PackageFixture.Bytes("/etc/passwd"), ExternalAttributes: 0xA1FF << 16));

        Assert.Contains("symbolic link", Message(package));
    }

    [Fact]
    public void Never_applies_an_entrys_stored_file_mode()
    {
        if (OperatingSystem.IsWindows()) return; // POSIX permission bits only

        // A package must not get to choose the permissions of what it writes. 0777 here would be
        // world-writable if the mode were honoured.
        var dest = Extract(PackageFixture.Valid("demo", null, null,
            new File("script.sh", PackageFixture.Bytes("#!/bin/sh\n"), ExternalAttributes: 0x81FF << 16)));

        var mode = System.IO.File.GetUnixFileMode(Path.Combine(dest, "script.sh"));
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        Assert.False(mode.HasFlag(UnixFileMode.GroupWrite));
    }

    // ── Integrity ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_content_whose_size_does_not_match_the_header()
    {
        var package = PackageFixture.Valid("demo", null, null,
            new File("data.bin", PackageFixture.Bytes("actually longer"), DeclaredSize: 3));

        Assert.Contains("declares 3", Assert.Throws<GamePackageException>(() => Extract(package)).Message);
    }

    [Fact]
    public void Rejects_content_that_fails_its_hash()
    {
        var package = PackageFixture.Valid("demo", null, null,
            new File("data.bin", PackageFixture.Bytes("tampered"), Sha256: new string('0', 64)));

        Assert.Contains("failed its SHA-256", Assert.Throws<GamePackageException>(() => Extract(package)).Message);
    }

    [Fact]
    public void Rejects_an_unsupported_encoding()
    {
        var package = PackageFixture.Build("demo", "Demo",
            [new File("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest))],
            headerJson: """
            {"formatVersion":1,"id":"demo","name":"Demo",
             "files":[{"path":"GAME.json","encoding":"gzip","size":10}]}
            """);

        Assert.Contains("unsupported encoding", Message(package));
    }

    // ── Resource ceilings ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rejects_too_many_entries()
    {
        var limits = Generous with { MaxEntries = 2 };
        Assert.Contains("over the 2 allowed", Message(PackageFixture.Valid(), limits));
    }

    [Fact]
    public void Rejects_a_package_declaring_more_content_than_allowed()
    {
        var limits = Generous with { MaxBytes = 1024 };
        var package = PackageFixture.Valid("demo", null, null, new File("big.bin", new byte[4096]));

        Assert.Contains("MaxPackageBytes", Message(package, limits));
    }

    [Fact]
    public void Enforces_the_byte_cap_against_actual_content_not_the_declared_size()
    {
        // The bomb case: declare a tiny size so the pre-check passes, then stream far more. Only counting
        // bytes AS THEY ARE COPIED catches this, because declared sizes are attacker-controlled.
        var limits = Generous with { MaxBytes = 4096 };
        var package = PackageFixture.Valid("demo", null, null,
            new File("bomb.bin", new byte[64 * 1024], Brotli: true, DeclaredSize: 10));

        Assert.Contains("MaxPackageBytes", Assert.Throws<GamePackageException>(() => Extract(package, limits)).Message);
    }

    [Fact]
    public void Rejects_an_implausible_expansion_ratio()
    {
        var limits = Generous with { MaxRatio = 50 };
        // Zeroes compress to almost nothing, so this is a genuine several-thousand-to-one expansion.
        var package = PackageFixture.Valid("demo", null, null, new File("bomb.bin", new byte[8 * 1024 * 1024], Brotli: true));

        Assert.Contains("MaxPackageRatio", Message(package, limits));
    }

    [Fact]
    public void A_limit_of_zero_disables_that_check()
    {
        var limits = new GamePackageLimits(0, 0, 0);
        var plan = Read(PackageFixture.Valid("demo", null, null, new File("big.bin", new byte[4096])), limits);
        Assert.Equal(3, plan.Files.Count);
    }

    // ── Corruption ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_truncated_archive_is_not_a_readable_zip()
    {
        var package = PackageFixture.Valid();
        var truncated = package[..(package.Length - 16)];

        // A ZIP's central directory sits at the end, so a partial copy reliably fails to open at all.
        Assert.ThrowsAny<Exception>(() =>
        {
            using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(truncated), System.IO.Compression.ZipArchiveMode.Read);
            return GamePackageReader.Read(archive, Generous);
        });
    }

    [Fact]
    public void A_corrupt_brotli_payload_is_a_package_failure_rather_than_an_unhandled_decompression_error()
    {
        // ReadManifestBytes documents GamePackageException for "corrupt", but BrotliStream throws
        // InvalidDataException — neither that nor IOException, so it escaped every caller's catch: the
        // upload route answered an unhandled 500 with no reason for the operator, and skipped disposing a
        // staged file that may be hundreds of megabytes.
        var package = PackageFixture.CorruptBrotli();

        using var archive = PackageFixture.Open(package);
        var plan = GamePackageReader.Read(archive, Generous);   // structurally valid: only the bytes rot

        var ex = Assert.Throws<GamePackageException>(() => GamePackageReader.ReadManifestBytes(plan));
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_corrupt_payload_fails_extraction_the_same_way()
    {
        var package = PackageFixture.CorruptBrotli(corruptFile: "index.html");
        var destination = Path.Combine(Path.GetTempPath(), $"kb-corrupt-{Guid.NewGuid():N}");

        try
        {
            using var archive = PackageFixture.Open(package);
            var plan = GamePackageReader.Read(archive, Generous);

            var ex = Assert.Throws<GamePackageException>(
                () => GamePackageReader.Extract(plan, destination, Generous));
            Assert.Contains("index.html", ex.Message);
        }
        finally
        {
            try { Directory.Delete(destination, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void PeekIdentity_reads_the_id_without_validating_the_file_list()
    {
        // The installer uses this to answer "is what I already installed still current?" cheaply. It must
        // work even when the rest of the package would be rejected.
        var package = PackageFixture.Valid("demo", null, null, new File("sneaky.js", PackageFixture.Bytes("x"), OmitFromHeader: true));

        using var archive = PackageFixture.Open(package);
        var (header, id) = GamePackageReader.PeekIdentity(archive);

        Assert.Equal("demo", id);
        Assert.Equal(1, header.FormatVersion);
        Assert.Throws<GamePackageException>(() => GamePackageReader.Read(archive, Generous));
    }

    [Fact]
    public void Header_is_read_by_name_not_by_position()
    {
        // The spec asks writers to put KBG.json first so a file can be sniffed without a ZIP parser, but
        // central-directory order is only conventionally the physical order, so readers must not rely on it.
        var package = PackageFixture.Zip([
            ("GAME.json", PackageFixture.Bytes(PackageFixture.DefaultManifest), 0),
            (GamePackage.HeaderEntryName, Encoding.UTF8.GetBytes(
                $$"""
                {"formatVersion":1,"id":"demo","name":"Demo","files":[
                  {"path":"GAME.json","encoding":"identity","size":{{PackageFixture.DefaultManifest.Length}}}]}
                """), 0),
        ]);

        Assert.Equal("demo", Read(package).Id);
    }
}
