using KnockBox.Server.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KnockBox.Server.Security;

/// <summary>
/// Manages admin authentication, secure password hashing, and admin session tokens.
/// 
/// Password Storage & Reset:
/// - Password is saved in a secure file (<c>admin.secret</c> by default), created owner-read/write only
///   on Unix so another account on the box can't read the hash and crack it offline. (On Windows the file
///   inherits the directory's ACL — there is no portable equivalent.)
/// - Hashing uses PBKDF2-HMAC-SHA256 with 600,000 iterations (OWASP 2026 recommended standard for PBKDF2)
///   and a unique 16-byte salt per set.
/// - Password reset path: Deleting the secret file returns the server to Unconfigured / Setup mode.
///
/// The secret file IS the credential, so write access to it is total control — whoever can replace it can
/// simply delete it and claim a new password instead. That is inherent to a file-backed credential with no
/// external state (the same property <c>/etc/shadow</c> has) and is deliberately not defended against:
/// detecting a rollback needs monotonic state the attacker doesn't also control. Filesystem permissions are
/// the boundary. What IS defended: any change to that file invalidates every outstanding session (see
/// <see cref="CurrentSigningKey"/>), so resetting the password — or swapping the file back to an older one —
/// actually revokes access rather than leaving live sessions working until the next restart.
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

    /// <summary>
    /// Minimum admin password length. The portal is claim-on-first-use, so the very first password is
    /// chosen in a hurry — and it protects an operator dashboard reachable (in some deployments) from the
    /// network, whose hash may be readable by anyone with a shell on the box. A floor here is worth more
    /// than the friction costs.
    /// </summary>
    public const int MinPasswordLength = 12;

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
        
        _secretFilePath = ResolveSecretPath(config);

        var sessionHours = config.GetValue("KnockBox:AdminSessionTtlHours", 8.0);
        _sessionTtl = TimeSpan.FromHours(sessionHours);
    }

    /// <summary>
    /// Where the password hash lives, resolved the same way whether or not the service has been
    /// constructed yet. Static because bootstrap needs the answer BEFORE the DI container exists, to
    /// check whether the directory is actually persisted (see <c>StatePersistence</c>) while the
    /// diagnostics it reports into can still reach the startup log. Exposed rather than copied — a
    /// second implementation of this fallback would eventually disagree with this one about where the
    /// password is, and the symptom would be a warning naming a file nothing reads.
    /// </summary>
    /// <remarks>
    /// A configured RELATIVE path resolves against the process working directory, not the content
    /// root — unlike the six <c>ContentPaths</c> roots. Prefer an absolute path; the container sets
    /// one.
    /// </remarks>
    public static string ResolveSecretPath(IConfiguration config)
    {
        var configuredPath = config["KnockBox:AdminPasswordPath"];
        return !string.IsNullOrWhiteSpace(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.Combine(AppContext.BaseDirectory, "admin.secret");
    }

    /// <summary>
    /// Path to the secret file holding the admin password hash.
    /// </summary>
    public string SecretFilePath => _secretFilePath;

    /// <summary>
    /// True if the admin password has been initialized.
    /// </summary>
    public bool IsConfigured => File.Exists(_secretFilePath);

    /// <summary>Why a <see cref="SetupPassword"/> call was refused — the caller turns this into a message.</summary>
    public enum SetupOutcome
    {
        Success,
        /// <summary>A password already exists. Delete the secret file to reset (see the type remarks).</summary>
        AlreadyConfigured,
        /// <summary>Blank, or shorter than <see cref="MinPasswordLength"/>.</summary>
        PasswordTooWeak,
        /// <summary>The secret file couldn't be written — almost always a permissions or mount problem.</summary>
        StorageFailed,
    }

    /// <summary>
    /// Sets the admin password, but only while unconfigured — there is no way to overwrite an existing one
    /// through this API. Returns why it was refused rather than a bare false, because "already configured"
    /// and "your storage isn't writable" need very different operator responses and had previously been
    /// reported to the portal as the same message.
    /// </summary>
    /// <remarks>
    /// The <see cref="IsConfigured"/> test is a fast path, NOT the guard — the guard is the atomic
    /// <see cref="FileMode.CreateNew"/> below. Check-then-write is a race on the one path that matters
    /// here: this endpoint is claim-on-first-use, so two callers can both find it unconfigured, both
    /// "succeed", and both be handed a session — except the session key is derived from a fingerprint of
    /// the stored hash, so whichever write lost is holding a cookie signed under a key that no longer
    /// exists. That caller is locked out of a portal it was just told it had claimed, and cannot re-run
    /// setup either. Letting the filesystem arbitrate makes exactly one caller the winner.
    /// </remarks>
    public SetupOutcome SetupPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinPasswordLength)
            return SetupOutcome.PasswordTooWeak;
        if (IsConfigured) return SetupOutcome.AlreadyConfigured;

        try
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithm, HashSizeBytes);

            var fileContent = $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}";

            var dir = Path.GetDirectoryName(_secretFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            WriteSecretFile(fileContent);
            _logger.LogInformation("Admin password successfully set in secret file at '{Path}'.", _secretFilePath);
            return SetupOutcome.Success;
        }
        catch (IOException) when (File.Exists(_secretFilePath))
        {
            // CreateNew lost the race (or the file appeared between the check and the write): someone
            // else claimed the portal. Same answer as the fast path above.
            _logger.LogWarning("Refused a concurrent admin password setup: '{Path}' was claimed first.", _secretFilePath);
            return SetupOutcome.AlreadyConfigured;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write admin password secret file at '{Path}'.", _secretFilePath);
            return SetupOutcome.StorageFailed;
        }
    }

    /// <summary>
    /// Writes the secret with owner-only permissions where the platform has them. The mode is set at CREATE
    /// time rather than afterwards: a write-then-chmod leaves a window where the hash sits world-readable,
    /// and on a shared host that window is the whole attack. <see cref="FileMode.CreateNew"/> rather than
    /// <c>Create</c> so an existing secret is never truncated — see <see cref="SetupPassword"/>.
    /// </summary>
    private void WriteSecretFile(string fileContent)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        // UnixCreateMode is rejected outright on Windows, where the file instead inherits the directory ACL.
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using var stream = new FileStream(_secretFilePath, options);
        stream.Write(Encoding.UTF8.GetBytes(fileContent));
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
        using var hmac = new HMACSHA256(CurrentSigningKey());
        return Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(data)));
    }

    /// <summary>
    /// The session signing key: the per-process secret bound to a fingerprint of the CURRENT stored
    /// password hash. Deriving it per call rather than once at construction is what makes a secret change
    /// revoke sessions — replace, delete, or restore the file and every previously issued token stops
    /// verifying, because the key it was signed under no longer exists.
    ///
    /// Without this, a session outlived the credential it was granted under: an admin who reset a
    /// compromised password would find the attacker's existing session still valid until the next restart.
    /// </summary>
    private byte[] CurrentSigningKey() => HMACSHA256.HashData(_sessionSigningSecret, SecretFingerprint());

    /// <summary>
    /// SHA-256 of the secret file's bytes, or empty when unconfigured. Deliberately NOT cached: the file is
    /// ~100 bytes and admin traffic is a trickle, so reading it costs microseconds — while a cache keyed on
    /// (mtime, length) would be unsound here, since every secret file is exactly the same length and
    /// filesystem timestamp granularity is coarse enough for two successive writes to share a stamp.
    /// </summary>
    private byte[] SecretFingerprint()
    {
        try
        {
            return File.Exists(_secretFilePath) ? SHA256.HashData(File.ReadAllBytes(_secretFilePath)) : [];
        }
        catch (Exception ex)
        {
            // An unreadable secret must not authenticate anyone. Returning a random key makes every
            // signature comparison fail closed, rather than falling back to a constant an attacker
            // could rely on.
            _logger.LogWarning(ex, "Could not read admin secret file at '{Path}' to validate a session.", _secretFilePath);
            return RandomNumberGenerator.GetBytes(HashSizeBytes);
        }
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
