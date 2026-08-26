namespace KnockBox.Server.Hosting;

/// <summary>
/// Answers "can this server actually write here?" by writing there.
///
/// Create-then-delete rather than inspecting permission bits: on a read-only bind mount — the shipped
/// deployment's <c>games/</c> — the bits can look perfectly writable and the write still fails, which is
/// precisely the case every caller of this exists to detect.
/// </summary>
/// <remarks>
/// One implementation, and one <see cref="FilePrefix"/>, because the probe file is itself an event the
/// rest of the server sees: <c>GameCatalog</c>'s watcher covers the games root and would otherwise treat
/// every probe as a change worth rediscovering the catalog for. It ignores this prefix, so the two must
/// agree — and they can only be relied on to agree if there is one of each.
/// </remarks>
public static class DirectoryProbe
{
    /// <summary>The name every probe file starts with. Referenced by <c>GameCatalog</c>'s watcher.</summary>
    public const string FilePrefix = ".kb-write-probe-";

    /// <summary>True when <paramref name="name"/> is one of our own probe files.</summary>
    public static bool IsProbeFile(string? name) =>
        name is not null && name.StartsWith(FilePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Null when a file can be created and removed in <paramref name="directory"/>, otherwise an
    /// operator-facing reason why not.
    /// </summary>
    public static string? WhyNotWritable(string directory)
    {
        if (!Directory.Exists(directory)) return $"'{directory}' does not exist.";

        var probe = Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"'{directory}' is not writable by the server ({ex.Message}).";
        }
    }

    /// <summary>
    /// The same question about the directory an entry would have to be removed FROM: deleting a file or
    /// folder needs write access to its parent, not to itself.
    /// </summary>
    public static string? WhyParentNotWritable(string target)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(target));
        return string.IsNullOrEmpty(parent)
            ? $"'{target}' has no parent directory."
            : WhyNotWritable(parent);
    }
}
