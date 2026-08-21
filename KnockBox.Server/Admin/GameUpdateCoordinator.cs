using KnockBox.Server.Games;
using KnockBox.Server.Marketplace;

namespace KnockBox.Server.Admin;

/// <summary>
/// The policy layer over the install engine: which games this server may update on its own, and how
/// often it looks.
/// </summary>
/// <remarks>
/// It owns no file handling and no swapping — <see cref="PackageManager"/> does all of that, including
/// the three apply modes. What lives here is the decision: fetch the catalogs on a schedule, ask
/// <see cref="PluginUpdateEvaluator"/> what is genuinely newer, and start a job for each game the
/// operator enrolled.
///
/// Nothing is enrolled by default. An operator who has not asked for automatic updates gets none, and
/// the portal simply reports what is available.
/// </remarks>
public sealed class GameUpdateCoordinator(
    MarketplaceSourceRegistry registry,
    PackageManager packages,
    GameCatalog catalog,
    AdminSettingsStore settings,
    ILogger<GameUpdateCoordinator> logger)
{
    /// <summary>How many jobs a pass started, and how many candidates it looked at.</summary>
    public readonly record struct PassResult(int Started, int Considered, string? Error);

    /// <summary>
    /// Checks every registered catalog and starts an update for each enrolled game that needs one.
    /// </summary>
    /// <remarks>
    /// A game already holding a job is skipped rather than queued: the next pass will see it again, and
    /// queueing would let a slow drain stack up behind itself.
    ///
    /// <see cref="PluginUpdateStatus.Incompatible"/> outranks an available update, so a game whose new
    /// version cannot run on this server is never started — <see cref="PluginUpdateEvaluator"/> has
    /// already made that call, and this only acts on <see cref="PluginUpdateStatus.UpdateAvailable"/>.
    /// </remarks>
    public async Task<PassResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var enrolled = settings.UpdatePolicies;
        // Nothing enrolled ⇒ no reason to touch the network at all. This is the overwhelmingly common
        // case, and it is what keeps a default deployment from making a scheduled outbound request it
        // has no use for.
        if (enrolled.Count == 0) return new PassResult(0, 0, null);

        IReadOnlyList<SourceCatalog> fetched;
        try
        {
            fetched = await registry.FetchAllAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The scheduled marketplace check could not fetch any catalog.");
            return new PassResult(0, 0, ex.Message);
        }

        var installed = catalog.GameLocations;
        var started = 0;
        var considered = 0;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in fetched)
        {
            if (source.Catalog is null) continue;

            foreach (var status in PluginUpdateEvaluator.Evaluate(source.Catalog, installed, Hosting.KnockBoxVersion.Current))
            {
                var id = status.Entry.Id ?? "";
                // First source to offer an id wins, matching how the portal's merged view resolves it.
                if (id.Length == 0 || !claimed.Add(id)) continue;
                if (status.Status != PluginUpdateStatus.UpdateAvailable) continue;

                var policy = settings.GetUpdatePolicy(id);
                if (policy == UpdatePolicy.Manual) continue;
                considered++;

                var client = registry.For(source.Source.Id);
                if (client is null) continue;

                var start = packages.StartMarketplaceInstall(client, status.Entry, Mode(policy));
                if (start.Started)
                {
                    started++;
                    logger.LogInformation(
                        "Scheduled update of '{GameId}' to {Version} started ({Policy}).",
                        id, status.Entry.Version ?? "(no version)", policy);
                }
                else if (start.Refusal != PackageRefusal.Busy)
                {
                    // Busy is expected and uninteresting — the previous pass's job is still running.
                    // Anything else is worth a line, since an operator enrolled this game and would
                    // otherwise never learn why nothing happens.
                    logger.LogWarning("Scheduled update of '{GameId}' was refused: {Reason}", id, start.Error);
                }
            }
        }

        return new PassResult(started, considered, null);
    }

    private static PackageApplyMode Mode(UpdatePolicy policy) => policy switch
    {
        UpdatePolicy.Force => PackageApplyMode.Force,
        UpdatePolicy.Drain => PackageApplyMode.Drain,
        // Auto is the cautious one: it applies only when the game is idle and gives up otherwise, so a
        // popular game is never interrupted by a schedule nobody was watching.
        _ => PackageApplyMode.Auto,
    };
}
