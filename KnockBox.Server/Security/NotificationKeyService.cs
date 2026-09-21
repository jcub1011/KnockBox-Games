using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KnockBox.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace KnockBox.Server.Security;

/// <summary>
/// The at-rest encryption key for the admin portal's notification store (the portal's
/// <c>localStorage</c> holds up to 50 notifications, and on a shared workstation that profile is
/// readable by whoever sits down next).
///
/// The key is a random 256-bit value per admin account, handed to an already-authenticated
/// portal page over its session — held in server memory, persisted to a server-side file so it
/// survives restarts, kept in page memory only client-side (never in browser storage, which would
/// put it next to the ciphertext it protects), never logged, and never derived from the admin
/// password. Deriving it from the password would make the key password-equivalent material (anyone
/// extracting it from browser memory could test password guesses offline against it); a random key
/// verifies nothing about the password and so leaks nothing about it.
///
/// Rotation is bound to the password by FINGERPRINT, not by value: each account's key is tagged with
/// <see cref="AdminAuthService.CurrentSecretFingerprint"/> (SHA-256 over the secret file's bytes —
/// one-way, and never sent to any client) at mint time, and any change to the secret file (set,
/// reset, replaced, restored) makes the next call mint a fresh key. Stored notifications encrypted
/// under the old key stop decrypting, which is exactly the advertised contract: changing the admin
/// password clears stored notifications.
///
/// Multi-account shape, single account today: entries are keyed by an opaque account id. The only
/// account that exists yet is <see cref="DefaultAccountId"/>, bound to the single admin password;
/// when username+password accounts land, the current password becomes the first account under its
/// existing id (so its history carries over), the session carries the caller's id, and the
/// <c>/admin/api/notifications/key</c> handler passes it through — no key-file migration, since
/// one account's rotation already never touches another's entry. Per-account password fingerprints
/// replace the shared secret fingerprint at that point for the same reason.
/// </summary>
public sealed class NotificationKeyService
{
    /// <summary>
    /// The account id used until multi-account auth exists. Stable on purpose: when the current
    /// single password becomes the first account, it keeps this id and its notification history.
    /// </summary>
    public const string DefaultAccountId = "default";

    /// <summary>The file's name when <c>KnockBox:AdminNotificationKeyPath</c> isn't set. It lands beside the
    /// admin password file, which is already required to be writable and, in a container, on a
    /// persisted volume outside the image — exactly the properties this file needs.</summary>
    private const string DefaultFileName = "admin-notifications.key.json";

    private const int KeySizeBytes = 32;

    private readonly AdminAuthService _auth;
    private readonly ILogger<NotificationKeyService> _logger;
    private readonly int _maxAttempts;
    private readonly int _retryDelayMs;
    private readonly string _keyFilePath;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;

    private sealed class Entry
    {
        public required byte[] Fingerprint;
        public required byte[] Key;
        /// <summary>False when minted but not yet written (or the write failed): the next matching
        /// call retries the persist, so an unwritable file degrades to "served in memory" rather
        /// than silently re-minting.</summary>
        public bool Persisted;
    }

    public NotificationKeyService(
        AdminAuthService auth,
        IConfiguration? config = null,
        ILogger<NotificationKeyService>? logger = null,
        int maxAttempts = 10,
        int retryDelayMs = 1000,
        string? keyFilePath = null)
    {
        _auth = auth;
        _logger = logger ?? NullLogger<NotificationKeyService>.Instance;
        _maxAttempts = Math.Max(1, maxAttempts);
        _retryDelayMs = Math.Max(0, retryDelayMs);
        _keyFilePath = keyFilePath
            ?? (config is null
                ? Path.Combine(Path.GetDirectoryName(auth.SecretFilePath) ?? AppContext.BaseDirectory, DefaultFileName)
                : ResolveKeyPath(config, auth.SecretFilePath));
    }

    /// <summary>
    /// Where the key file lives, resolved the same way whether or not the service has been
    /// constructed yet. Static for the same reason as <see cref="AdminAuthService.ResolveSecretPath"/>:
    /// bootstrap checks whether this file is on persisted storage before DI exists.
    /// </summary>
    public static string ResolveKeyPath(IConfiguration config, string secretFilePath)
    {
        var configured = config["KnockBox:AdminNotificationKeyPath"];
        return !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Path.GetDirectoryName(secretFilePath) ?? AppContext.BaseDirectory, DefaultFileName);
    }

    /// <summary>Where the key file lives. Surfaced so an error message can name it.</summary>
    public string KeyFilePath => _keyFilePath;

    /// <summary>
    /// The account's data key, rotating it first when the password changed underneath. Returns a copy:
    /// the cached bytes stay owned by this service.
    /// </summary>
    public byte[] GetKey(string? accountId = DefaultAccountId)
    {
        var id = NormalizeAccountId(accountId);

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
            EnsureLoaded_Locked();
            _entries.TryGetValue(id, out var entry);

            if (!read)
            {
                // Still unreadable: keep serving the existing key (mint once when there is none),
                // untagged so the next successful read rotates exactly once instead of once per call.
                // Never persisted without a fingerprint to bind it to (Persist_Locked skips
                // untagged entries, so a later call for another account cannot sweep this one to disk).
                if (entry is not null)
                    return (byte[])entry.Key.Clone();
                _logger.LogWarning("Admin secret file still unreadable; minted a fallback notification key.");
                var fallback = RandomNumberGenerator.GetBytes(KeySizeBytes);
                _entries[id] = new Entry { Fingerprint = [], Key = fallback, Persisted = true };
                return (byte[])fallback.Clone();
            }

            if (entry is not null
                && entry.Key.Length == KeySizeBytes
                && CryptographicOperations.FixedTimeEquals(fingerprint, entry.Fingerprint))
            {
                if (!entry.Persisted)
                    Persist_Locked();
                return (byte[])entry.Key.Clone();
            }

            var fresh = RandomNumberGenerator.GetBytes(KeySizeBytes);
            _entries[id] = new Entry { Fingerprint = fingerprint, Key = fresh, Persisted = false };
            Persist_Locked();
            return (byte[])fresh.Clone();
        }
    }

    /// <summary>The account's data key as base64, for the one authenticated endpoint that serves it.</summary>
    public string GetKeyBase64(string? accountId = DefaultAccountId) => Convert.ToBase64String(GetKey(accountId));

    /// <summary>
    /// Account ids are opaque strings owned by the auth layer (usernames, eventually). The only rule
    /// enforced here: blank means the pre-multi-account account.
    /// </summary>
    private static string NormalizeAccountId(string? accountId) =>
        string.IsNullOrWhiteSpace(accountId) ? DefaultAccountId : accountId;

    /// <summary>Reads the key file once, tolerantly: missing means first boot, corrupt means re-mint
    /// (one clearing event, logged) rather than a broken endpoint. Only well-formed 32-byte entries
    /// are admitted; anything else is dropped with a warning.</summary>
    private void EnsureLoaded_Locked()
    {
        if (_loaded)
            return;
        _loaded = true;

        byte[] json;
        try
        {
            if (!File.Exists(_keyFilePath))
                return;
            json = File.ReadAllBytes(_keyFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read notification key file at '{Path}'; keys will be minted in memory.", _keyFilePath);
            return;
        }

        NotificationKeyFile parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, NotificationKeyFileJsonContext.Default.NotificationKeyFile)
                ?? new NotificationKeyFile(0, new Dictionary<string, NotificationKeyEntry>());
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Notification key file at '{Path}' is corrupt; minting fresh keys.", _keyFilePath);
            return;
        }

        foreach (var (id, record) in parsed.Keys ?? new Dictionary<string, NotificationKeyEntry>())
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            byte[] fingerprint, key;
            try
            {
                fingerprint = Convert.FromHexString(record.Fingerprint ?? "");
                key = Convert.FromBase64String(record.Key ?? "");
            }
            catch (FormatException)
            {
                _logger.LogWarning("Dropping malformed notification key entry for account '{AccountId}'.", id);
                continue;
            }
            if (key.Length != KeySizeBytes)
            {
                _logger.LogWarning("Dropping notification key entry for account '{AccountId}' with an unexpected length.", id);
                continue;
            }
            _entries[id] = new Entry { Fingerprint = fingerprint, Key = key, Persisted = true };
        }
    }

    /// <summary>Writes the whole map atomically (temp + overwriting rename, the <see cref="AtomicFile"/>
    /// discipline). Best-effort by design — like <c>AdminSettingsStore.Save</c>, a failure is a warning
    /// naming the path while the in-memory key keeps serving, so an unwritable file degrades to
    /// "stable until restart" instead of breaking the key endpoint.</summary>
    private void Persist_Locked()
    {
        var records = new Dictionary<string, NotificationKeyEntry>(StringComparer.Ordinal);
        foreach (var (id, entry) in _entries)
        {
            if (entry.Key.Length != KeySizeBytes)
                continue;
            // Untagged fallbacks (minted while the secret file was unreadable) carry no fingerprint
            // to bind them to, so they stay memory-only: persisting one would write a key whose next
            // successful read cannot tell it apart from a password change, and another account's
            // persist would otherwise sweep it to disk.
            if (entry.Fingerprint.Length == 0)
                continue;
            records[id] = new NotificationKeyEntry(
                Convert.ToHexString(entry.Fingerprint), Convert.ToBase64String(entry.Key));
        }

        var temp = _keyFilePath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_keyFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.SerializeToUtf8Bytes(
                new NotificationKeyFile(1, records), NotificationKeyFileJsonContext.Default.NotificationKeyFile);
            if (!OperatingSystem.IsWindows())
            {
                // Owner-only at CREATE time, like the secret file itself: a write-then-chmod leaves a
                // window where decryption keys sit readable, and these keys decrypt operator history.
                using var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
                stream.Write(json, 0, json.Length);
            }
            else
            {
                File.WriteAllBytes(temp, json);
            }
            AtomicFile.MoveWithRetry(temp, _keyFilePath);
            foreach (var (id, entry) in _entries)
            {
                if (records.ContainsKey(id))
                    entry.Persisted = true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist notification keys to '{Path}'; serving in-memory keys until restart.", _keyFilePath);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }
}

/// <summary>The persisted form of one account's entry: hex fingerprint plus base64 key.</summary>
internal sealed record NotificationKeyEntry(string Fingerprint, string Key);

/// <summary>The persisted key file: versioned, mapping account id to entry. A map rather than a
/// single key so the second account is a new entry, not a schema change.</summary>
internal sealed record NotificationKeyFile(int Version, Dictionary<string, NotificationKeyEntry> Keys);

/// <summary>
/// Source-generated serializer for the notification key file. Reflection-based JSON is not
/// Native-AOT-safe (<c>PublishAot</c> is on; the <c>aot</c> CI job treats trim warnings as errors),
/// so this file goes through the same source-generated serializer as the settings file.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(NotificationKeyFile))]
[JsonSerializable(typeof(NotificationKeyEntry))]
[JsonSerializable(typeof(Dictionary<string, NotificationKeyEntry>))]
internal partial class NotificationKeyFileJsonContext : JsonSerializerContext { }
