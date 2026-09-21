using System.Text.Json;
using KnockBox.Server.Hosting;
using KnockBox.Server.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The public version endpoint (<c>/api/server-version</c>) is the one deliberate hole in the
/// session-gated surface: both page headers print the version, including before login. These pin
/// that it stays a version-only payload, served without a session on the shell and admin origins —
/// and NOT on the game origin, whose requests would otherwise reach the shell's top-level mapping.
/// </summary>
public class ServerVersionTests
{
    [Fact]
    public async Task Current_reports_the_running_server_version_as_camelCase_json()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        // Results.Json resolves serializer options and logging from DI; a bare context has neither.
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await ServerVersionApi.Current().ExecuteAsync(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        var back = JsonSerializer.Deserialize(json, KnockBoxProtocolContext.Default.ServerVersionResponse);

        Assert.NotNull(back);
        Assert.Equal(KnockBoxVersion.Current.ToString(), back.Version);
        Assert.Contains("\"version\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_origin_registers_the_endpoint_without_a_session_gate()
    {
        var source = RepoFile.Read("KnockBox.Server/Hosting/AdminApi.cs");
        if (source is null)
        {
            // No checkout (publish output): nothing file-based to assert. An explicit skip,
            // not a silent pass, so a runner never reports this as verified when it wasn't.
            Assert.Skip("No repo checkout: nothing file-based to assert.");
            return;
        }
        var registration = FindMapGet(source);
        Assert.False(string.IsNullOrEmpty(registration), "AdminApi.cs does not map /api/server-version.");
        Assert.DoesNotContain("RequireSession", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_origin_maps_the_same_endpoint_but_not_for_the_game_origin()
    {
        var source = RepoFile.Read("KnockBox.Server/Program.cs");
        if (source is null)
        {
            Assert.Skip("No repo checkout: nothing file-based to assert.");
            return;
        }
        // The shell registration maps the shared path constant AND refuses game-origin requests:
        // a bare top-level MapGet would answer on every non-admin origin, and the game MapWhen
        // branch rejoins the pipeline, so game requests reach the outer endpoint table too.
        var registration = FindMapGetFor(source, "ServerVersionApi.Path");
        Assert.False(string.IsNullOrEmpty(registration), "Program.cs does not map /api/server-version.");
        Assert.Contains("IsGameOrigin", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_origins_share_one_path_constant()
    {
        // Two string literals would drift; the constant is the whole sharing mechanism.
        var admin = RepoFile.Read("KnockBox.Server/Hosting/AdminApi.cs");
        var program = RepoFile.Read("KnockBox.Server/Program.cs");
        if (admin is null || program is null)
        {
            Assert.Skip("No repo checkout: nothing file-based to assert.");
            return;
        }
        Assert.DoesNotContain($"\"{ServerVersionApi.Path}\"", admin, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{ServerVersionApi.Path}\"", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_is_served_no_store()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await ServerVersionApi.Current().ExecuteAsync(ctx);

        Assert.Equal("no-store", ctx.Response.Headers.CacheControl.ToString());
    }

    private static string? FindMapGet(string source)
    {
        // Balanced, rather than "the MapGet line": a formatter wrap would otherwise fail the test
        // for a reason unrelated to the code (see AdminRouteGuardTests.Registration).
        const string marker = "MapGet(";
        for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            var registration = Registration(source, at + marker.Length);
            if (registration.Contains("ServerVersionApi", StringComparison.Ordinal))
                return registration;
        }
        // Or the MapServerVersion extension, which maps the shared path with no session gate by
        // construction. Returned as-is: it carries no RequireSession to find, and its non-emptiness
        // is the existence assertion.
        const string extension = "MapServerVersion(";
        var ext = source.IndexOf(extension, StringComparison.Ordinal);
        if (ext >= 0) return source.Substring(ext, extension.Length);
        return null;
    }

    private static string? FindMapGetFor(string source, string mustContain)
    {
        // Same balanced extraction as above, matched on the registration's own content: a fixed
        // line window past MapGet would run into the NEXT registration and read a neighbour's
        // guard as this route's (see AdminRouteGuardTests.Registration).
        const string marker = "MapGet(";
        for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(marker, at + marker.Length, StringComparison.Ordinal))
        {
            var registration = Registration(source, at + marker.Length);
            if (registration.Contains(mustContain, StringComparison.Ordinal))
                return registration;
        }
        return null;
    }

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

        throw new InvalidOperationException("Unbalanced MapGet registration in the route source.");
    }
}
