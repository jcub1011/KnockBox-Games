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
/// that it stays a version-only payload, served without a session on both origins.
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
        var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
        var back = JsonSerializer.Deserialize(json, KnockBoxProtocolContext.Default.ServerVersionResponse);

        Assert.NotNull(back);
        Assert.Equal(KnockBoxVersion.Current.ToString(), back.Version);
        Assert.Contains("\"version\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_origin_registers_the_endpoint_without_a_session_gate()
    {
        var source = RepoFile.Read("KnockBox.Server/Hosting/AdminApi.cs");
        if (source is null) return; // No checkout (publish output): nothing file-based to assert.
        var registration = FindMapGet(source);
        Assert.False(string.IsNullOrEmpty(registration), "AdminApi.cs does not map /api/server-version.");
        Assert.DoesNotContain("RequireSession", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void Shell_origin_maps_the_same_endpoint()
    {
        var source = RepoFile.Read("KnockBox.Server/Program.cs");
        if (source is null) return;
        Assert.Contains("MapServerVersion()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_origins_share_one_path_constant()
    {
        // Two string literals would drift; the constant is the whole sharing mechanism.
        var admin = RepoFile.Read("KnockBox.Server/Hosting/AdminApi.cs");
        var program = RepoFile.Read("KnockBox.Server/Program.cs");
        if (admin is null || program is null) return;
        Assert.DoesNotContain($"\"{ServerVersionApi.Path}\"", admin, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{ServerVersionApi.Path}\"", program, StringComparison.Ordinal);
    }

    private static string? FindMapGet(string source)
    {
        // The registration lives or dies by the constant, so locate the MapGet line mentioning it.
        foreach (var line in source.Split('\n'))
        {
            if (line.Contains("MapGet(", StringComparison.Ordinal)
                && line.Contains("ServerVersionApi", StringComparison.Ordinal))
                return line;
        }
        return null;
    }
}
