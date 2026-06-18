using KnockBox.Server.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace KnockBox.Server.Tests;

public class DeploymentDiagnosticsTests
{
    [Fact]
    public void No_issues_when_nothing_reported_and_no_games_error()
    {
        var d = new DeploymentDiagnostics();
        Assert.Empty(d.Current());
    }

    [Fact]
    public void Reported_startup_issues_are_returned()
    {
        var d = new DeploymentDiagnostics();
        d.Report("Logs folder is not writable", "'/app/logs' is not writable.");

        var issues = d.Current();
        Assert.Single(issues);
        Assert.Equal("Logs folder is not writable", issues[0].Title);
    }

    [Fact]
    public void A_non_blocking_warning_does_not_count_as_blocking()
    {
        var d = new DeploymentDiagnostics();
        d.Report("Pre-compressed cache is not writable", "degraded only");

        Assert.NotEmpty(d.Current());        // still surfaced...
        Assert.False(d.HasBlockingIssue());  // ...but doesn't blank the home page
    }

    [Fact]
    public void A_blocking_issue_is_detected()
    {
        var d = new DeploymentDiagnostics();
        d.Report("Platform shell is missing", "no index.html", blocking: true);

        Assert.True(d.HasBlockingIssue());
    }

    [Fact]
    public void Live_games_access_error_is_appended_blocking_then_clears_when_resolved()
    {
        string? gamesError = "denied";
        var d = new DeploymentDiagnostics { GamesAccessError = () => gamesError };

        Assert.Contains(d.Current(), i => i.Title == "Games folder is not accessible" && i.Blocking);
        Assert.True(d.HasBlockingIssue());

        gamesError = null; // permissions fixed, a rescan succeeded
        Assert.Empty(d.Current());
        Assert.False(d.HasBlockingIssue());
    }

    [Fact]
    public void Warning_page_lists_each_issue_and_html_encodes_the_detail()
    {
        var html = DeploymentWarningPage.Render(
        [
            new DeploymentDiagnostics.Issue("Games folder is not accessible", "path '/games' & <stuff>"),
        ]);

        Assert.Contains("Games folder is not accessible", html);
        Assert.Contains("&amp;", html);          // '&' encoded
        Assert.Contains("&lt;stuff&gt;", html);   // angle brackets encoded
        Assert.DoesNotContain("<stuff>", html);   // never emitted raw (no injection)
    }

    // ── Home-page middleware: when a blocking issue exists, GET / (and /index.html) is replaced with
    // the warning page; everything else falls through to the real shell/static pipeline. ───────────

    [Theory]
    [InlineData("/")]
    [InlineData("")]            // empty path is also the home document
    [InlineData("/index.html")]
    [InlineData("/INDEX.HTML")] // case-insensitive
    public async Task Blocking_issue_replaces_the_home_document(string path)
    {
        var d = new DeploymentDiagnostics();
        d.Report("Platform shell is missing", "no index.html", blocking: true);

        var (status, body, nextCalled) = await RunMiddleware(d, path);

        Assert.False(nextCalled);                  // short-circuited — the broken shell never served
        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("Platform shell is missing", body);
    }

    [Fact]
    public async Task Healthy_boot_serves_the_real_shell()
    {
        var d = new DeploymentDiagnostics(); // no issues

        var (_, _, nextCalled) = await RunMiddleware(d, "/");

        Assert.True(nextCalled); // falls through to UseDefaultFiles/UseStaticFiles
    }

    [Fact]
    public async Task Non_blocking_warning_does_not_replace_the_home_document()
    {
        var d = new DeploymentDiagnostics();
        d.Report("Pre-compressed cache is not writable", "degraded only"); // non-blocking

        var (_, _, nextCalled) = await RunMiddleware(d, "/");

        Assert.True(nextCalled); // a working site is never blanked by a warning alone
    }

    [Fact]
    public async Task Non_home_paths_are_never_intercepted_even_when_blocking()
    {
        var d = new DeploymentDiagnostics();
        d.Report("Platform shell is missing", "no index.html", blocking: true);

        var (_, _, nextCalled) = await RunMiddleware(d, "/lobby");

        Assert.True(nextCalled); // only the home document is replaced
    }

    [Fact]
    public async Task Non_GET_home_requests_are_not_intercepted()
    {
        var d = new DeploymentDiagnostics();
        d.Report("Platform shell is missing", "no index.html", blocking: true);

        var (_, _, nextCalled) = await RunMiddleware(d, "/", method: "POST");

        Assert.True(nextCalled);
    }

    /// <summary>Runs the warning middleware against a synthetic request and reports the outcome.</summary>
    private static async Task<(int status, string body, bool nextCalled)> RunMiddleware(
        DeploymentDiagnostics d, string path, string method = "GET")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = method;
        using var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;

        var nextCalled = false;
        var middleware = new DeploymentWarningMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, d);
        await middleware.InvokeAsync(ctx);

        responseBody.Position = 0;
        var body = await new StreamReader(responseBody).ReadToEndAsync();
        return (ctx.Response.StatusCode, body, nextCalled);
    }
}
