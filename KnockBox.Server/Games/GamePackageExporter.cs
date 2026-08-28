using System.IO.Compression;
using KnockBox.Server.Hosting;

namespace KnockBox.Server.Games;

/// <summary>
/// Exports an installed game for archiving or transfer:
/// - If the game was installed from a <c>.kbg</c> package (marketplace or manual upload), streams the original package file.
/// - If the game was installed from a plain directory, creates and streams a standard <c>.zip</c> archive of the directory.
/// </summary>
public static class GamePackageExporter
{
    public const string KbgContentType = "application/vnd.knockbox.game+zip";
    public const string ZipContentType = "application/zip";

    public readonly record struct ExportInfo(string FileName, string ContentType);

    /// <summary>
    /// Determines the export filename and content type for a game.
    /// </summary>
    public static ExportInfo GetExportInfo(GameCatalog.GameLocation location, ContentPaths.Resolved paths)
    {
        var id = location.Manifest.Id;
        var package = GamePackageLocations.Find(paths, id);
        if (package is { } found && File.Exists(found.Path))
        {
            return new ExportInfo($"{id}{GamePackage.Extension}", KbgContentType);
        }

        return new ExportInfo($"{id}.zip", ZipContentType);
    }

    /// <summary>
    /// Exports the game at <paramref name="location"/> to <paramref name="destination"/>.
    /// </summary>
    public static async Task<ExportInfo> ExportAsync(
        GameCatalog.GameLocation location,
        ContentPaths.Resolved paths,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        var id = location.Manifest.Id;
        var package = GamePackageLocations.Find(paths, id);

        if (package is { } found && File.Exists(found.Path))
        {
            await using var source = new FileStream(
                found.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);

            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            return new ExportInfo($"{id}{GamePackage.Extension}", KbgContentType);
        }

        var sourceDir = location.Directory;
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Game directory '{sourceDir}' does not exist.");

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var filePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(sourceDir, filePath);

                // Skip internal metadata/marker files
                var fileName = Path.GetFileName(filePath);
                if (fileName is PackageMarker.FileName or ".kb-precompress.index"
                    || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entryName = relative.Replace('\\', '/');
                var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTimeUtc(filePath);

                await using var fileStream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    bufferSize: 81920,
                    useAsync: true);

                await using var entryStream = entry.Open();
                await fileStream.CopyToAsync(entryStream, cancellationToken).ConfigureAwait(false);
            }
        }

        memory.Position = 0;
        await memory.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

        return new ExportInfo($"{id}.zip", ZipContentType);
    }
}
