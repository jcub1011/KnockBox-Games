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

        Directory.CreateDirectory(games);
        Directory.CreateDirectory(managed);
        Directory.CreateDirectory(unpacked);
        Directory.CreateDirectory(compressed);
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(web);

        _paths = new ContentPaths.Resolved(web, games, unpacked, compressed, logs, managed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
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

        var info = GamePackageExporter.GetExportInfo(location, _paths);
        Assert.Equal("pkg-game.kbg", info.FileName);
        Assert.Equal(GamePackageExporter.KbgContentType, info.ContentType);

        using var memory = new MemoryStream();
        var result = await GamePackageExporter.ExportAsync(location, _paths, memory);
        Assert.Equal("pkg-game.kbg", result.FileName);
        Assert.Equal(GamePackageExporter.KbgContentType, result.ContentType);
        Assert.Equal(pkgBytes, memory.ToArray());
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

        var info = GamePackageExporter.GetExportInfo(location, _paths);
        Assert.Equal("folder-game.zip", info.FileName);
        Assert.Equal(GamePackageExporter.ZipContentType, info.ContentType);

        using var memory = new MemoryStream();
        var result = await GamePackageExporter.ExportAsync(location, _paths, memory);
        Assert.Equal("folder-game.zip", result.FileName);
        Assert.Equal(GamePackageExporter.ZipContentType, result.ContentType);

        memory.Position = 0;
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

        using var memory = new MemoryStream();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            GamePackageExporter.ExportAsync(location, _paths, memory));
    }

    [Fact]
    public async Task PlainFolder_Game_Exports_To_AsyncOnly_Stream()
    {
        var gameId = "async-folder-game";
        var gameDir = Path.Combine(_paths.GamesRoot, gameId);
        Directory.CreateDirectory(gameDir);

        await File.WriteAllTextAsync(Path.Combine(gameDir, "GAME.json"),
            """{"id":"async-folder-game","name":"Async Folder Game","entry":"index.html","maxPlayers":4}""");
        await File.WriteAllTextAsync(Path.Combine(gameDir, "index.html"), "<!doctype html><title>Game</title>");

        var location = new GameCatalog.GameLocation(
            new GameManifest(gameId, "Async Folder Game", "index.html", null, 4),
            gameDir);

        using var memory = new MemoryStream();
        using var asyncOnly = new AsyncOnlyStream(memory);
        var result = await GamePackageExporter.ExportAsync(location, _paths, asyncOnly);
        Assert.Equal("async-folder-game.zip", result.FileName);
        Assert.Equal(GamePackageExporter.ZipContentType, result.ContentType);
        Assert.True(memory.Length > 0);
    }

    private sealed class AsyncOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Sync read disallowed");
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new InvalidOperationException("Synchronous operations are disallowed.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);
    }
}
