using KnockBox.Server.Games;

namespace KnockBox.Server.Marketplace;

/// <summary>How an installed game compares to what the marketplace currently offers.</summary>
public enum PluginUpdateStatus
{
    /// <summary>The catalog offers it and this server does not have it.</summary>
    NotInstalled,

    /// <summary>Installed at exactly the offered version.</summary>
    UpToDate,

    /// <summary>Installed, and the catalog offers a newer version this server can run.</summary>
    UpdateAvailable,

    /// <summary>
    /// Installed at a HIGHER version than the catalog offers — a local build, or a catalog that has
    /// been rolled back. Reported distinctly so the UI never invites an operator to "update" to
    /// something older than what they are running.
    /// </summary>
    InstalledAhead,

    /// <summary>
    /// Installed, but its <c>GAME.json</c> declares no parseable <c>version</c>, so the two cannot be
    /// compared. Distinct from <see cref="UpdateAvailable"/> on purpose: every hand-made game on the
    /// server is in this state, and nagging that they all need updating would be noise, not signal.
    /// </summary>
    InstalledVersionUnknown,

    /// <summary>
    /// The offered version declares an app-version range this server falls outside of. Takes
    /// precedence over any of the above, so an update that could not run is never presented as one.
    /// </summary>
    Incompatible,

    /// <summary>
    /// The catalog entry itself is unusable — no id, no parseable version, or no supported source.
    /// Kept in the results rather than dropped so a broken published entry is visible to an operator
    /// instead of silently vanishing from the list.
    /// </summary>
    Unusable,
}

/// <summary>One catalog entry, judged against what this server has installed.</summary>
/// <param name="Reason">Operator-facing explanation for the non-obvious statuses; null otherwise.</param>
public sealed record PluginStatus(
    string Id,
    PluginUpdateStatus Status,
    SemVer? Installed,
    SemVer? Available,
    string? Reason,
    MarketplacePlugin Entry);

/// <summary>
/// Decides, for each catalog entry, whether this server's copy is current.
/// </summary>
/// <remarks>
/// Pure: no I/O, no clock, no network — the installed side arrives as
/// <see cref="GameCatalog.GameLocations"/>, which is already an in-memory snapshot. Keeping the
/// judgement separate from <see cref="MarketplaceClient"/> is what makes the whole status matrix
/// testable without a single HTTP call, the same split <c>web/kb-core.js</c> uses for protocol logic.
/// </remarks>
public static class PluginUpdateEvaluator
{
    /// <summary>
    /// Judges every entry in <paramref name="catalog"/> against <paramref name="installed"/>.
    /// Returns one <see cref="PluginStatus"/> per entry, in catalog order.
    /// </summary>
    /// <param name="appVersion">This server's version — normally <see cref="Hosting.KnockBoxVersion.Current"/>.</param>
    public static IReadOnlyList<PluginStatus> Evaluate(
        MarketplaceCatalog catalog,
        IReadOnlyDictionary<string, GameCatalog.GameLocation> installed,
        SemVer appVersion)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(installed);

        if (catalog.Plugins is not { Count: > 0 }) return [];

        var results = new List<PluginStatus>(catalog.Plugins.Count);
        foreach (var plugin in catalog.Plugins)
        {
            if (plugin is not null) results.Add(Evaluate(plugin, installed, appVersion));
        }
        return results;
    }

    /// <summary>Judges a single catalog entry. See <see cref="Evaluate(MarketplaceCatalog, IReadOnlyDictionary{string, GameCatalog.GameLocation}, SemVer)"/>.</summary>
    public static PluginStatus Evaluate(
        MarketplacePlugin plugin,
        IReadOnlyDictionary<string, GameCatalog.GameLocation> installed,
        SemVer appVersion)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(installed);

        var id = plugin.Id ?? "";
        if (string.IsNullOrWhiteSpace(id))
            return new PluginStatus("", PluginUpdateStatus.Unusable, null, null, "the entry declares no id.", plugin);

        if (!SemVer.TryParse(plugin.Version, out var available))
        {
            return new PluginStatus(id, PluginUpdateStatus.Unusable, null, null,
                $"'{plugin.Version}' is not a semantic version.", plugin);
        }

        // The installed version, when there is one. Read before the compatibility check so it can be
        // reported alongside an Incompatible verdict — an operator seeing "incompatible" still wants
        // to know what they are running.
        SemVer? current = null;
        var isInstalled = installed.TryGetValue(id, out var location) && location is not null;
        var installedVersionText = isInstalled ? location!.Manifest.Version : null;
        if (isInstalled) current = SemVer.TryParse(installedVersionText);

        // Compatibility outranks everything: offering an update the server cannot run, or a game it
        // could never load, is worse than saying nothing. Note this gates on the version the catalog
        // OFFERS — an already-installed copy keeps running regardless; this only governs what we
        // would put in front of an operator.
        if (Incompatibility(plugin, appVersion) is { } reason)
            return new PluginStatus(id, PluginUpdateStatus.Incompatible, current, available, reason, plugin);

        if (!isInstalled)
            return new PluginStatus(id, PluginUpdateStatus.NotInstalled, null, available, null, plugin);

        if (current is null)
        {
            return new PluginStatus(id, PluginUpdateStatus.InstalledVersionUnknown, null, available,
                installedVersionText is { Length: > 0 } raw
                    ? $"the installed copy declares version '{raw}', which is not a semantic version."
                    : "the installed copy declares no version, so it cannot be compared.",
                plugin);
        }

        var status = current.Value.CompareTo(available) switch
        {
            0 => PluginUpdateStatus.UpToDate,
            < 0 => PluginUpdateStatus.UpdateAvailable,
            _ => PluginUpdateStatus.InstalledAhead,
        };
        return new PluginStatus(id, status, current, available, null, plugin);
    }

    /// <summary>
    /// Returns why <paramref name="appVersion"/> falls outside the entry's declared app-version
    /// range, or null when it does not. An unparseable bound is itself a reason: a published entry
    /// whose constraint cannot be read must not be treated as unconstrained.
    /// </summary>
    private static string? Incompatibility(MarketplacePlugin plugin, SemVer appVersion)
    {
        if (plugin.MinAppVersion is { Length: > 0 } min)
        {
            if (!SemVer.TryParse(min, out var minimum))
                return $"minAppVersion '{min}' is not a semantic version.";
            if (appVersion < minimum)
                return $"needs KnockBox {minimum} or newer; this server is {appVersion}.";
        }

        if (plugin.MaxAppVersion is { Length: > 0 } max)
        {
            if (!SemVer.TryParse(max, out var maximum))
                return $"maxAppVersion '{max}' is not a semantic version.";
            if (appVersion > maximum)
                return $"supports KnockBox up to {maximum}; this server is {appVersion}.";
        }

        return null;
    }
}
