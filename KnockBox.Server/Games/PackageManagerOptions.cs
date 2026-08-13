namespace KnockBox.Server.Games;

/// <summary>Knobs for the managed package root and the install engine that writes to it.</summary>
/// <param name="Enabled">
/// <c>KnockBox:ManagedPackages</c>. Off ⇒ the root is never created and every portal install is refused;
/// packages an operator drops into <c>games/</c> by hand keep working exactly as before.
/// </param>
/// <param name="BackupCount">
/// <c>KnockBox:PackageBackupCount</c> — how many previous versions of each managed package to retain for
/// rollback. <c>0</c> disables backups entirely, which also makes an update a bare atomic move with no
/// copy. One is the default because the overwhelmingly common rollback is "undo the update I just did".
/// </param>
/// <param name="MaxConcurrentInstalls">
/// <c>KnockBox:MaxConcurrentInstalls</c>. Two simultaneous half-gigabyte downloads on a small VPS is not
/// a feature, so this defaults to one; it bounds bandwidth and peak disk, not the number of jobs.
/// </param>
/// <param name="JobRetention"><c>KnockBox:PackageJobRetention</c> — finished jobs kept for the portal to show.</param>
public sealed record PackageManagerOptions(
    bool Enabled = true,
    int BackupCount = 1,
    int MaxConcurrentInstalls = 1,
    int JobRetention = PackageJobRegistry.DefaultRetention)
{
    public static PackageManagerOptions FromConfiguration(IConfiguration config) => new(
        Enabled: config.GetValue("KnockBox:ManagedPackages", true),
        BackupCount: Math.Max(0, config.GetValue("KnockBox:PackageBackupCount", 1)),
        MaxConcurrentInstalls: Math.Max(1, config.GetValue("KnockBox:MaxConcurrentInstalls", 1)),
        JobRetention: Math.Max(4, config.GetValue("KnockBox:PackageJobRetention", PackageJobRegistry.DefaultRetention)));
}
