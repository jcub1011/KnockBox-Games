using System.Security.Cryptography;

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
/// </summary>
public sealed class NotificationKeyService
{
    private readonly AdminAuthService _auth;
    private readonly Lock _gate = new();
    private byte[] _fingerprint = [];
    private byte[] _key = [];

    public NotificationKeyService(AdminAuthService auth)
    {
        _auth = auth;
    }

    /// <summary>
    /// The current data key, rotating it first when the password changed underneath. Returns a copy:
    /// the cached bytes stay owned by this service.
    /// </summary>
    public byte[] GetKey()
    {
        lock (_gate)
        {
            var fingerprint = _auth.CurrentSecretFingerprint();
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
