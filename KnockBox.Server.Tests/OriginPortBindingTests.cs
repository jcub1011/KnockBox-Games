using System.Text.RegularExpressions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// Guards the one invariant that binds the three origins together: EVERY origin must be listed in EVERY
/// place that tells Kestrel what to bind.
///
/// Program.cs binds all three origins itself only when the host wasn't told what to bind. Setting
/// <c>ASPNETCORE_URLS</c> (launchSettings' <c>applicationUrl</c>) or <c>ASPNETCORE_HTTP_PORTS</c> (the
/// Dockerfile) REPLACES that list rather than adding to it — so an origin missing from one of those files
/// silently never binds and answers "connection refused". That is precisely how the admin portal shipped
/// unreachable: the routing was in place, the listener was not.
///
/// These assertions are deliberately about the deployment FILES rather than about a running host: nothing
/// observable in-process distinguishes "port not configured" from "port not reachable", and a test that
/// bound real sockets would be flaky on a busy machine.
/// </summary>
public class OriginPortBindingTests
{
    // Program.cs defaults: shell is fixed, the other two come from KnockBox:GamesPort / KnockBox:AdminPort.
    private const int DevShellPort = 5114;
    private const int DevGamesPort = 5115;
    private const int DevAdminPort = 5116;

    // Dockerfile values (KnockBox__GamesPort / KnockBox__AdminPort + ASPNETCORE_HTTP_PORTS).
    private const int ContainerShellPort = 8080;
    private const int ContainerGamesPort = 8081;
    private const int ContainerAdminPort = 8082;

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public void Every_dev_origin_is_listed_in_the_launch_profile(string profile)
    {
        var launchSettings = ReadRepoFile(Path.Combine("KnockBox.Server", "Properties", "launchSettings.json"));
        if (launchSettings is null) return; // not run from a repo checkout — nothing to check

        var applicationUrl = ApplicationUrlOf(launchSettings, profile);
        Assert.NotNull(applicationUrl);

        foreach (var (port, origin) in new[]
                 {
                     (DevShellPort, "shell"), (DevGamesPort, "games"), (DevAdminPort, "admin"),
                 })
        {
            Assert.True(
                applicationUrl!.Contains($":{port}", StringComparison.Ordinal),
                $"launchSettings profile '{profile}' does not bind the {origin} origin (port {port}). " +
                "applicationUrl becomes ASPNETCORE_URLS, which replaces the built-in port defaults in " +
                $"Program.cs wholesale — so the {origin} origin would refuse connections. Current value: " +
                $"'{applicationUrl}'.");
        }
    }

    [Fact]
    public void Every_container_origin_is_listed_in_the_dockerfile_http_ports()
    {
        var dockerfile = ReadRepoFile(Path.Combine("KnockBox.Server", "Dockerfile"));
        if (dockerfile is null) return;

        var httpPorts = Match(dockerfile, @"ASPNETCORE_HTTP_PORTS\s*=\s*""([^""]+)""");
        Assert.NotNull(httpPorts);

        var ports = httpPorts!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (port, origin) in new[]
                 {
                     (ContainerShellPort, "shell"), (ContainerGamesPort, "games"), (ContainerAdminPort, "admin"),
                 })
        {
            Assert.True(
                ports.Contains(port.ToString()),
                $"The Dockerfile's ASPNETCORE_HTTP_PORTS does not bind the {origin} origin (port {port}). " +
                $"Setting it replaces the built-in port defaults, so the {origin} origin would refuse " +
                $"connections in the container. Current value: '{httpPorts}'.");
        }
    }

    /// <summary>
    /// The container's routing knobs must agree with the ports it binds — a bound port the router doesn't
    /// know about is as unreachable as one that was never bound.
    /// </summary>
    [Fact]
    public void Container_routing_knobs_match_the_bound_container_ports()
    {
        var dockerfile = ReadRepoFile(Path.Combine("KnockBox.Server", "Dockerfile"));
        if (dockerfile is null) return;

        Assert.Equal(ContainerGamesPort.ToString(), Match(dockerfile, @"KnockBox__GamesPort=(\d+)"));
        Assert.Equal(ContainerAdminPort.ToString(), Match(dockerfile, @"KnockBox__AdminPort=(\d+)"));
    }

    /// <summary>
    /// The admin password hash must not default to the image's own directory: /app is root-owned while the
    /// container runs as $APP_UID, so the write fails — and /app lives inside the image, so the password
    /// would be lost on every update, returning the portal to its unclaimed state.
    /// </summary>
    [Fact]
    public void Container_stores_the_admin_secret_outside_the_app_directory()
    {
        var dockerfile = ReadRepoFile(Path.Combine("KnockBox.Server", "Dockerfile"));
        if (dockerfile is null) return;

        var secretPath = Match(dockerfile, @"KnockBox__AdminPasswordPath=(\S+)");
        Assert.NotNull(secretPath);
        var directory = secretPath!.Replace('\\', '/');
        directory = directory[..directory.LastIndexOf('/')];

        Assert.NotEqual("/app", directory);
        Assert.True(
            Regex.IsMatch(dockerfile, @"chown \$APP_UID[^\n]*" + Regex.Escape(directory)),
            $"'{directory}' holds the admin password hash but is not chowned to $APP_UID in the Dockerfile, " +
            "so the container's non-root user cannot write it and setting an admin password would fail.");
    }

    /// <summary>The <c>applicationUrl</c> of one launchSettings profile, without a JSON dependency.</summary>
    private static string? ApplicationUrlOf(string launchSettings, string profile)
    {
        // Find the profile object, then the first applicationUrl at or after it.
        var profileIndex = launchSettings.IndexOf($"\"{profile}\"", StringComparison.Ordinal);
        if (profileIndex < 0) return null;
        return Match(launchSettings[profileIndex..], @"""applicationUrl""\s*:\s*""([^""]+)""");
    }

    private static string? Match(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Reads a repo-relative file, locating the checkout by walking up for the solution marker — the same
    /// trick ContentPaths uses. Returns null when there is no checkout to read (so the test no-ops rather
    /// than failing somewhere the deployment files legitimately aren't present).
    /// </summary>
    private static string? ReadRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "KnockBox-Games.slnx"))) continue;
            var full = Path.Combine(dir.FullName, relativePath);
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        return null;
    }
}
