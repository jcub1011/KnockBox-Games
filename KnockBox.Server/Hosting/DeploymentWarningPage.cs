using System.Net;
using System.Text;

namespace KnockBox.Server.Hosting;

/// <summary>
/// Renders the self-contained HTML page that replaces the shell home page when
/// <see cref="DeploymentDiagnostics"/> has issues. No external assets (inline CSS) so it renders even
/// when the web root itself is the problem. AOT-safe: plain string building + HTML encoding, no
/// reflection or serialization.
/// </summary>
public static class DeploymentWarningPage
{
    public static string Render(IReadOnlyList<DeploymentDiagnostics.Issue> issues)
    {
        var items = new StringBuilder();
        foreach (var issue in issues)
            items.Append("<li><strong>")
                 .Append(WebUtility.HtmlEncode(issue.Title))
                 .Append("</strong><span>")
                 .Append(WebUtility.HtmlEncode(issue.Detail))
                 .Append("</span></li>");

        // $$"""...""": interpolation is {{ }}, so single { } in the CSS are literal.
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>KnockBox — configuration problem</title>
        <style>
          :root { color-scheme: dark; }
          body { margin: 0; min-height: 100vh; display: grid; place-items: center;
                 font: 16px/1.5 system-ui, -apple-system, sans-serif; background: #1a1207; color: #f5e9d8; }
          main { max-width: 42rem; padding: 2rem; }
          h1 { font-size: 1.5rem; margin: 0 0 .25rem; }
          .sub { color: #d8a23a; margin: 0 0 1.5rem; }
          ul { list-style: none; padding: 0; margin: 0 0 1.5rem; }
          li { background: #2a1e0c; border: 1px solid #4a3613; border-left: 4px solid #d8a23a;
               border-radius: 6px; padding: .85rem 1rem; margin-bottom: .75rem; }
          li strong { display: block; }
          li span { display: block; color: #c9b89a; font-size: .92rem; margin-top: .3rem; }
          .foot { color: #9a8a6a; font-size: .9rem; }
          code { background: #00000038; padding: .1rem .3rem; border-radius: 4px; }
        </style>
        </head>
        <body>
        <main>
          <h1>⚠️ KnockBox can't serve normally</h1>
          <p class="sub">The server started, but found problems that need an administrator's attention.</p>
          <ul>{{items}}</ul>
          <p class="foot">Fix the items above, then reload. This page clears automatically once the games
          folder becomes readable (it's re-checked continuously); other fixes apply on the next restart.
          See <code>docs/HOSTING.md</code> for deployment guidance.</p>
        </main>
        </body>
        </html>
        """;
    }
}
