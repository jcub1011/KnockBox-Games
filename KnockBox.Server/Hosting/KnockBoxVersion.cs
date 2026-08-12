using System.Reflection;
using KnockBox.Server.Marketplace;

namespace KnockBox.Server.Hosting;

/// <summary>
/// This server's own version, as a comparable <see cref="SemVer"/>.
/// </summary>
/// <remarks>
/// Read from the assembly rather than declared as a constant so <c>&lt;Version&gt;</c> in
/// KnockBox.Server.csproj stays the single source of truth — a second copy in code is a copy that
/// eventually disagrees with the one that ships.
///
/// It exists because marketplace catalog entries declare <c>minAppVersion</c>/<c>maxAppVersion</c>:
/// without a host version to compare against there is no way to avoid offering an operator a game
/// their server cannot run.
/// </remarks>
public static class KnockBoxVersion
{
    /// <summary>
    /// The running server's version. Falls back to <c>0.0.0</c> only if the assembly carries no
    /// usable version at all, which reads as "older than any plausible minAppVersion" — the safe
    /// direction, since it withholds games rather than offering ones that may not run.
    /// </summary>
    public static SemVer Current { get; } = Resolve(typeof(KnockBoxVersion).Assembly);

    internal static SemVer Resolve(Assembly assembly)
    {
        // InformationalVersion is the one that carries a prerelease tag ("1.2.0-rc.1"); the SDK also
        // appends "+<commit sha>" via SourceLink, which SemVer.TryParse discards as build metadata.
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVer.TryParse(informational, out var parsed)) return parsed;

        // AssemblyVersion is always present but is four-part (1.2.0.0) and truncates any prerelease
        // tag, so it is the fallback rather than the primary.
        var version = assembly.GetName().Version;
        return version is null ? new SemVer(0, 0, 0, null) : new SemVer(version.Major, version.Minor, version.Build < 0 ? 0 : version.Build, null);
    }
}
