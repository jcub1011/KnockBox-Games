namespace KnockBox.Server.Games;

/// <summary>
/// The on-disk shape of the managed package root — the writable folder the admin portal installs into.
/// </summary>
/// <remarks>
/// <code>
/// games-managed/
///   &lt;id&gt;.kbg                                    one package per game id, always named for the id
///   .staging/                                    downloads and uploads in flight
///   .backups/&lt;id&gt;/&lt;ticks&gt;-&lt;version&gt;-&lt;sha12&gt;.kbg  retained previous versions, for rollback
/// </code>
/// Both subdirectories are dot-prefixed, and <see cref="GamePackageInstaller"/> enumerates
/// <c>*.kbg</c> non-recursively, so neither is ever mistaken for a package to install.
///
/// The canonical <c>&lt;id&gt;.kbg</c> naming is a deliberate constraint on this root (the wider installer
/// accepts any file name). It makes id → file derivable, makes an update an overwrite rather than an
/// accumulation, and makes two packages claiming one id impossible within the root.
/// </remarks>
public static class ManagedPackageLayout
{
    public const string StagingDirName = ".staging";
    public const string BackupsDirName = ".backups";

    /// <summary>The canonical package path for <paramref name="id"/> inside the managed root.</summary>
    public static string PackagePath(string managedRoot, string id) =>
        Path.Combine(managedRoot, id + GamePackage.Extension);

    public static string StagingDir(string managedRoot) => Path.Combine(managedRoot, StagingDirName);

    public static string BackupDir(string managedRoot, string id) =>
        Path.Combine(managedRoot, BackupsDirName, id);
}
