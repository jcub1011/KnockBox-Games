namespace KnockBox.Server.Hosting;

/// <summary>
/// Collects file-access / configuration problems found at startup (plus a live probe of the games
/// folder) so the shell home page can warn an administrator that the deployment is misconfigured —
/// instead of the server crashing, or silently serving a blank or empty site. Populated during
/// bootstrap in <c>Program.cs</c>; read per-request by the home-page warning middleware.
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
    /// Live probe for the games folder's read state (the catalog's <c>ScanError</c>). Set after the
    /// catalog exists so the warning reflects the CURRENT state and clears once access is fixed and a
    /// rescan succeeds — unlike the recorded startup issues, which apply until the next restart.
    /// </summary>
    public Func<string?>? GamesAccessError { get; set; }

    /// <summary>Record a startup problem. Called single-threaded during bootstrap, before any request.</summary>
    public void Report(string title, string detail, bool blocking = false) =>
        _issues.Add(new Issue(title, detail, blocking));

    /// <summary>All current issues: the recorded startup problems plus the live games-access error, if any.</summary>
    public IReadOnlyList<Issue> Current()
    {
        var gamesError = GamesAccessError?.Invoke();
        return gamesError is null
            ? _issues
            : [.. _issues, new Issue("Games folder is not accessible", gamesError, Blocking: true)];
    }

    /// <summary>True when at least one current issue is blocking — the signal to replace the home page.</summary>
    public bool HasBlockingIssue() => Current().Any(i => i.Blocking);
}
