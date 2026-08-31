using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Asserts that every mutating admin route is still registered behind <c>WriteGuard</c>.
/// </summary>
/// <remarks>
/// A repo-file-consistency test (the <see cref="RepoFile"/> pattern <c>OriginPortBindingTests</c> and
/// <c>AddonManifestTests</c> use) rather than a test against a running host, because composing the real
/// route table needs thirty-odd dependencies — which is the same reason <c>WriteGuardRefusal</c> was
/// split out as a pure function in the first place. <c>AdminWriteGuardTests</c> covers that function's
/// decision; nothing covered whether the routes still ASK it, and a rewrite of the route table quietly
/// dropped the wrapper from ten routes (close/kick a lobby, set availability, delete a game, roll back,
/// uninstall, cancel a job, remove/toggle a source, set an update policy) while leaving their siblings
/// guarded. Every one of those is reachable with an <c>enctype="text/plain"</c> form post, whose body
/// <c>ReadJson</c> then discards — so the handler runs against its all-defaulted record.
/// </remarks>
public class AdminRouteGuardTests
{
    [Fact]
    public void Every_Mutating_Route_Goes_Through_WriteGuard()
    {
        var source = RepoFile.Read("KnockBox.Server/Hosting/AdminApi.cs");
        if (source is null) return; // not a checkout (publish output / NuGet-restored run)

        var unguarded = new List<string>();
        var seen = 0;

        const string marker = "routes.MapPost(";
        for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            seen++;
            var registration = Registration(source, at + marker.Length);
            var route = registration[(registration.IndexOf('"') + 1)..];
            route = route[..route.IndexOf('"')];

            if (!registration.Contains("WriteGuard(", StringComparison.Ordinal)) unguarded.Add(route);
        }

        Assert.True(seen > 20, $"Only found {seen} MapPost registrations; the parser has drifted from the file.");
        Assert.Empty(unguarded);
    }

    /// <summary>
    /// The text of one registration: from just inside <c>MapPost(</c> to its matching close paren.
    /// </summary>
    /// <remarks>
    /// Balanced, rather than "the next few lines" — a fixed window runs into the NEXT registration, and
    /// since the routes that lost their guard sat between routes that kept theirs, every one of them
    /// read as guarded. The test passed against the very file it was written to fail on.
    /// </remarks>
    private static string Registration(string source, int from)
    {
        var depth = 1;
        var inString = false;
        for (var i = from; i < source.Length; i++)
        {
            var c = source[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return source[from..i];
        }

        throw new InvalidOperationException("Unbalanced MapPost registration in AdminApi.cs.");
    }
}
