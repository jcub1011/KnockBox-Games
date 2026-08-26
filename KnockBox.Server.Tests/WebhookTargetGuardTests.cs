using KnockBox.Server.Admin;
using KnockBox.Server.Marketplace;
using KnockBox.Server.Security;
using KnockBox.Server.Webhooks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The address guard as the delivery path actually applies it — through a REAL
/// <see cref="System.Net.Http.HttpClient"/> built by the same factory <c>Program.cs</c> uses, rather than
/// through the predicate alone.
/// </summary>
/// <remarks>
/// No listener and no network are involved: the refusal happens in the connect callback, before a socket
/// is opened. That is the property being pinned — a rule applied to the URL string would be defeated by a
/// hostname that resolves inward, and by a redirect, both of which arrive here instead.
/// </remarks>
public class WebhookTargetGuardTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"kb-webhook-guard-{Guid.NewGuid():N}");

    public WebhookTargetGuardTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private WebhookDispatcher Build(bool allowPrivateTargets)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = Path.Combine(_directory, "admin.secret"),
            ["KnockBox:AdminSettingsPath"] = Path.Combine(_directory, "settings.json"),
        }).Build();
        var auth = new AdminAuthService(config, TimeProvider.System, NullLogger<AdminAuthService>.Instance);
        var store = new AdminSettingsStore(config, auth, NullLogger<AdminSettingsStore>.Instance);
        var options = new WebhookOptions(
            Enabled: true, MaxEndpoints: 8, Timeout: TimeSpan.FromSeconds(5), ErrorsPerMinute: 6,
            MemoryThresholdMb: 0, CpuPercentThreshold: 0, AllowPrivateTargets: allowPrivateTargets);

        // Exactly the wiring Program.cs performs for the webhook client.
        var http = MarketplaceClient.CreateHttpClient(
            options.AllowPrivateTargets ? null : PrivateAddressGuard.IsBlocked);

        return new WebhookDispatcher(new WebhookQueue(), store, http, options, TimeProvider.System,
            NullLogger<WebhookDispatcher>.Instance);
    }

    private static WebhookEndpoint Endpoint(string url) =>
        new("test", "Test", url, [], true);

    private static WebhookPayload Payload() => WebhookDispatcher.Payload(
        WebhookEvent.MaintenanceChanged, "Test.", DateTimeOffset.UnixEpoch, title: "Test");

    [Theory]
    [InlineData("http://127.0.0.1:9/hook")]
    [InlineData("http://[::1]:9/hook")]
    [InlineData("http://10.0.0.7/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    public async Task Refuses_an_inward_target_before_opening_a_socket(string url)
    {
        var result = await Build(allowPrivateTargets: false).DeliverAsync(Endpoint(url), Payload());

        Assert.False(result.Ok);
        // No status at all: nothing was ever sent, which is exactly what makes this useless as a probe —
        // "refused by us" and "refused by them" must not be distinguishable in the reply.
        Assert.Null(result.Status);
        Assert.Contains(PrivateAddressGuard.Knob, result.Error);
    }

    [Fact]
    public async Task Allows_an_inward_target_once_the_operator_lifts_the_rule()
    {
        // Port 9 (discard) with nothing listening: the delivery still fails, but on the CONNECTION rather
        // than on the guard — which is the difference this test is about. A monitoring agent on the same
        // host must be reachable when the knob is set.
        var result = await Build(allowPrivateTargets: true)
            .DeliverAsync(Endpoint("http://127.0.0.1:9/hook"), Payload());

        Assert.False(result.Ok);
        Assert.DoesNotContain(PrivateAddressGuard.Knob, result.Error ?? "");
    }
}
