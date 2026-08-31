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

    /// <summary>
    /// The oldest timestamp a ZIP entry can carry. <see cref="ZipArchiveEntry.LastWriteTime"/> throws
    /// below it, and <see cref="File.GetLastWriteTimeUtc"/> answers 1601-01-01 for a file that no longer
    /// exists — which a rescan or a reinstall running alongside an export makes entirely reachable.
    /// </summary>
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// An export that is fully built and ready to be copied out: the caller knows its name, type and
    /// exact length before writing a single response byte.
    /// </summary>
    public sealed class OpenExport(string fileName, string contentType, long length, Stream content)
        : IAsyncDisposable
    {
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;
        public long Length { get; } = length;
        public Stream Content { get; } = content;

        public ValueTask DisposeAsync() => Content.DisposeAsync();
    }

    /// <summary>
    /// Opens the export for the game at <paramref name="location"/>.
    /// </summary>
    /// <remarks>
    /// Open-then-stream rather than build-into-the-response, for two reasons that are really one. The
    /// directory walk, a per-file read and the zip construction can all fail, and once a response has
    /// started there is no way to say so: the browser saves a truncated archive under HTTP 200, which is
    /// the worst possible outcome for a button offered as "keep a copy before you delete this". Failing
    /// here means the caller has still sent nothing and can answer a clean refusal. It also means the
    /// length is known, so a short read is something the browser can notice.
    ///
    /// The zip is built into a temp FILE, never a MemoryStream: a folder-installed WASM game runs to
    /// hundreds of megabytes, which would otherwise be resident per concurrent export (and throws
    /// outright past int.MaxValue). <see cref="FileOptions.DeleteOnClose"/> on the read handle ties its
    /// lifetime to the response — disposing the <see cref="OpenExport"/> removes it.
    /// </remarks>
    public static async Task<OpenExport> OpenAsync(
        GameCatalog.GameLocation location,
        ContentPaths.Resolved paths,
        CancellationToken cancellationToken = default)
    {
        var id = location.Manifest.Id;

        // Resolved ONCE. Looking it up again to stream after looking it up to name the download let a
        // package removed in between produce a body that disagreed with its own Content-Disposition.
        var package = GamePackageLocations.Find(paths, id);
        if (package is { } found && File.Exists(found.Path))
        {
            var source = new FileStream(
                found.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);

            return new OpenExport($"{id}{GamePackage.Extension}", KbgContentType, source.Length, source);
        }

        var sourceDir = location.Directory;
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Game directory '{sourceDir}' does not exist.");

        var temp = Path.Combine(Path.GetTempPath(), "kb-export-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await using (var writer = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(writer, ZipArchiveMode.Create))
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
                    var stamp = File.GetLastWriteTimeUtc(filePath);
                    entry.LastWriteTime = stamp.Year is < 1980 or > 2107 ? ZipEpoch : new DateTimeOffset(stamp, TimeSpan.Zero);

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

            var read = new FileStream(
                temp,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                bufferSize: 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);

            return new OpenExport($"{id}.zip", ZipContentType, read.Length, read);
        }
        catch
        {
            // Nothing holds the temp file yet (the read handle is the last thing built), so a best-effort
            // delete here is what keeps a failed export from leaving one behind.
            try { File.Delete(temp); } catch { /* best effort */ }
            throw;
        }
    }
}
