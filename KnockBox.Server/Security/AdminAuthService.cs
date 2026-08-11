using KnockBox.Server.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnockBox.Server.Security;

/// <summary>
/// Manages admin authentication, secure password hashing, and admin session tokens.
/// 
/// Password Storage & Reset:
/// - Password is saved in a secure file (<c>admin.secret</c> by default).
/// - Hashing uses PBKDF2-HMAC-SHA256 with 600,000 iterations (OWASP 2026 recommended standard for PBKDF2)
///   and a unique 16-byte salt per set.
/// - Password reset path: Deleting the secret file returns the server to Unconfigured / Setup mode.
/// 
/// Timing Side-Channel Protection:
/// - Uses <see cref="CryptographicOperations.FixedTimeEquals"/> for constant-time hash comparisons.
/// - Performs dummy hash iterations even when unconfigured or invalid, ensuring constant-duration responses.
/// </summary>
public sealed class AdminAuthService
{
    /// <summary>
    /// Name of the cookie carrying the admin session token. Lives here rather than in the request
    /// handlers so the issuing, reading and clearing sites cannot drift apart.
    /// </summary>
    public const string SessionCookieName = "kb_admin_session";

    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Pbkdf2Iterations = 600_000;
    private static readonly HashAlgorithmName HashAlgorithm = HashAlgorithmName.SHA256;

    // Dummy salt and hash to execute constant-time dummy hashing when unconfigured or invalid.
    private static readonly byte[] DummySalt = new byte[SaltSizeBytes];
    private static readonly byte[] DummyPasswordHash = Rfc2898DeriveBytes.Pbkdf2(
        "dummy_password", DummySalt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);

    private readonly string _secretFilePath;
    private readonly byte[] _sessionSigningSecret = RandomNumberGenerator.GetBytes(32);
    private readonly TimeSpan _sessionTtl;
    private readonly TimeProvider _clock;
    private readonly ILogger<AdminAuthService> _logger;

    public AdminAuthService(IConfiguration config, TimeProvider clock, ILogger<AdminAuthService> logger)
    {
        _clock = clock;
        _logger = logger;
        
        var configuredPath = config["KnockBox:AdminPasswordPath"];
        _secretFilePath = !string.IsNullOrWhiteSpace(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.Combine(AppContext.BaseDirectory, "admin.secret");

        var sessionHours = config.GetValue("KnockBox:AdminSessionTtlHours", 8.0);
        _sessionTtl = TimeSpan.FromHours(sessionHours);
    }

    /// <summary>
    /// Path to the secret file holding the admin password hash.
    /// </summary>
    public string SecretFilePath => _secretFilePath;

    /// <summary>
    /// True if the admin password has been initialized.
    /// </summary>
    public bool IsConfigured => File.Exists(_secretFilePath);

    /// <summary>
    /// Sets the admin password. Throws if password is empty or already configured.
    /// </summary>
    public bool SetupPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password)) return false;
        if (IsConfigured) return false;

        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);

            var fileContent = $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";
            
            var dir = Path.GetDirectoryName(_secretFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            File.WriteAllText(_secretFilePath, fileContent, Encoding.UTF8);
            _logger.LogInformation("Admin password successfully set in secret file at '{Path}'.", _secretFilePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write admin password secret file at '{Path}'.", _secretFilePath);
            return false;
        }
    }

    /// <summary>
    /// Verifies the provided password against the stored password hash.
    /// Incorporates constant-time execution and comparison to mitigate timing side-channel attacks.
    /// </summary>
    public bool VerifyPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || !IsConfigured)
        {
            // Execute dummy hash calculation to match timing profile when unconfigured.
            _ = Rfc2898DeriveBytes.Pbkdf2(password ?? "", DummySalt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);
            CryptographicOperations.FixedTimeEquals(DummyPasswordHash, DummyPasswordHash);
            return false;
        }

        try
        {
            var fileContent = File.ReadAllText(_secretFilePath, Encoding.UTF8).Trim();
            var parts = fileContent.Split(':');
            if (parts.Length != 2)
            {
                // Malformed file -> run dummy hash and return false
                _ = Rfc2898DeriveBytes.Pbkdf2(password, DummySalt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);
                return false;
            }

            var salt = Convert.FromHexString(parts[0]);
            var expectedHash = Convert.FromHexString(parts[1]);

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);
            
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading or parsing admin secret file at '{Path}'.", _secretFilePath);
            // Run dummy hash to preserve timing invariant
            _ = Rfc2898DeriveBytes.Pbkdf2(password, DummySalt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);
            return false;
        }
    }

    /// <summary>
    /// Resets the admin password by deleting the secret file.
    /// </summary>
    public bool ResetPassword()
    {
        try
        {
            if (File.Exists(_secretFilePath))
            {
                File.Delete(_secretFilePath);
                _logger.LogInformation("Admin secret file deleted from '{Path}' (password reset).", _secretFilePath);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete admin secret file at '{Path}'.", _secretFilePath);
            return false;
        }
    }

    // ── Admin Session Token Management ─────────────────────────────────────
    public sealed record AdminSessionPayload(long Exp, string Nonce);

    public string CreateSessionToken()
    {
        var exp = _clock.GetUtcNow().Add(_sessionTtl).ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var payload = new AdminSessionPayload(exp, nonce);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, KnockBoxProtocolContext.Default.AdminSessionPayload);
        var body = Base64UrlEncode(payloadBytes);
        var sig = Sign(body);
        return $"{body}.{sig}";
    }

    public bool ValidateSessionToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || !IsConfigured) return false;
        var dot = token.LastIndexOf('.');
        if (dot <= 0) return false;

        var body = token[..dot];
        var sig = token[(dot + 1)..];

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(sig), Encoding.UTF8.GetBytes(Sign(body))))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AdminSessionPayload>(
                Base64UrlDecode(body), KnockBoxProtocolContext.Default.AdminSessionPayload);
            if (payload is not null)
            {
                return _clock.GetUtcNow().ToUnixTimeSeconds() < payload.Exp;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private string Sign(string data)
    {
        using var hmac = new HMACSHA256(_sessionSigningSecret);
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }
}
