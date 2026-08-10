namespace KnockBox.Server.Hosting;

/// <summary>
/// Collects file-access / configuration problems found at startup, plus live probes (the games
/// folder's read state, game-package install failures), so the shell home page can warn an
/// administrator that the deployment is misconfigured — instead of the server crashing, or silently
/// serving a blank or empty site. Populated during bootstrap in <c>Program.cs</c>; read per-request by
/// the home-page warning middleware.
/// </summary>
public sealed class DeploymentDiagnostics
{
    /// <summary>
    /// A single deployment problem. <paramref name="Blocking"/> means the server can't serve its core
    /// purpose (no shell, or no games can ever load) — those replace the home page. A non-blocking
    /// issue is a degraded-but-functional warning (e.g. an unwritable cache): it's logged and listed on
    /// the warning page when one is shown, but never blanks a working site on its own.
    /// </summary>
    public sealed record Issue(string Title, string Detail, bool Blocking = false);

    // Appended only during bootstrap (single-threaded, before app.Run); read during requests
    // afterwards. No concurrent write + read, so no lock needed.
    private readonly List<Issue> _issues = [];

    /// <summary>
    /// A problem re-evaluated on every read: the probe returns a detail string while the problem exists
    /// and null once it clears.
    /// </summary>
    private sealed record Probe(string Title, Func<string?> Detail, bool Blocking);

    // Also bootstrap-only writes, for the same reason as _issues.
    private readonly List<Probe> _probes = [];

    /// <summary>
    /// Register a LIVE probe, re-evaluated on every <see cref="Current"/> call. Unlike the recorded
    /// startup issues (which apply until the next restart), a probe's warning disappears as soon as the
    /// underlying problem is fixed — no restart needed.
    /// </summary>
    /// <remarks>
    /// Anything running after startup — a timer, a background reconcile — must report through a probe
    /// rather than <see cref="Report"/>, whose backing list is deliberately unsynchronized.
    /// </remarks>
    public void AddProbe(string title, Func<string?> detail, bool blocking = false) =>
        _probes.Add(new Probe(title, detail, blocking));

    /// <summary>Record a startup problem. Called single-threaded during bootstrap, before any request.</summary>
    public void Report(string title, string detail, bool blocking = false) =>
        _issues.Add(new Issue(title, detail, blocking));

    /// <summary>All current issues: the recorded startup problems plus every live probe reporting one.</summary>
    public IReadOnlyList<Issue> Current()
    {
        if (_probes.Count == 0) return _issues;

        List<Issue>? live = null;
        foreach (var probe in _probes)
        {
            // A throwing probe must not take the warning page down with it — that page is the only
            // channel telling the operator what's wrong.
            string? detail;
            try { detail = probe.Detail(); }
            catch (Exception ex) { detail = $"could not be checked: {ex.Message}"; }

            if (detail is null) continue;
            (live ??= [.. _issues]).Add(new Issue(probe.Title, detail, probe.Blocking));
        }
        return live ?? _issues;
    }

    /// <summary>True when at least one current issue is blocking — the signal to replace the home page.</summary>
    public bool HasBlockingIssue() => Current().Any(i => i.Blocking);
}
