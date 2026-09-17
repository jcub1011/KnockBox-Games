using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnockBox.Server.Security;

/// <summary>
/// The at-rest encryption key for the admin portal's notification store (the portal's
/// <c>localStorage</c> holds up to 50 notifications, and on a shared workstation that profile is
/// readable by whoever sits down next).
///
/// The key is a random 256-bit value held in server memory and handed to an already-authenticated
/// portal page over its session — never persisted, never logged, and never derived from the admin
/// password. Deriving it from the password would make the key password-equivalent material (anyone
/// extracting it from browser memory could test password guesses offline against it); a random key
/// verifies nothing about the password and so leaks nothing about it.
///
/// Rotation is bound to the password by FINGERPRINT, not by value: the key is tagged with
/// <see cref="AdminAuthService.CurrentSecretFingerprint"/> (SHA-256 over the secret file's bytes —
/// one-way, and never sent to any client) at mint time, and any change to the secret file (set,
/// reset, replaced, restored) makes the next call mint a fresh key. Stored notifications encrypted
/// under the old key stop decrypting, which is exactly the advertised contract: changing the admin
/// password clears stored notifications.
///
/// A transient read failure (AV/indexer holding the ~100-byte secret file) must NOT look like a
/// password change: the fingerprint is read with retries outside the lock, and only a positively-read
/// different value rotates. When every attempt fails, the existing key is kept (minted once and tagged
/// with the previous fingerprint when there is none yet), so the next successful read rotates exactly
/// once instead of once per call.
/// </summary>
public sealed class NotificationKeyService
{
    private readonly AdminAuthService _auth;
    private readonly ILogger<NotificationKeyService> _logger;
    private readonly int _maxAttempts;
    private readonly int _retryDelayMs;
    private readonly Lock _gate = new();
    private byte[] _fingerprint = [];
    private byte[] _key = [];

    public NotificationKeyService(
        AdminAuthService auth,
        ILogger<NotificationKeyService>? logger = null,
        int maxAttempts = 10,
        int retryDelayMs = 1000)
    {
        _auth = auth;
        _logger = logger ?? NullLogger<NotificationKeyService>.Instance;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelayMs = Math.Max(0, retryDelayMs);
    }

    /// <summary>
    /// The current data key, rotating it first when the password changed underneath. Returns a copy:
    /// the cached bytes stay owned by this service.
    /// </summary>
    public byte[] GetKey()
    {
        // Read outside the lock: a transient lock on the secret file is retried (10 x 1s by default)
        // rather than mistaken for a password change, and sleeping must never hold the gate.
        byte[] fingerprint = [];
        bool read = false;
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            if (_auth.TryGetSecretFingerprint(out fingerprint, logFailure: false))
            {
                read = true;
                break;
            }
            if (attempt == 0)
                _logger.LogWarning("Could not read admin secret file for notification key; retrying.");
            if (attempt + 1 < _maxAttempts && _retryDelayMs > 0)
                Thread.Sleep(_retryDelayMs);
        }

        lock (_gate)
        {
            if (!read)
            {
                // Still unreadable: keep serving the existing key (mint once when there is none),
                // tagged with the previous fingerprint so the next successful read rotates exactly once.
                if (_key.Length == 0)
                {
                    _logger.LogWarning("Admin secret file still unreadable; minted a fallback notification key.");
                    _key = RandomNumberGenerator.GetBytes(32);
                }
                return (byte[])_key.Clone();
            }
            if (_key.Length == 0 || !CryptographicOperations.FixedTimeEquals(fingerprint, _fingerprint))
            {
                _key = RandomNumberGenerator.GetBytes(32);
                _fingerprint = fingerprint;
            }
            return (byte[])_key.Clone();
        }
    }

    /// <summary>The current data key as base64, for the one authenticated endpoint that serves it.</summary>
    public string GetKeyBase64() => Convert.ToBase64String(GetKey());
}
