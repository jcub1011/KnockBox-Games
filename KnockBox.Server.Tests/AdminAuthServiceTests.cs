using KnockBox.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KnockBox.Server.Tests;

public class AdminAuthServiceTests : IDisposable
{
    private readonly string _tempSecretPath;
    private readonly MutableTimeProvider _clock;
    private readonly IConfiguration _config;

    public AdminAuthServiceTests()
    {
        _tempSecretPath = Path.Combine(Path.GetTempPath(), $"admin-test-{Guid.NewGuid():N}.secret");
        _clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        
        var configDict = new Dictionary<string, string?>
        {
            ["KnockBox:AdminPasswordPath"] = _tempSecretPath,
            ["KnockBox:AdminSessionTtlHours"] = "1.0"
        };
        _config = new ConfigurationBuilder().AddInMemoryCollection(configDict).Build();
    }

    public void Dispose()
    {
        if (File.Exists(_tempSecretPath))
        {
            try { File.Delete(_tempSecretPath); } catch { }
        }
    }

    [Fact]
    public void IsConfigured_ReturnsFalse_WhenFileDoesNotExist()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        Assert.False(service.IsConfigured);
    }

    [Fact]
    public void SetupPassword_CreatesSecretFile_AndReturnsTrue()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);

        var outcome = service.SetupPassword("SecretPassword123!");

        Assert.Equal(AdminAuthService.SetupOutcome.Success, outcome);
        Assert.True(service.IsConfigured);
        Assert.True(File.Exists(_tempSecretPath));
    }

    [Fact]
    public void SetupPassword_Fails_WhenAlreadyConfigured()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        Assert.Equal(AdminAuthService.SetupOutcome.Success, service.SetupPassword("FirstPassword"));

        // Distinguished from a weak password so the portal can tell the operator to reset instead.
        Assert.Equal(AdminAuthService.SetupOutcome.AlreadyConfigured, service.SetupPassword("SecondPassword"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    [InlineData("elevenchars")]   // 11 — one under the floor
    public void SetupPassword_rejects_a_password_under_the_minimum_length(string password)
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);

        Assert.Equal(AdminAuthService.SetupOutcome.PasswordTooWeak, service.SetupPassword(password));
        // A rejected password must not half-claim the portal.
        Assert.False(service.IsConfigured);
        Assert.False(File.Exists(_tempSecretPath));
    }

    [Fact]
    public void SetupPassword_accepts_exactly_the_minimum_length()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);

        var atMinimum = new string('x', AdminAuthService.MinPasswordLength);
        Assert.Equal(AdminAuthService.SetupOutcome.Success, service.SetupPassword(atMinimum));
        Assert.True(service.VerifyPassword(atMinimum));
    }

    [Fact]
    public void Secret_file_is_readable_only_by_its_owner()
    {
        // Unix-only: on Windows the file inherits the directory's ACL and there is no mode to assert.
        // This runs for real in the CI `dotnet` job (ubuntu) and the container smoke test.
        if (OperatingSystem.IsWindows()) return;

        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("SecretPassword123!");

        // A world-readable hash invites offline cracking by anyone with a shell on the box.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_tempSecretPath));
    }

    [Fact]
    public void VerifyPassword_ValidatesCorrectPassword()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("MyAdminPass123!");

        Assert.True(service.VerifyPassword("MyAdminPass123!"));
        Assert.False(service.VerifyPassword("WrongPass"));
    }

    [Fact]
    public void VerifyPassword_ExecutesDummyHash_WhenUnconfigured()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        
        Assert.False(service.VerifyPassword("AnyPassword"));
    }

    [Fact]
    public void ResetPassword_DeletesFile_AndResetsState()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("PasswordToReset");
        Assert.True(service.IsConfigured);

        var resetResult = service.ResetPassword();

        Assert.True(resetResult);
        Assert.False(service.IsConfigured);
        Assert.False(File.Exists(_tempSecretPath));
    }

    [Fact]
    public void SessionToken_IssuesAndValidatesCorrectly()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("PassForToken");

        var token = service.CreateSessionToken();
        Assert.False(string.IsNullOrEmpty(token));

        Assert.True(service.ValidateSessionToken(token));
    }

    [Fact]
    public void SessionToken_Fails_WhenExpired()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("PassForTokenExpired");

        var token = service.CreateSessionToken();
        
        // Advance clock past 1 hour TTL
        _clock.Advance(TimeSpan.FromHours(2));

        Assert.False(service.ValidateSessionToken(token));
    }

    [Fact]
    public void SessionToken_Fails_WhenTampered()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("PassForTokenTampered");

        var token = service.CreateSessionToken();
        var tamperedToken = token + "X";

        Assert.False(service.ValidateSessionToken(tamperedToken));
    }

    [Fact]
    public void Resetting_the_password_revokes_existing_sessions()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("FirstPassword1");
        var token = service.CreateSessionToken();
        Assert.True(service.ValidateSessionToken(token));

        // The whole point of the reset path: an admin resetting a COMPROMISED password must not leave the
        // attacker's session alive until the next restart.
        service.ResetPassword();
        service.SetupPassword("SecondPassword2");

        Assert.False(service.ValidateSessionToken(token));
    }

    [Fact]
    public void Sessions_track_the_current_secret_exactly_so_a_swap_never_leaves_two_sets_live()
    {
        // Pins the semantics of a filesystem-level secret swap (delete, re-claim, restore a backup).
        // Rollback itself is NOT defended against — whoever can write the file can already delete it and
        // claim a new password, and detecting rollback would need state they don't control. What IS
        // guaranteed: sessions are valid for exactly the secret currently on disk, never for a secret that
        // was replaced. So a swap can never leave the old admin's AND the new claimant's sessions both live.
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("OriginalPassword1");
        var tokenUnderOriginal = service.CreateSessionToken();
        var originalSecret = File.ReadAllText(_tempSecretPath);

        service.ResetPassword();
        service.SetupPassword("ReplacementPassword2");
        var tokenUnderReplacement = service.CreateSessionToken();

        Assert.False(service.ValidateSessionToken(tokenUnderOriginal));   // superseded
        Assert.True(service.ValidateSessionToken(tokenUnderReplacement));

        // Restoring the original file restores the original password — and, consistently, its sessions —
        // while killing the replacement's. That is the documented consequence of controlling the file; the
        // invariant is that the two sets are never simultaneously valid.
        File.WriteAllText(_tempSecretPath, originalSecret);

        Assert.True(service.VerifyPassword("OriginalPassword1"));
        Assert.False(service.VerifyPassword("ReplacementPassword2"));
        Assert.True(service.ValidateSessionToken(tokenUnderOriginal));
        Assert.False(service.ValidateSessionToken(tokenUnderReplacement));
    }

    [Fact]
    public void A_session_never_validates_while_unconfigured()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        service.SetupPassword("PasswordToReset1");
        var token = service.CreateSessionToken();

        service.ResetPassword();

        Assert.False(service.ValidateSessionToken(token));
    }
}
