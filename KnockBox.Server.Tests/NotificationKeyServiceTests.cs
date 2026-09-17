using KnockBox.Server.Hosting;
using KnockBox.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

/// <summary>
/// The notification store's at-rest encryption key: stable while the password stands, rotated when it
/// changes, and served to signed-in admins only.
/// </summary>
public class NotificationKeyServiceTests : IDisposable
{
    private readonly string _tempSecretPath = Path.Combine(Path.GetTempPath(), $"notif-key-test-{Guid.NewGuid():N}.secret");
    private readonly MutableTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly IConfiguration _config;

    public NotificationKeyServiceTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = _tempSecretPath,
            ["KnockBox:AdminSessionTtlHours"] = "1.0",
        }).Build();
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempSecretPath)) File.Delete(_tempSecretPath);
            else if (Directory.Exists(_tempSecretPath)) Directory.Delete(_tempSecretPath);
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AdminAuthService Auth() => new(_config, _clock, NullLogger<AdminAuthService>.Instance);

    [Fact]
    public void Key_is_stable_while_the_password_stands()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = new NotificationKeyService(auth);

        Assert.Equal(keys.GetKeyBase64(), keys.GetKeyBase64());
    }

    [Fact]
    public void Key_is_32_bytes()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        Assert.Equal(32, new NotificationKeyService(auth).GetKey().Length);
    }

    [Fact]
    public void Key_rotates_when_the_password_is_replaced()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = new NotificationKeyService(auth);
        var before = keys.GetKeyBase64();

        // Reset + a new password is the operator-visible "password change" (there is no overwrite API).
        Assert.True(auth.ResetPassword());
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("SecondPassword456"));

        Assert.NotEqual(before, keys.GetKeyBase64());
    }

    [Fact]
    public void Key_is_stable_across_transient_secret_read_failures()
    {
        // A directory at the secret path makes every read throw (portably, on Windows and Linux),
        // simulating a transient AV/indexer lock. That must not look like a password change.
        Directory.CreateDirectory(_tempSecretPath);
        try
        {
            var auth = Auth();
            var keys = new NotificationKeyService(auth, maxAttempts: 3, retryDelayMs: 0);

            Assert.Equal(keys.GetKeyBase64(), keys.GetKeyBase64());
        }
        finally
        {
            Directory.Delete(_tempSecretPath);
        }
    }

    [Fact]
    public void Key_rotates_once_after_recovery_from_read_failures()
    {
        Directory.CreateDirectory(_tempSecretPath);
        var auth = Auth();
        var keys = new NotificationKeyService(auth, maxAttempts: 2, retryDelayMs: 0);
        var fallback = keys.GetKeyBase64();
        Directory.Delete(_tempSecretPath);

        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));

        Assert.NotEqual(fallback, keys.GetKeyBase64());
        Assert.Equal(keys.GetKeyBase64(), keys.GetKeyBase64());
    }

    [Fact]
    public void Each_call_returns_a_copy_the_caller_cannot_mutate()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = new NotificationKeyService(auth);

        var first = keys.GetKey();
        first[0] ^= 0xFF;
        Assert.Equal(keys.GetKeyBase64(), Convert.ToBase64String(keys.GetKey()));
    }

    [Fact]
    public async Task Key_handler_answers_503_without_a_service_and_200_with_one()
    {
        // Without the service the origin still builds (the nullable-dependency precedent): the endpoint
        // refuses rather than throwing.
        var without = new DefaultHttpContext();
        without.Response.Body = new MemoryStream();
        var empty = TestOptions();
        await AdminApi.NotificationKey(without, empty);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, without.Response.StatusCode);

        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var withService = new DefaultHttpContext();
        withService.Response.Body = new MemoryStream();
        await AdminApi.NotificationKey(withService, empty with
        {
            NotificationKeys = new NotificationKeyService(auth),
        });
        Assert.Equal(StatusCodes.Status200OK, withService.Response.StatusCode);
    }

    private static AdminApi.Options TestOptions() => new(
        Auth: null!,
        Lobbies: null!,
        Closer: null!,
        Catalog: null!,
        Settings: null!,
        Lifecycle: null!,
        Operations: null!,
        Packages: null!,
        PackageOptions: null!,
        PackageLimits: null!,
        Marketplace: null,
        Updates: null,
        Scheduler: null,
        Logs: null!,
        Disk: null!,
        Relay: null!,
        Authority: null!,
        History: null!,
        MetricSampleSeconds: 0,
        Limits: null!,
        AuthorityLimits: null!,
        BlobLimits: null!,
        Blobs: null,
        Webhooks: null,
        WebhookLog: null,
        WebhookOptions: null!,
        Connections: null!,
        Authorities: null,
        Paths: null!,
        Diagnostics: null!,
        Time: TimeProvider.System,
        Logger: NullLogger<NotificationKeyServiceTests>.Instance,
        LoginAttemptsPerMinutePerIp: 0,
        LoginAttemptsPerMinuteGlobal: 0,
        CookieAlwaysSecure: false,
        StaleAfter: TimeSpan.Zero);
}
