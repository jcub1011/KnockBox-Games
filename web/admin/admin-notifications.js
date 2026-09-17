// The admin portal's notification store: the persistent replacement for the toast system.
//
// Split of responsibilities, matching the admin-core.js/admin.js divide: this module owns the
// notification LIST (in-memory state, localStorage persistence, at-rest encryption) plus notify(),
// the same-shaped entry point toast() used to be. It owns NO page DOM — badge, drawer and modals
// live in admin.js, which subscribes here and renders. That keeps the one confirm dialog, the one
// Escape cascade and the one poll-timer discipline in the module that already has them.
//
// Encryption: localStorage on a shared workstation is readable by whoever sits down next, so the
// store is AES-GCM ciphertext under a server-minted data key (GET /admin/api/notifications/key,
// authenticated). The key is held in this module's memory ONLY — never persisted anywhere
// client-side, which would put it next to the ciphertext it protects. Rotation on password change
// is detected server-side by fingerprint; here a blob that no longer decrypts means exactly that,
// and the store is discarded with a flag the UI turns into the warned-about notice.
// The password itself is never involved: a password-derived key would be password-equivalent
// material (extract it and test guesses offline), while a random key verifies nothing and leaks
// nothing. See NotificationKeyService on the server side.
//
// WebCrypto needs a secure context (loopback counts; plain-HTTP LAN does not). Where subtle is
// missing the store degrades to PLAINTEXT plus a persistent UI warning — never a silent downgrade.

import {
  NOTIFICATION_LIMIT,
  NOTIFICATION_STORAGE_KEY,
  capNotifications,
  createNotification,
  dismissNotification,
  markAllRead,
  sanitizeNotifications,
  toggleRead,
} from './admin-core.js';

/** How long an arrival drawer stays up without interaction. */
export const NOTIFICATION_DRAWER_MS = 5000;

/**
 * Exit-animation durations, mirroring admin.css — change them together. The drawer fades back over
 * 160ms (notif-out), the notification modals settle back over 180ms (notif-fade-out/notif-modal-out).
 */
export const NOTIF_DRAWER_EXIT_MS = 160;
export const NOTIF_MODAL_EXIT_MS = 180;

let items = [];
let listeners = new Set();
let storage = null;
let keyBytes = null;
let onNewCallback = null;
let persistTimer = null;
let subtleOverride = undefined;
let plaintextFallback = false;
let decryptFailed = false;
// Set when a v1 (encrypted) blob was found but this origin cannot decrypt it
// (no WebCrypto, e.g. plain-HTTP LAN). While set, persistNow() must not write
// a v0 plaintext blob over it — that would destroy history the next
// secure-context visit could still read. Cleared once the v1 blob is
// successfully read, discarded as corrupt, or dropped on logout.
let unreadEncrypted = false;

function defaultStorage() {
  try {
    return typeof localStorage !== 'undefined' ? localStorage : null;
  } catch {
    return null;
  }
}

function resolveSubtle() {
  if (subtleOverride !== undefined) return subtleOverride;
  try {
    return globalThis.crypto?.subtle ?? null;
  } catch {
    return null;
  }
}

function emit() {
  const snapshot = [...items];
  for (const fn of listeners) {
    try { fn(snapshot); } catch { /* a subscriber must not break the store */ }
  }
}

function bytesToBase64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
  return btoa(s);
}

function base64ToBytes(b64) {
  const s = atob(String(b64 ?? ''));
  const out = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
  return out;
}

async function encryptItems(list, key, subtle) {
  const iv = new Uint8Array(12);
  globalThis.crypto.getRandomValues(iv);
  const imported = await subtle.importKey('raw', key, 'AES-GCM', false, ['encrypt', 'decrypt']);
  const data = new TextEncoder().encode(JSON.stringify(list));
  const ct = await subtle.encrypt({ name: 'AES-GCM', iv }, imported, data);
  return { v: 1, iv: bytesToBase64(iv), data: bytesToBase64(new Uint8Array(ct)) };
}

async function decryptEnvelope(env, key, subtle) {
  if (!env || env.v !== 1 || typeof env.iv !== 'string' || typeof env.data !== 'string') {
    throw new Error('Not an encrypted notification store.');
  }
  const imported = await subtle.importKey('raw', key, 'AES-GCM', false, ['decrypt']);
  const pt = await subtle.decrypt({ name: 'AES-GCM', iv: base64ToBytes(env.iv) }, imported, base64ToBytes(env.data));
  return JSON.parse(new TextDecoder().decode(pt));
}

/**
 * Points the store at a storage backend and an arrival callback. Called once from admin.js's wire();
 * tests pass a fake storage and no callback.
 */
export function initNotificationStore({ storage: s = defaultStorage(), onNew = null } = {}) {
  storage = s;
  onNewCallback = onNew;
}

/** The data key, held in memory only. Null clears it (logout). */
export function setNotificationKey(bytes) {
  keyBytes = bytes ? new Uint8Array(bytes) : null;
}

/**
 * Fetches the data key over the authenticated session. True when the page now holds a key; false
 * leaves the store memory-only (items still work for this session, they just aren't persisted).
 * A 401 here means the session went away — the caller funnels that through its auth check.
 */
export async function refreshNotificationKey() {
  try {
    const res = await fetch('/admin/api/notifications/key');
    if (res.status === 401) return { key: false, unauthorized: true };
    if (!res.ok) return { key: false, unauthorized: false };
    const body = await res.json().catch(() => null);
    if (!body || typeof body.key !== 'string' || !body.key) return { key: false, unauthorized: false };
    setNotificationKey(base64ToBytes(body.key));
    return { key: true, unauthorized: false };
  } catch {
    return { key: false, unauthorized: false };
  }
}

/** Drops the in-memory key. Logout calls this: the next login re-fetches the same password-bound key. */
export function clearNotificationKey() {
  keyBytes = null;
}

export function getNotifications() {
  return [...items];
}

export function getUnreadCount() {
  return items.filter((n) => n.read !== true).length;
}

export function getNotification(id) {
  return items.find((n) => n.id === id) ?? null;
}

/** True once a store has been kept or read as plaintext (no WebCrypto on this origin). */
export function isPlaintextFallback() {
  return plaintextFallback;
}

/** True when the stored blob could not be decrypted (password changed, or tampering). */
export function hadDecryptFailure() {
  return decryptFailed;
}

/**
 * True when an encrypted blob is stored but unreadable on this origin
 * (no WebCrypto here). The blob is left untouched so a secure context can
 * still read it; session items are memory-only meanwhile.
 */
export function hasUnreadEncrypted() {
  return unreadEncrypted;
}

/** Reads and clears the decrypt-failure flag, so the notice is raised exactly once. */
export function consumeDecryptFailure() {
  const was = decryptFailed;
  decryptFailed = false;
  return was;
}

/** True when items exist that cannot be persisted (key unavailable, crypto available). */
export function isMemoryOnly() {
  return items.length > 0 && !keyBytes && !plaintextFallback && resolveSubtle() !== null;
}

/**
 * True when storage holds any notification blob (v0 or v1), regardless of readability. Lets callers
 * tell "no stored notifications" apart from "stored but currently unavailable" (key fetch failed).
 */
export function hasStoredBlob() {
  let raw = null;
  try {
    raw = storage?.getItem(NOTIFICATION_STORAGE_KEY) ?? null;
  } catch {
    return false;
  }
  if (!raw) return false;
  try {
    const env = JSON.parse(raw);
    return !!env && typeof env === 'object' && (env.v === 0 || env.v === 1);
  } catch {
    return false;
  }
}

export function subscribe(fn) {
  listeners.add(fn);
  return () => listeners.delete(fn);
}

/**
 * Pushes a notification. Synchronous for the caller's sake (the old toast() was too): the
 * badge/list update from memory immediately, persistence flushes on a short debounce.
 */
export function notify(message, kind = 'info') {
  const item = createNotification({ message, kind });
  if (!item.message) return null;
  items = capNotifications([item, ...items], NOTIFICATION_LIMIT);
  emit();
  schedulePersist();
  if (onNewCallback) {
    try { onNewCallback(item); } catch { /* preview must not break the push */ }
  }
  return item.id;
}

export function markNotificationRead(id, read = true) {
  const next = items.map((n) => (n.id === id ? { ...n, read: read === true } : n));
  items = next;
  emit();
  schedulePersist();
}

export function toggleNotificationRead(id) {
  items = toggleRead(items, id);
  emit();
  schedulePersist();
}

export function markAllNotificationsRead() {
  items = markAllRead(items);
  emit();
  schedulePersist();
}

export function dismissOneNotification(id) {
  items = dismissNotification(items, id);
  emit();
  schedulePersist();
}

export function clearNotifications() {
  items = [];
  emit();
  schedulePersist();
}

/**
 * Reads the store from storage. Call after refreshNotificationKey(): an encrypted blob with no key
 * yet is left for the retry, not treated as corrupt. Corrupt-or-undecryptable input discards the
 * store and raises hadDecryptFailure() exactly once.
 */
export async function loadNotifications() {
  let raw = null;
  try {
    raw = storage?.getItem(NOTIFICATION_STORAGE_KEY) ?? null;
  } catch {
    raw = null;
  }
  if (!raw) {
    items = [];
    emit();
    return;
  }

  let env = null;
  try {
    env = JSON.parse(raw);
  } catch {
    env = null;
  }
  if (!env || typeof env !== 'object') {
    return corrupted();
  }

  if (env.v === 0 && Array.isArray(env.items)) {
    plaintextFallback = true;
    items = sanitizeNotifications(env.items, NOTIFICATION_LIMIT);
    emit();
    return;
  }

  if (env.v === 1) {
    const subtle = resolveSubtle();
    if (!subtle) {
      // Insecure origin: the ciphertext is intact but undecryptable here.
      // Mark it so persistNow() stays memory-only instead of overwriting it
      // with a v0 plaintext containing only this session's items. A missing
      // key alone (secure origin, key not fetched yet) stays a silent retry.
      unreadEncrypted = true;
      emit();
      return;
    }
    if (!keyBytes) return; // key not fetched yet — the caller retries after fetching
    try {
      items = sanitizeNotifications(await decryptEnvelope(env, keyBytes, subtle), NOTIFICATION_LIMIT);
    } catch {
      return corrupted();
    }
    unreadEncrypted = false;
    plaintextFallback = false;
    emit();
    return;
  }

  return corrupted();
}

function corrupted() {
  decryptFailed = true;
  unreadEncrypted = false;
  items = [];
  try {
    storage?.removeItem(NOTIFICATION_STORAGE_KEY);
  } catch {
    // A storage that can't be read may not be writable either; in-memory state stays authoritative.
  }
  emit();
}

export function schedulePersist() {
  if (persistTimer !== null) return;
  persistTimer = setTimeout(() => {
    persistTimer = null;
    persistNow();
  }, 150);
}

/** Cancels a pending debounced write. Exported for the jsdom tests (the stopPolling() trap). */
export function stopPersistTimer() {
  if (persistTimer !== null) clearTimeout(persistTimer);
  persistTimer = null;
}

export async function persistNow() {
  if (!storage) return;
  const subtle = resolveSubtle();
  try {
    if (subtle && keyBytes) {
      storage.setItem(NOTIFICATION_STORAGE_KEY, JSON.stringify(await encryptItems(items, keyBytes, subtle)));
      if (plaintextFallback) {
        plaintextFallback = false;
        emit();
      }
    } else if (!subtle) {
      if (unreadEncrypted) return; // an unread v1 blob is stored: stay memory-only, never clobber it
      plaintextFallback = true;
      storage.setItem(NOTIFICATION_STORAGE_KEY, JSON.stringify({ v: 0, items }));
    }
    // Else: crypto available but no key (logged out, or the key endpoint failed) — memory-only.
    // Writing plaintext here would silently downgrade the store, so nothing is written.
  } catch {
    // Quota or a hostile storage backend: in-memory state stays authoritative for the session.
  }
}

/**
 * Drops everything in memory without touching storage. Logout calls this
 * after clearNotificationKey(): decrypted items are plaintext regardless of
 * the at-rest form, so leaving them behind the login view breaks the shared-
 * workstation guarantee. The stored blob is left intact for the next login;
 * a pending debounce is cancelled first so it cannot persist the cleared
 * list over it. Unsaved memory-only items are intentionally discarded.
 */
export function unloadNotificationsForLogout() {
  stopPersistTimer();
  items = [];
  unreadEncrypted = false;
  plaintextFallback = false;
  decryptFailed = false;
  emit();
}

// ── Test seams ────────────────────────────────────────────────────────────────

export function setSubtleForTests(subtle) {
  subtleOverride = subtle;
}

export function resetNotificationsForTests() {
  stopPersistTimer();
  items = [];
  listeners = new Set();
  storage = null;
  keyBytes = null;
  onNewCallback = null;
  subtleOverride = undefined;
  plaintextFallback = false;
  decryptFailed = false;
  unreadEncrypted = false;
}
