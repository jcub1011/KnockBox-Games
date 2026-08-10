using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KnockBox.Server.Games;

namespace KnockBox.Server.Tests;

/// <summary>
/// Builds <c>.kbg</c> packages in memory for tests — including deliberately malformed ones the real
/// packer would refuse to produce, which is exactly what the server's validation must reject.
/// </summary>
internal static class PackageFixture
{
    /// <summary>One file as it appears in the header's <c>files</c> list and in the archive.</summary>
    /// <param name="Path">Logical game-folder path.</param>
    /// <param name="Brotli">Store as a Brotli stream under <c>Path + ".br"</c> rather than raw.</param>
    /// <param name="DeclaredSize">Overrides the size written to the header (for testing mismatches).</param>
    /// <param name="Sha256">Overrides the hash written to the header (for testing corruption).</param>
    internal sealed record File(
        string Path,
        byte[] Data,
        bool Brotli = false,
        long? DeclaredSize = null,
        string? Sha256 = null,
        bool OmitFromHeader = false,
        bool OmitFromArchive = false,
        int ExternalAttributes = 0);

    internal const string DefaultManifest =
        """{"id":"demo","name":"Demo","entry":"index.html","maxPlayers":2}""";

    internal static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A well-formed package: manifest, entry page, and whatever extra files are given.</summary>
    internal static byte[] Valid(string id = "demo", string? name = null, string? version = null, params File[] extra)
    {
        var manifest = id == "demo" && name is null
            ? DefaultManifest
            : $$"""{"id":"{{id}}","name":"{{name ?? id}}","entry":"index.html","maxPlayers":2}""";
        return Build(id, name ?? id, [
            new File("GAME.json", Bytes(manifest)),
            new File("index.html", Bytes("<!doctype html><title>demo</title>")),
            .. extra,
        ], version: version);
    }

    /// <summary>
    /// Builds a package with full control over the header and the archive, so a test can break exactly
    /// one rule. Pass <paramref name="headerJson"/> to replace the generated header wholesale.
    /// </summary>
    internal static byte[] Build(
        string id,
        string name,
        IReadOnlyList<File> files,
        int formatVersion = 1,
        string? version = null,
        string? headerJson = null,
        bool omitHeader = false)
    {
        var rows = new List<string>();
        foreach (var f in files)
        {
            if (f.OmitFromHeader) continue;
            var size = f.DeclaredSize ?? f.Data.Length;
            var sha = f.Sha256 ?? Convert.ToHexStringLower(SHA256.HashData(f.Data));
            // Escape the path properly. Tests deliberately use paths containing backslashes and control
            // characters, and pasting those raw would silently produce a DIFFERENT string than intended
            // (unescaped "a\b" is a backspace, not a backslash) — testing the wrong rule.
            rows.Add($$"""{"path":{{JsonEncode(f.Path)}},"encoding":"{{(f.Brotli ? "br" : "identity")}}","size":{{size}},"sha256":"{{sha}}"}""");
        }

        headerJson ??= $$"""
        {"formatVersion":{{formatVersion}},"id":{{JsonEncode(id)}},"name":{{JsonEncode(name)}},
         {{(version is null ? "" : $"\"version\":\"{version}\",")}}
         "packedBy":"test","packedAt":"2026-08-09T00:00:00Z",
         "files":[{{string.Join(",", rows)}}]}
        """;

        var entries = new List<(string Name, byte[] Data, int Attributes)>();
        if (!omitHeader) entries.Add((GamePackage.HeaderEntryName, Bytes(headerJson), 0));
        foreach (var f in files)
        {
            if (f.OmitFromArchive) continue;
            var payload = f.Brotli ? Compress(f.Data) : f.Data;
            entries.Add((f.Brotli ? f.Path + ".br" : f.Path, payload, f.ExternalAttributes));
        }
        return Zip(entries);
    }

    /// <summary>
    /// The fixed timestamp every fixture entry carries, mirroring the real packer's deterministic
    /// output. Tests assert that extraction carries it onto disk rather than stamping "now".
    /// </summary>
    internal static readonly DateTimeOffset EntryTimestamp = new(1990, 3, 4, 5, 6, 8, TimeSpan.Zero);

    /// <summary>Assembles a ZIP with every entry stored, mirroring what the real packer writes.</summary>
    internal static byte[] Zip(IEnumerable<(string Name, byte[] Data, int Attributes)> entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data, attributes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = EntryTimestamp;
                if (attributes != 0) entry.ExternalAttributes = attributes;
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Minimal JSON string encoder, so a test path lands in the header exactly as written.</summary>
    private static string JsonEncode(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    internal static byte[] Compress(byte[] data)
    {
        var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            brotli.Write(data);
        return output.ToArray();
    }

    /// <summary>Highly compressible filler, comfortably over the 1 KiB compression floor.</summary>
    internal static byte[] Filler(int repeats = 500) =>
        Bytes(string.Concat(Enumerable.Repeat("hello knockbox world ", repeats)));

    /// <summary>Opens a package from bytes for a reader-level test.</summary>
    internal static ZipArchive Open(byte[] package) => new(new MemoryStream(package), ZipArchiveMode.Read);
}
