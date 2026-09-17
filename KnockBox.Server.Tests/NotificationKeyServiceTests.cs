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
    // Per-test key file: the default key path is derived from the temp directory alone, so without
    // this every test in the class would share one file and isolation would rest on Dispose ordering.
    private readonly string _tempKeyPath = Path.Combine(Path.GetTempPath(), $"notif-key-{Guid.NewGuid():N}.key.json");
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
            if (File.Exists(_tempKeyPath)) File.Delete(_tempKeyPath);
            if (File.Exists(_tempKeyPath + ".tmp")) File.Delete(_tempKeyPath + ".tmp");
        }
        catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private AdminAuthService Auth() => new(_config, _clock, NullLogger<AdminAuthService>.Instance);

    private NotificationKeyService Keys(AdminAuthService auth, int maxAttempts = 10, int retryDelayMs = 1000) =>
        new(auth, keyFilePath: _tempKeyPath, maxAttempts: maxAttempts, retryDelayMs: retryDelayMs);

    [Fact]
    public void Key_is_stable_while_the_password_stands()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = Keys(auth);

        Assert.Equal(keys.GetKeyBase64(), keys.GetKeyBase64());
    }

    [Fact]
    public void Key_is_32_bytes()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        Assert.Equal(32, Keys(auth).GetKey().Length);
    }

    [Fact]
    public void Key_rotates_when_the_password_is_replaced()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = Keys(auth);
        var before = keys.GetKeyBase64();

        // Reset + a new password is the operator-visible "password change" (there is no overwrite API).
        Assert.True(auth.ResetPassword());
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("SecondPassword456"));

        Assert.NotEqual(before, keys.GetKeyBase64());
    }

    [Fact]
    public void Key_survives_a_restart_while_the_password_stands()
    {
        // The reported bug: the key lived in process memory only, so every restart minted a fresh one
        // and the portal's stored notifications stopped decrypting (blamed on a password change that
        // never happened). A new service over the same files must serve the same key.
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var before = Keys(auth).GetKeyBase64();

        var after = Keys(Auth()).GetKeyBase64();

        Assert.Equal(before, after);
    }

    [Fact]
    public void Different_accounts_get_independent_keys_that_each_survive_a_restart()
    {
        // The multi-account seam: one entry per account id, so a second account is a new entry rather
        // than a schema change — and neither account's calls disturb the other's key.
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = Keys(auth);
        var mine = keys.GetKeyBase64("alice");
        var other = keys.GetKeyBase64("bob");
        var current = keys.GetKeyBase64();

        Assert.NotEqual(mine, other);
        Assert.Equal(mine, keys.GetKeyBase64("alice"));

        var restarted = Keys(Auth());
        Assert.Equal(mine, restarted.GetKeyBase64("alice"));
        Assert.Equal(other, restarted.GetKeyBase64("bob"));
        Assert.Equal(current, restarted.GetKeyBase64());
    }

    [Fact]
    public void Corrupt_key_file_recovers_with_a_fresh_stable_key()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = Keys(auth);
        var before = keys.GetKeyBase64();

        File.WriteAllText(keys.KeyFilePath, "{not json");

        var recovered = Keys(Auth());
        var fresh = recovered.GetKeyBase64();
        Assert.NotEqual(before, fresh);
        Assert.Equal(fresh, recovered.GetKeyBase64());
    }

    [Fact]
    public void Unwritable_key_path_serves_an_in_memory_key_rather_than_failing()
    {
        // A directory at the key path makes every persist throw (portably, on Windows and Linux).
        // The key endpoint must keep answering; the cost is "stable until restart", warned about.
        var dir = Path.Combine(Path.GetTempPath(), $"notif-key-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var auth = Auth();
            Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
            var keys = new NotificationKeyService(auth, keyFilePath: dir);

            Assert.Equal(keys.GetKeyBase64(), keys.GetKeyBase64());
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void Key_file_is_readable_only_by_its_owner()
    {
        // Unix-only, like the secret file itself: these keys decrypt operator history.
        if (OperatingSystem.IsWindows()) return;

        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = Keys(auth);
        _ = keys.GetKeyBase64();

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keys.KeyFilePath));
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
            var keys = Keys(auth, maxAttempts: 3, retryDelayMs: 0);

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
        var keys = Keys(auth, maxAttempts: 2, retryDelayMs: 0);
        var fallback = keys.GetKeyBase64();
        Directory.Delete(_tempSecretPath);

        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));

        Assert.NotEqual(fallback, keys.GetKeyBase64());
        Assert.Equal(keys.GetKeyBase64(), keys.GetKeyBase64());
    }

    [Fact]
    public void Fallback_key_is_not_persisted_by_another_accounts_call()
    {
        // The reported drift: a fallback minted while the secret was unreadable was swept to disk
        // by the next persist for a different account, despite the "never persisted" comment.
        Directory.CreateDirectory(_tempSecretPath);
        var auth = Auth();
        var keys = Keys(auth, maxAttempts: 2, retryDelayMs: 0);
        _ = keys.GetKeyBase64();
        Directory.Delete(_tempSecretPath);

        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));

        _ = keys.GetKeyBase64("alice");

        var json = File.ReadAllText(keys.KeyFilePath);
        Assert.Contains("alice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("default", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_call_returns_a_copy_the_caller_cannot_mutate()
    {
        var auth = Auth();
        Assert.Equal(AdminAuthService.SetupOutcome.Success, auth.SetupPassword("FirstPassword123"));
        var keys = Keys(auth);

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
            NotificationKeys = Keys(auth),
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
