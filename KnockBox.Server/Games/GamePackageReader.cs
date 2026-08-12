using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using KnockBox.Server.Serialization;

namespace KnockBox.Server.Games;

/// <summary>Raised for any package that violates the <c>.kbg</c> contract. The message is operator-facing.</summary>
public sealed class GamePackageException(string message) : Exception(message);

/// <summary>
/// Reads and validates <c>.kbg</c> packages. See <c>docs/KBG_FORMAT.md</c> for the format.
///
/// A package is UNTRUSTED input — it may be hand-crafted to escape its destination, exhaust the disk,
/// or overwrite files. Two rules keep that contained:
///
/// 1. Validation is complete BEFORE any byte is written (<see cref="Read"/> then <see cref="Extract"/>),
///    so a rejected package never leaves a partial tree behind.
/// 2. <c>ZipFile.ExtractToDirectory</c> is deliberately NOT used. It blocks traversal, but it applies
///    no size/entry/ratio caps, cannot pre-validate the header, silently resolves duplicate entry
///    names, and on .NET 7+ restores each entry's stored Unix file mode — letting a hostile package
///    choose the permissions of the files it writes. Entries are iterated by hand instead.
/// </summary>
public static class GamePackageReader
{
    /// <summary>A validated file to extract: where it goes, and which archive entry holds it.</summary>
    /// <param name="LogicalPath">Path relative to the game folder, using <c>/</c> separators.</param>
    /// <param name="Brotli">True when the entry holds a Brotli stream rather than the raw bytes.</param>
    public sealed record PlannedFile(string LogicalPath, ZipArchiveEntry Entry, bool Brotli, long Size, string? Sha256);

    /// <summary>A package that passed every check and is safe to extract.</summary>
    public sealed record PackagePlan(GamePackageHeader Header, string Id, IReadOnlyList<PlannedFile> Files);

    // Windows treats these as device names with or without an extension, so such a path could never be
    // extracted there. Rejecting them keeps a package's behaviour identical across platforms.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Unix mode bits (in the high 16 of ExternalAttributes) marking a symbolic link.
    private const int UnixSymlinkMode = 0xA000;
    private const int UnixFileTypeMask = 0xF000;

    // Longest destination path we'll build. Comfortably under Windows' classic 260-char MAX_PATH once
    // a realistic games root is prepended, and stops a package from being unextractable on one OS only.
    private const int MaxRelativePathLength = 200;

    /// <summary>
    /// Validates a package end to end and returns the extraction plan. Throws
    /// <see cref="GamePackageException"/> with an operator-facing message on the first violation.
    /// Nothing is written and nothing is decompressed here beyond the small header entry.
    /// </summary>
    public static PackagePlan Read(ZipArchive archive, GamePackageLimits limits)
    {
        if (limits.MaxEntries > 0 && archive.Entries.Count > limits.MaxEntries)
        {
            throw new GamePackageException(
                $"package has {archive.Entries.Count} entries, over the {limits.MaxEntries} allowed " +
                "(KnockBox:MaxPackageEntries).");
        }

        var (header, id) = PeekIdentity(archive);
        if (header.Files is null)
            throw new GamePackageException($"{GamePackage.HeaderEntryName} has no 'files' list.");

        // Index the archive so the file list can be matched against it in both directions.
        var byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue; // directory entry: carries no content
            if (entry.IsEncrypted)
                throw new GamePackageException($"entry '{entry.FullName}' is encrypted; encrypted packages are not supported.");
            if (((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymlinkMode)
                throw new GamePackageException($"entry '{entry.FullName}' is a symbolic link, which packages may not contain.");
            if (!byName.TryAdd(entry.FullName, entry))
                throw new GamePackageException($"package contains two entries named '{entry.FullName}'.");
        }

        var files = new List<PlannedFile>(header.Files.Count);
        var claimed = new HashSet<string>(StringComparer.Ordinal) { GamePackage.HeaderEntryName };
        // Ordinal-ignore-case, because two paths differing only in case are the SAME file on Windows
        // and macOS and would silently clobber each other.
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;

        foreach (var row in header.Files)
        {
            var path = ValidateRelativePath(row.Path);
            if (!seenPaths.Add(path))
            {
                throw new GamePackageException(
                    $"{GamePackage.HeaderEntryName} lists '{path}' more than once (paths differing only by case count as " +
                    "the same file).");
            }

            var brotli = row.Encoding switch
            {
                "br" => true,
                "identity" => false,
                _ => throw new GamePackageException(
                    $"unsupported encoding '{row.Encoding}' for '{path}'; expected \"identity\" or \"br\"."),
            };
            if (row.Size < 0) throw new GamePackageException($"'{path}' declares a negative size.");

            var entryName = brotli ? path + GamePackage.BrotliSuffix : path;
            if (!byName.TryGetValue(entryName, out var entry))
                throw new GamePackageException($"{GamePackage.HeaderEntryName} lists '{path}' but the package has no '{entryName}' entry.");
            claimed.Add(entryName);

            declaredTotal += row.Size;
            files.Add(new PlannedFile(path, entry, brotli, row.Size, row.Sha256));
        }

        // The other direction: nothing may ride along unlisted. Without this check an attacker could
        // append entries the header never mentions and have them land in the served game folder.
        foreach (var name in byName.Keys)
        {
            if (!claimed.Contains(name))
                throw new GamePackageException($"package contains '{name}', which is not listed in {GamePackage.HeaderEntryName}.");
        }

        if (!seenPaths.Contains(GamePackage.ManifestEntryName))
            throw new GamePackageException($"package has no root {GamePackage.ManifestEntryName}; every game needs its manifest.");

        if (limits.MaxBytes > 0 && declaredTotal > limits.MaxBytes)
        {
            throw new GamePackageException(
                $"package declares {declaredTotal / (1024 * 1024)} MiB of content, over the " +
                $"{limits.MaxBytes / (1024 * 1024)} MiB allowed (KnockBox:MaxPackageBytes).");
        }
        if (limits.MaxRatio > 0)
        {
            var packed = archive.Entries.Sum(e => e.CompressedLength);
            if (packed > 0 && declaredTotal / packed > limits.MaxRatio)
            {
                throw new GamePackageException(
                    $"package expands {declaredTotal / packed}:1, over the {limits.MaxRatio}:1 allowed " +
                    "(KnockBox:MaxPackageRatio). This looks like a decompression bomb rather than a game.");
            }
        }

        return new PackagePlan(header, id, files);
    }

    /// <summary>
    /// Reads just the header and validates the two things needed to identify a package: that this server
    /// understands its format version, and that its id is a usable directory name.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Read"/> so the installer can answer "is the copy I already installed
    /// still current?" without validating (or decompressing) the whole archive on every reconcile pass.
    /// </remarks>
    public static (GamePackageHeader Header, string Id) PeekIdentity(ZipArchive archive)
    {
        var header = ReadHeader(archive);

        if (header.FormatVersion <= 0)
        {
            throw new GamePackageException(
                $"{GamePackage.HeaderEntryName} has no valid 'formatVersion'; this is not a KnockBox game package.");
        }
        if (header.FormatVersion > GamePackage.MaxFormatVersion)
        {
            throw new GamePackageException(
                $"package declares .kbg format version {header.FormatVersion}, but this server understands only " +
                $"{GamePackage.MaxFormatVersion} — it was packed by a newer version of KnockBox. Upgrade the server.");
        }

        return (header, ValidateId(header.Id));
    }

    private static GamePackageHeader ReadHeader(ZipArchive archive)
    {
        // Read by NAME, not by position. The spec asks writers to put the header first so a file can be
        // sniffed without a ZIP parser, but ZipArchive.Entries follows central-directory order, which is
        // only conventionally the physical order — so a reader must not depend on it.
        var entry = archive.GetEntry(GamePackage.HeaderEntryName)
            ?? throw new GamePackageException(
                $"no {GamePackage.HeaderEntryName} entry; this is not a KnockBox game package (.kbg).");

        try
        {
            using var stream = entry.Open();
            return JsonSerializer.Deserialize(stream, KnockBoxProtocolContext.Default.GamePackageHeader)
                ?? throw new GamePackageException($"{GamePackage.HeaderEntryName} is empty.");
        }
        catch (JsonException ex)
        {
            throw new GamePackageException($"{GamePackage.HeaderEntryName} is not valid JSON: {ex.Message}");
        }
    }

    /// <summary>Validates the package id: one path segment, safe as a directory name on every platform.</summary>
    private static string ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new GamePackageException($"{GamePackage.HeaderEntryName} has no 'id'.");
        if (id.Contains('/') || id.Contains('\\'))
            throw new GamePackageException($"'id' must be a single path segment (no slashes): '{id}'.");
        ValidateSegment(id, id);
        return id;
    }

    /// <summary>
    /// Validates one logical path from the header against docs/KBG_FORMAT.md "Path rules", returning it
    /// with <c>/</c> separators. Syntactic only — <see cref="Extract"/> additionally confirms the
    /// resolved destination stays inside the target directory.
    /// </summary>
    private static string ValidateRelativePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new GamePackageException($"{GamePackage.HeaderEntryName} contains a file with no path.");
        // A ZIP path is '/'-separated by spec. A '\' would be a literal filename character on Linux but a
        // directory separator on Windows, so the same package would extract differently per platform.
        if (raw.Contains('\\'))
            throw new GamePackageException($"path '{raw}' contains a backslash; packages must use '/' separators.");
        if (raw.StartsWith('/'))
            throw new GamePackageException($"path '{raw}' must be relative, not absolute.");
        if (raw.Length > MaxRelativePathLength)
            throw new GamePackageException($"path '{raw}' is longer than the {MaxRelativePathLength} characters allowed.");

        foreach (var segment in raw.Split('/')) ValidateSegment(segment, raw);
        return raw;
    }

    private static void ValidateSegment(string segment, string whole)
    {
        if (segment.Length == 0)
            throw new GamePackageException($"path '{whole}' has an empty segment.");
        if (segment is "." or "..")
            throw new GamePackageException($"path '{whole}' must not contain '.' or '..' segments.");
        // Windows silently trims trailing dots and spaces, so 'a. ' and 'a' would resolve to one file —
        // a way to collide with a path the header lists separately.
        if (segment.EndsWith('.') || segment.EndsWith(' '))
            throw new GamePackageException($"path segment '{segment}' in '{whole}' must not end with a dot or space.");
        // ':' would create an NTFS alternate data stream on 'name', and a naive containment check passes
        // it because the resolved path still starts with the destination prefix.
        if (segment.Contains(':'))
            throw new GamePackageException($"path segment '{segment}' in '{whole}' must not contain ':'.");
        if (segment.AsSpan().IndexOfAny(InvalidNameChars) >= 0)
            throw new GamePackageException($"path segment '{segment}' in '{whole}' contains an invalid filename character.");

        var stem = segment.Contains('.') ? segment[..segment.IndexOf('.')] : segment;
        if (ReservedNames.Contains(stem))
            throw new GamePackageException($"path segment '{segment}' in '{whole}' is a reserved device name on Windows.");
    }

    // Hardcoded to WINDOWS' invalid set plus every control character, rather than
    // Path.GetInvalidFileNameChars(), which is platform-specific: on Unix it is only NUL and '/'. Using
    // the platform's own set would let a package install on Linux and then fail on Windows, making the
    // format's behaviour depend on the host. '/' is handled by segment splitting, while backslash and
    // ':' get their own messages above so an operator sees which rule they broke.
    private static readonly System.Buffers.SearchValues<char> InvalidNameChars =
        System.Buffers.SearchValues.Create(
            "<>\"|?*" + new string([.. Enumerable.Range(0, 32).Select(c => (char)c)]));

    /// <summary>
    /// Decodes just the package's <c>GAME.json</c> into memory, without extracting anything.
    /// </summary>
    /// <remarks>
    /// The marketplace needs to read a downloaded package's declared version before deciding whether
    /// to keep it, and extracting a few hundred megabytes to disk to read one small file would be an
    /// absurd way to answer that. <see cref="Read"/> has already proved the entry exists and closed
    /// the header against the archive, so the only new risk here is expansion size — capped at
    /// <see cref="MaxManifestBytes"/>, well above any real manifest and far below anything that could
    /// pressure memory.
    /// </remarks>
    /// <exception cref="GamePackageException">The manifest is missing, oversized, or corrupt.</exception>
    public static byte[] ReadManifestBytes(PackagePlan plan)
    {
        var manifest = plan.Files.FirstOrDefault(f =>
            f.LogicalPath.Equals(GamePackage.ManifestEntryName, StringComparison.Ordinal))
            ?? throw new GamePackageException($"the package contains no {GamePackage.ManifestEntryName}.");

        if (manifest.Size > MaxManifestBytes)
        {
            throw new GamePackageException(
                $"{GamePackage.ManifestEntryName} declares {manifest.Size} bytes, over the {MaxManifestBytes}-byte limit.");
        }

        using var entryStream = manifest.Entry.Open();
        using Stream source = manifest.Brotli ? new BrotliStream(entryStream, CompressionMode.Decompress) : entryStream;
        using var buffer = new MemoryStream();

        // Read one byte past the cap so an entry that lies about its size is caught here rather than
        // being allowed to expand freely — declared sizes are attacker-controlled (see CopyCounted).
        var chunk = new byte[8192];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxManifestBytes)
            {
                throw new GamePackageException(
                    $"{GamePackage.ManifestEntryName} expands past the {MaxManifestBytes}-byte limit.");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Ceiling on an in-memory <c>GAME.json</c>. Real manifests are well under a kilobyte.</summary>
    private const int MaxManifestBytes = 256 * 1024;

    /// <summary>
    /// Extracts a validated plan into <paramref name="destination"/>, which must be empty. Verifies each
    /// file's byte count (and SHA-256 when the package declares one) as it copies, and enforces
    /// <paramref name="limits"/> against bytes ACTUALLY written rather than the sizes the package claims.
    /// </summary>
    /// <returns>The logical paths written, in plan order.</returns>
    public static IReadOnlyList<string> Extract(
        PackagePlan plan, string destination, GamePackageLimits limits, CancellationToken cancellationToken = default)
    {
        var destFull = Path.GetFullPath(destination);
        var destPrefix = destFull.EndsWith(Path.DirectorySeparatorChar) ? destFull : destFull + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destFull);

        var written = new List<string>(plan.Files.Count);
        long total = 0;

        foreach (var file in plan.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Belt and braces: the segment checks above already make traversal impossible, but the
            // resolved-path test is the invariant that actually matters, so assert it too. Same shape as
            // the entry-path guard in GameCatalog.
            var target = Path.GetFullPath(Path.Combine(destFull, file.LogicalPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destPrefix, StringComparison.OrdinalIgnoreCase))
                throw new GamePackageException($"'{file.LogicalPath}' would be written outside the game folder.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            long copied;
            byte[] hash;
            using (var entryStream = file.Entry.Open())
            using (Stream source = file.Brotli ? new BrotliStream(entryStream, CompressionMode.Decompress) : entryStream)
            using (var output = File.Create(target))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                copied = CopyCounted(source, output, hasher, limits.MaxBytes, total, file.LogicalPath, cancellationToken);
                hash = hasher.GetHashAndReset();
            }
            total += copied;

            if (copied != file.Size)
            {
                throw new GamePackageException(
                    $"'{file.LogicalPath}' expanded to {copied} bytes but the package declares {file.Size}.");
            }
            if (file.Sha256 is not null && !Convert.ToHexStringLower(hash).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new GamePackageException($"'{file.LogicalPath}' failed its SHA-256 check; the package is corrupt.");

            // Carry the package's timestamps onto disk. Deterministic mtimes are what let the
            // pre-compressed cache treat a reinstalled-but-unchanged file as fresh instead of
            // recompressing it at maximum effort.
            try { File.SetLastWriteTimeUtc(target, file.Entry.LastWriteTime.UtcDateTime); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // A refused or out-of-range timestamp only costs one extra compression pass later.
            }

            written.Add(file.LogicalPath);
        }

        return written;
    }

    /// <summary>
    /// Copies while counting and hashing, aborting the moment the running total exceeds the cap. The
    /// cap CANNOT be pre-checked from the header: declared sizes are attacker-controlled, so a bomb
    /// declares a small size and streams gigabytes.
    /// </summary>
    private static long CopyCounted(
        Stream source, Stream destination, IncrementalHash hasher,
        long maxBytes, long alreadyWritten, string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            copied += read;
            if (maxBytes > 0 && alreadyWritten + copied > maxBytes)
            {
                throw new GamePackageException(
                    $"package exceeds the {maxBytes / (1024 * 1024)} MiB limit while expanding '{path}' " +
                    "(KnockBox:MaxPackageBytes). This looks like a decompression bomb rather than a game.");
            }
            destination.Write(buffer, 0, read);
            hasher.AppendData(buffer, 0, read);
        }
        return copied;
    }
}
