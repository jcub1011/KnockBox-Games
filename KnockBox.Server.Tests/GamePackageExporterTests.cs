using System.IO.Compression;
using System.Text;
using KnockBox.Contracts;
using KnockBox.Server.Games;
using KnockBox.Server.Hosting;
using Xunit;

namespace KnockBox.Server.Tests;

public sealed class GamePackageExporterTests : IDisposable
{
    private readonly string _temp;
    private readonly ContentPaths.Resolved _paths;

    public GamePackageExporterTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "kb-export-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);

        var games = Path.Combine(_temp, "games");
        var managed = Path.Combine(_temp, "managed");
        var unpacked = Path.Combine(_temp, "games-unpacked");
        var compressed = Path.Combine(_temp, "games-compressed");
        var logs = Path.Combine(_temp, "logs");
        var web = Path.Combine(_temp, "web");
        var blobs = Path.Combine(_temp, "blobs");

        Directory.CreateDirectory(games);
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(unpacked);
        Directory.CreateDirectory(compressed);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(web);
        Directory.CreateDirectory(blobs);

        // Named, not positional. This call used to read
        // `new(web, games, unpacked, compressed, logs, managed)` against a declaration ordered
        // (Web, Games, Logs, GamesCompressed, GamesUnpacked, GamesManaged) -- so LogsRoot pointed at
        // games-unpacked and GamesUnpackedRoot at logs, and it compiled for as long as it existed
        // because all six parameters are `string`. Nothing in this file reads either root, which is
        // why nothing failed.
        _paths = new ContentPaths.Resolved(
            WebRoot: web,
            GamesRoot: games,
            LogsRoot: logs,
            GamesCompressedRoot: compressed,
            GamesUnpackedRoot: unpacked,
            GamesManagedRoot: managed)
        {
            BlobsRoot = blobs,
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task PackageBacked_Game_Exports_As_Kbg()
    {
        var gameId = "pkg-game";
        var pkgBytes = PackageFixture.Valid(gameId, "Pkg Game", "1.0.0");
        var pkgPath = Path.Combine(_paths.GamesManagedRoot, gameId + GamePackage.Extension);
        await File.WriteAllBytesAsync(pkgPath, pkgBytes);

        var location = new GameCatalog.GameLocation(
            new GameManifest(gameId, "Pkg Game", "index.html", null, 2, Version: "1.0.0"),
            Path.Combine(_paths.GamesUnpackedRoot, gameId));

        await using var export = await GamePackageExporter.OpenAsync(location, _paths);
        Assert.Equal("pkg-game.kbg", export.FileName);
        Assert.Equal(GamePackageExporter.KbgContentType, export.ContentType);
        Assert.Equal(pkgBytes.Length, export.Length);
        Assert.Equal(pkgBytes, await ReadAll(export.Content));
    }

    [Fact]
    public async Task PlainFolder_Game_Exports_As_Zip()
    {
        var gameId = "folder-game";
        var gameDir = Path.Combine(_paths.GamesRoot, gameId);
        Directory.CreateDirectory(gameDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "assets"));

        await File.WriteAllTextAsync(Path.Combine(gameDir, "GAME.json"),
            """{"id":"folder-game","name":"Folder Game","entry":"index.html","maxPlayers":4}""");
        await File.WriteAllTextAsync(Path.Combine(gameDir, "index.html"), "<!doctype html><title>Game</title>");
        await File.WriteAllTextAsync(Path.Combine(gameDir, "assets", "data.txt"), "hello from assets");
        // Internal markers that must be excluded from the zip export
        await File.WriteAllTextAsync(Path.Combine(gameDir, PackageMarker.FileName), "dummy marker");
        await File.WriteAllTextAsync(Path.Combine(gameDir, ".kb-precompress.index"), "dummy index");
        await File.WriteAllTextAsync(Path.Combine(gameDir, "temp.tmp"), "dummy temp");

        var location = new GameCatalog.GameLocation(
            new GameManifest(gameId, "Folder Game", "index.html", null, 4),
            gameDir);

        byte[] bytes;
        await using (var export = await GamePackageExporter.OpenAsync(location, _paths))
        {
            Assert.Equal("folder-game.zip", export.FileName);
            Assert.Equal(GamePackageExporter.ZipContentType, export.ContentType);
            bytes = await ReadAll(export.Content);
            // The length the handler puts on Content-Length has to be the length it then writes, or a
            // truncated download is something the browser accepts silently.
            Assert.Equal(export.Length, bytes.Length);
        }

        using var memory = new MemoryStream(bytes);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entryNames = zip.Entries.Select(e => e.FullName).OrderBy(s => s).ToList();

        Assert.Equal(3, entryNames.Count);
        Assert.Contains("GAME.json", entryNames);
        Assert.Contains("index.html", entryNames);
        Assert.Contains("assets/data.txt", entryNames);
        Assert.DoesNotContain(PackageMarker.FileName, entryNames);
        Assert.DoesNotContain(".kb-precompress.index", entryNames);
        Assert.DoesNotContain("temp.tmp", entryNames);

        var htmlEntry = zip.GetEntry("index.html");
        Assert.NotNull(htmlEntry);
        using var reader = new StreamReader(htmlEntry.Open(), Encoding.UTF8);
        var htmlContent = await reader.ReadToEndAsync();
        Assert.Equal("<!doctype html><title>Game</title>", htmlContent);
    }

    [Fact]
    public async Task Missing_GameDirectory_Throws_DirectoryNotFoundException()
    {
        var location = new GameCatalog.GameLocation(
            new GameManifest("missing", "Missing", "index.html", null, 4),
            Path.Combine(_paths.GamesRoot, "missing"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            GamePackageExporter.OpenAsync(location, _paths));
    }

    [Fact]
    public async Task File_Stamped_Before_The_Zip_Epoch_Still_Exports()
    {
        // ZipArchiveEntry.LastWriteTime throws below 1980 — an ArgumentOutOfRangeException that no
        // catch filter on the request path covers. In production the trigger is a file that vanished
        // between the walk and the write, which File.GetLastWriteTimeUtc answers as 1601-01-01; that
        // exact stamp is FILETIME 0, which Windows treats as "leave unchanged", so the fixture uses
        // the last stamp below the clamp instead. Same branch.
        var gameId = "ancient-game";
        var gameDir = Path.Combine(_paths.GamesRoot, gameId);
        Directory.CreateDirectory(gameDir);

        var stale = Path.Combine(gameDir, "index.html");
        await File.WriteAllTextAsync(stale, "<!doctype html>");
        File.SetLastWriteTimeUtc(stale, new DateTime(1979, 12, 31, 23, 59, 58, DateTimeKind.Utc));

        var location = new GameCatalog.GameLocation(
            new GameManifest(gameId, "Ancient Game", "index.html", null, 4),
            gameDir);

        await using var export = await GamePackageExporter.OpenAsync(location, _paths);
        using var memory = new MemoryStream(await ReadAll(export.Content));
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);

        var entry = zip.GetEntry("index.html");
        Assert.NotNull(entry);
        Assert.Equal(1980, entry.LastWriteTime.Year);
    }

    [Fact]
    public async Task File_Stamped_After_The_Zip_MaxYear_Still_Exports()
    {
        var gameId = "future-game";
        var gameDir = Path.Combine(_paths.GamesRoot, gameId);
        Directory.CreateDirectory(gameDir);

        var file = Path.Combine(gameDir, "index.html");
        await File.WriteAllTextAsync(file, "<!doctype html>");
        File.SetLastWriteTimeUtc(file, new DateTime(2150, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var location = new GameCatalog.GameLocation(
            new GameManifest(gameId, "Future Game", "index.html", null, 4),
            gameDir);

        await using var export = await GamePackageExporter.OpenAsync(location, _paths);
        using var memory = new MemoryStream(await ReadAll(export.Content));
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);

        var entry = zip.GetEntry("index.html");
        Assert.NotNull(entry);
        Assert.Equal(1980, entry.LastWriteTime.Year);
    }

    [Fact]
    public async Task Export_Leaves_No_Temp_File_Behind()
    {
        var gameId = "tidy-game";
        var gameDir = Path.Combine(_paths.GamesRoot, gameId);
        Directory.CreateDirectory(gameDir);
        await File.WriteAllTextAsync(Path.Combine(gameDir, "index.html"), "<!doctype html>");

        var location = new GameCatalog.GameLocation(
            new GameManifest(gameId, "Tidy Game", "index.html", null, 4),
            gameDir);

        var before = Directory.EnumerateFiles(Path.GetTempPath(), "kb-export-*.zip").Count();
        await using (var export = await GamePackageExporter.OpenAsync(location, _paths))
        {
            await ReadAll(export.Content);
        }

        Assert.Equal(before, Directory.EnumerateFiles(Path.GetTempPath(), "kb-export-*.zip").Count());
    }
}
