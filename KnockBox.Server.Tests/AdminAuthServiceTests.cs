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
        
        var success = service.SetupPassword("SecretPassword123!");
        
        Assert.True(success);
        Assert.True(service.IsConfigured);
        Assert.True(File.Exists(_tempSecretPath));
    }

    [Fact]
    public void SetupPassword_Fails_WhenAlreadyConfigured()
    {
        var service = new AdminAuthService(_config, _clock, NullLogger<AdminAuthService>.Instance);
        Assert.True(service.SetupPassword("FirstPassword"));
        
        Assert.False(service.SetupPassword("SecondPassword"));
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
}
