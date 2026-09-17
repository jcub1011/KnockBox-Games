// @vitest-environment jsdom
//
// The admin portal's notification system: the bell badge, the arrival drawer, the list modal with
// per-item and bulk actions, the details modal, and the encrypted localStorage store.
//
// admin.js is side-effecting on import like shell.js, so each test resets modules, injects the REAL
// admin/index.html, stubs fetch, and imports both modules fresh — the service import resolves to the
// same instance admin.js renders from, which is exactly the seam under test.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { installFakeFetch, loadAdminDom, tick } from './helpers.js';

const el = (id) => document.getElementById(id);

let admin;
let notifs;

async function importBoth() {
  admin = await import('../admin/admin.js');
  notifs = await import('../admin/admin-notifications.js');
}

function authedRoutes(overrides = {}) {
  return {
    'GET /admin/api/auth/status': { body: { configured: true, authenticated: true } },
    ...overrides,
  };
}

async function bootstrapAuthed(overrides = {}) {
  installFakeFetch(authedRoutes(overrides));
  await importBoth();
  admin.bootstrap();
  await tick();
  await tick();
  await tick();
}

beforeEach(() => {
  vi.resetModules();
  loadAdminDom();
  window.location.hash = '';
  try { localStorage.clear(); } catch { /* ignore */ }
});

afterEach(() => {
  try { admin?.stopNotifDrawerTimer(); } catch { /* never bootstrapped */ }
  try { admin?.stopNotifExitTimers(); } catch { /* never bootstrapped */ }
  try { notifs?.stopPersistTimer(); } catch { /* never imported */ }
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  try { localStorage.clear(); } catch { /* ignore */ }
});

describe('bell badge', () => {
  it('is hidden with no notifications and counts unread', async () => {
    await bootstrapAuthed();
    expect(el('notif-badge').classList.contains('hidden')).toBe(true);

    notifs.notify('First failure', 'error');
    expect(el('notif-badge').classList.contains('hidden')).toBe(false);
    expect(el('notif-badge').textContent).toBe('1');
    expect(el('notif-bell-btn').getAttribute('aria-label')).toContain('1 unread');
  });

  it('caps the displayed count at 99+', async () => {
    await bootstrapAuthed();
    for (let i = 0; i < 105; i++) notifs.notify(`Notice ${i}`, 'info');
    // The store itself caps at 50, dropping the oldest.
    expect(notifs.getNotifications()).toHaveLength(50);
    expect(el('notif-badge').textContent).toBe('50');
  });

  it('stays hidden until the dashboard is shown', async () => {
    installFakeFetch({ 'GET /admin/api/auth/status': { body: { configured: true, authenticated: false } } });
    await importBoth();
    admin.bootstrap();
    await tick();
    expect(el('notif-bell-wrap').classList.contains('hidden')).toBe(true);
  });
});

describe('arrival drawer', () => {
  it('opens on arrival showing the three newest, and closes animated via its close button', async () => {
    vi.useFakeTimers();
    await bootstrapAuthed();
    notifs.notify('One', 'info');
    notifs.notify('Two', 'warning');
    notifs.notify('Three', 'error');
    notifs.notify('Four', 'success');

    const drawer = el('notif-drawer');
    expect(drawer.classList.contains('hidden')).toBe(false);
    const items = el('notif-drawer-items').querySelectorAll('.notif-item');
    expect(items).toHaveLength(3);
    expect(el('notif-drawer-items').textContent).toContain('Four');
    expect(el('notif-drawer-items').textContent).not.toContain('One');

    el('notif-drawer-close').click();
    // The exit animation plays first: still visible, carrying the closing class…
    expect(drawer.classList.contains('hidden')).toBe(false);
    expect(drawer.classList.contains('notif-drawer-closing')).toBe(true);
    await vi.advanceTimersByTimeAsync(200);
    expect(drawer.classList.contains('hidden')).toBe(true);
    expect(drawer.classList.contains('notif-drawer-closing')).toBe(false);
    // Dismissing the preview marks nothing read.
    expect(el('notif-badge').textContent).toBe('4');
  });

  it('opens on bell hover even with nothing new since the last opening', async () => {
    vi.useFakeTimers();
    await bootstrapAuthed();
    notifs.notify('Only one', 'info');
    el('notif-drawer-close').click();
    await vi.advanceTimersByTimeAsync(200);
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);

    el('notif-bell-btn').dispatchEvent(new Event('mouseenter'));
    expect(el('notif-drawer').classList.contains('hidden')).toBe(false);
    expect(el('notif-drawer-items').textContent).toContain('Only one');
  });

  it('auto-dismisses after five seconds unless hovered', async () => {
    await bootstrapAuthed();
    vi.useFakeTimers();
    notifs.notify('Ephemeral', 'info');
    expect(el('notif-drawer').classList.contains('hidden')).toBe(false);

    // The auto-dismiss runs the same exit animation as a manual close…
    await vi.advanceTimersByTimeAsync(5000);
    expect(el('notif-drawer').classList.contains('hidden')).toBe(false);
    expect(el('notif-drawer').classList.contains('notif-drawer-closing')).toBe(true);
    await vi.advanceTimersByTimeAsync(200);
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);

    // Hover holds it: the dismiss timer is paused while held…
    notifs.notify('Held', 'info');
    el('notif-drawer').dispatchEvent(new Event('mouseenter'));
    await vi.advanceTimersByTimeAsync(20000);
    expect(el('notif-drawer').classList.contains('hidden')).toBe(false);
    // …and leaving dismisses at once through the exit animation, with no second grace period.
    el('notif-drawer').dispatchEvent(new Event('mouseleave'));
    expect(el('notif-drawer').classList.contains('notif-drawer-closing')).toBe(true);
    await vi.advanceTimersByTimeAsync(200);
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);
  });

  it('stays shut while the list modal is open', async () => {
    await bootstrapAuthed();
    el('notif-bell-btn').click();
    expect(el('notifications-backdrop').classList.contains('hidden')).toBe(false);

    notifs.notify('While modal', 'info');
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);
    // The badge still moves — suppression is about attention, not information.
    expect(el('notif-badge').textContent).toBe('1');
  });
});

describe('list modal', () => {
  it('opens from the bell and lists every notification with read controls', async () => {
    await bootstrapAuthed();
    notifs.notify('Alpha problem', 'error');
    notifs.notify('Beta note', 'info');
    el('notif-drawer-close').click();

    el('notif-bell-btn').click();
    expect(el('notifications-backdrop').classList.contains('hidden')).toBe(false);
    const rows = el('notifications-list').querySelectorAll('.notif-row');
    expect(rows).toHaveLength(2);
    expect(el('notifications-unread').textContent).toBe('2 unread');

    // Per-item toggle.
    const firstToggle = rows[0].querySelectorAll('button')[0];
    expect(firstToggle.textContent).toBe('Mark read');
    firstToggle.click();
    expect(el('notifications-unread').textContent).toBe('1 unread');
    expect(el('notif-badge').textContent).toBe('1');

    // Per-item dismiss.
    const firstDismiss = rows[0].querySelectorAll('button')[1];
    firstDismiss.click();
    expect(el('notifications-list').querySelectorAll('.notif-row')).toHaveLength(1);
  });

  it('marks all read and dismisses all behind a confirm', async () => {
    await bootstrapAuthed();
    notifs.notify('One', 'info');
    notifs.notify('Two', 'info');
    el('notif-bell-btn').click();

    el('notifications-mark-all').click();
    expect(el('notifications-unread').textContent).toBe('0 unread');
    expect(el('notif-badge').classList.contains('hidden')).toBe(true);

    el('notifications-dismiss-all').click();
    // Destructive, so it asks first — through the portal's one confirm dialog.
    expect(el('confirm-backdrop').classList.contains('hidden')).toBe(false);
    el('confirm-ok').click();
    await tick();
    expect(notifs.getNotifications()).toHaveLength(0);
    expect(el('notifications-empty').classList.contains('hidden')).toBe(false);
  });

  it('warns when the store fell back to plaintext', async () => {
    await bootstrapAuthed();
    notifs.setSubtleForTests(null);
    notifs.notify('Plain', 'info');
    await notifs.persistNow();
    expect(notifs.isPlaintextFallback()).toBe(true);

    el('notif-bell-btn').click();
    expect(el('notifications-note').classList.contains('hidden')).toBe(false);
    expect(el('notifications-note').textContent).toContain('unencrypted');
  });
});

describe('details modal', () => {
  it('opens on row click with the full text and seconds, marking it read', async () => {
    vi.useFakeTimers();
    await bootstrapAuthed();
    notifs.notify('Full story here', 'warning');
    el('notif-bell-btn').click();

    el('notifications-list').querySelector('.notif-row').click();
    expect(el('notification-details-backdrop').classList.contains('hidden')).toBe(false);
    const body = el('notification-details-body');
    expect(body.textContent).toContain('Full story here');
    expect(body.textContent).toContain('Warning');
    // Opening the dedicated modal auto-marks the notification read.
    expect(body.textContent).toContain('Read');
    expect(body.textContent).not.toContain('Unread');
    expect(el('notif-badge').classList.contains('hidden')).toBe(true);

    el('notification-details-toggle').click();
    expect(el('notif-badge').classList.contains('hidden')).toBe(false);
    expect(el('notif-badge').textContent).toBe('1');
    expect(el('notification-details-toggle').textContent).toBe('Mark read');

    el('notification-details-dismiss').click();
    await vi.advanceTimersByTimeAsync(200);
    expect(el('notification-details-backdrop').classList.contains('hidden')).toBe(true);
    expect(notifs.getNotifications()).toHaveLength(0);
  });

  it('renders the received time with seconds', async () => {
    await bootstrapAuthed();
    // Seconds are timezone-invariant, so a fixed instant pins them whatever the runner's zone is.
    const item = notifs.notify('Timed', 'info');
    const stored = notifs.getNotification(item);
    expect(stored).not.toBeNull();
    admin.openNotificationDetails(item);
    // The short list form has no seconds; the details form must show them.
    const seconds = String(new Date(stored.at).getSeconds()).padStart(2, '0');
    expect(el('notification-details-body').textContent).toContain(`:${seconds}`);
  });
});

describe('modal animation and stacking', () => {
  it('opens the list modal at once and closes it through the exit animation', async () => {
    vi.useFakeTimers();
    await bootstrapAuthed();
    notifs.notify('Animated', 'info');

    el('notif-bell-btn').click();
    const bd = el('notifications-backdrop');
    expect(bd.classList.contains('hidden')).toBe(false);

    el('notifications-close').click();
    expect(bd.classList.contains('hidden')).toBe(false);
    expect(bd.classList.contains('modal-closing')).toBe(true);
    await vi.advanceTimersByTimeAsync(200);
    expect(bd.classList.contains('hidden')).toBe(true);
    expect(bd.classList.contains('modal-closing')).toBe(false);
  });

  it('dismisses the list modal on backdrop click and Escape, animated', async () => {
    vi.useFakeTimers();
    await bootstrapAuthed();
    notifs.notify('Animated', 'info');
    el('notif-bell-btn').click();

    el('notifications-backdrop').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await vi.advanceTimersByTimeAsync(200);
    expect(el('notifications-backdrop').classList.contains('hidden')).toBe(true);

    el('notif-bell-btn').click();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await vi.advanceTimersByTimeAsync(200);
    expect(el('notifications-backdrop').classList.contains('hidden')).toBe(true);
  });
});

describe('notification stacking (CSS contract)', () => {
  // jsdom never loads the linked stylesheet, so stacking can't be observed in the DOM — pin the
  // declarations instead, the same repo-file-consistency shape AdminRouteGuardTests uses for the
  // route table. This is the test for "the Dismiss-all confirm opens behind the notification modal".
  function adminCss() {
    return readFileSync(resolve(process.cwd(), 'admin/admin.css'), 'utf8');
  }

  function zIndexOf(css, selectorPattern) {
    const m = css.match(new RegExp(`${selectorPattern}\\s*\\{[^}]*?z-index:\\s*(\\d+)`, 's'));
    return m ? Number(m[1]) : null;
  }

  it('paints the confirm dialog above every notification surface and the header', () => {
    const css = adminCss();
    const backdrop = zIndexOf(css, '\\.modal-backdrop');
    const drawer = zIndexOf(css, '\\.notif-drawer');
    const confirm = zIndexOf(css, '#confirm-backdrop');
    const header = zIndexOf(css, '\\.admin-header');

    expect(backdrop).not.toBeNull();
    expect(drawer).not.toBeNull();
    expect(confirm).not.toBeNull();
    expect(header).not.toBeNull();
    expect(confirm).toBeGreaterThan(backdrop);
    expect(confirm).toBeGreaterThan(drawer);
    expect(backdrop).toBeGreaterThan(header);
    expect(backdrop).toBeGreaterThan(drawer);
  });

  it('docks the drawer to the bottom on mobile so the header cannot cover its buttons', () => {
    const css = adminCss();
    const mobile = css.slice(css.indexOf('@media (max-width: 860px)'));
    expect(mobile).toContain('.notif-drawer');
    const m = mobile.match(/\.notif-drawer\s*\{[^}]*?bottom:\s*([^;]+);/s);
    expect(m).not.toBeNull();
  });

  it('keeps the exit durations in sync between JS and CSS', async () => {
    const css = adminCss();
    notifs = await import('../admin/admin-notifications.js');
    // Both sides name their durations explicitly so a change to one without the other reads wrong:
    // the drawer fades over NOTIF_DRAWER_EXIT_MS, the modals over NOTIF_MODAL_EXIT_MS.
    expect(css).toContain(`notif-out ${notifs.NOTIF_DRAWER_EXIT_MS}ms`);
    expect(css).toContain(`notif-modal-out ${notifs.NOTIF_MODAL_EXIT_MS}ms`);
    expect(css).toContain(`notif-fade-out ${notifs.NOTIF_MODAL_EXIT_MS}ms`);
    // Exit animations hold their end state until the timeout hides the element — without `forwards`
    // the drawer snaps back to full opacity first, which reads as a stutter.
    expect(css).toMatch(/notif-out \d+ms ease forwards/);
    expect(css).toMatch(/notif-modal-out \d+ms ease forwards/);
    expect(css).toMatch(/notif-fade-out \d+ms ease forwards/);
  });
});

describe('touch devices (no hover)', () => {
  function noHover() {
    window.matchMedia = vi.fn(() => ({ matches: false }));
  }

  afterEach(() => {
    // Direct assignment, not a stub — unstubAllGlobals won't remove it.
    delete window.matchMedia;
  });

  it('bell tap opens the modal directly with no drawer fight', async () => {
    await bootstrapAuthed();
    noHover();
    notifs.notify('Tapped', 'info');

    // Arrivals move the badge but never pop the drawer where there is no hover to hold it.
    expect(el('notif-badge').textContent).toBe('1');
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);

    // Even emulated mouseenter (which touch taps can fire before click) opens nothing…
    el('notif-bell-btn').dispatchEvent(new Event('mouseenter'));
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);

    // …so the tap lands straight in the list with no flash of drawer first.
    el('notif-bell-btn').click();
    expect(el('notifications-backdrop').classList.contains('hidden')).toBe(false);
    expect(el('notif-drawer').classList.contains('hidden')).toBe(true);
    expect(el('notifications-list').textContent).toContain('Tapped');
  });

  it('reports hover-capable by default (including jsdom without matchMedia)', async () => {
    await bootstrapAuthed();
    expect(admin.canHoverPreview()).toBe(true);
    noHover();
    expect(admin.canHoverPreview()).toBe(false);
  });
});

describe('encrypted store', () => {
  beforeEach(async () => {
    // No admin.js here: these drive the store directly against a fake backend.
    notifs = await import('../admin/admin-notifications.js');
  });

  function memoryStorage() {
    const map = new Map();
    return {
      getItem: (k) => (map.has(k) ? map.get(k) : null),
      setItem: (k, v) => map.set(k, String(v)),
      removeItem: (k) => map.delete(k),
    };
  }

  const keyA = new Uint8Array(32).fill(7);
  const keyB = new Uint8Array(32).fill(9);

  it('persists ciphertext that round-trips under the same key', async () => {
    const storage = memoryStorage();
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyA);
    notifs.notify('Secret outcome', 'error');
    notifs.notify('Another', 'info');
    await notifs.persistNow();

    const raw = storage.getItem('kb.admin.notifications');
    expect(raw).not.toBeNull();
    expect(raw).toContain('"v":1');
    expect(raw).not.toContain('Secret outcome');

    // A fresh module state with the same key reads it back (the relogin path).
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyA);
    await notifs.loadNotifications();
    expect(notifs.getNotifications()).toHaveLength(2);
    expect(notifs.getNotifications().map((n) => n.message)).toContain('Secret outcome');
  });

  it('discards the store when the key rotated, raising the flag once', async () => {
    const storage = memoryStorage();
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyA);
    notifs.notify('Old secret', 'info');
    await notifs.persistNow();

    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyB); // the password changed: the server minted a new data key
    await notifs.loadNotifications();
    expect(notifs.getNotifications()).toHaveLength(0);
    expect(notifs.hadDecryptFailure()).toBe(true);
    expect(notifs.consumeDecryptFailure()).toBe(true);
    expect(notifs.consumeDecryptFailure()).toBe(false);
  });

  it('keeps plaintext readable with the fallback flag set', async () => {
    const storage = memoryStorage();
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setSubtleForTests(null);
    notifs.notify('Plain note', 'warning');
    await notifs.persistNow();

    const raw = storage.getItem('kb.admin.notifications');
    expect(raw).toContain('"v":0');
    expect(notifs.isPlaintextFallback()).toBe(true);

    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setSubtleForTests(null);
    await notifs.loadNotifications();
    expect(notifs.getNotifications()).toHaveLength(1);
    expect(notifs.isPlaintextFallback()).toBe(true);
  });

  it('treats a corrupt store as empty with the failure flag', async () => {
    const storage = memoryStorage();
    storage.setItem('kb.admin.notifications', '{not json');
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyA);
    await notifs.loadNotifications();
    expect(notifs.getNotifications()).toHaveLength(0);
    expect(notifs.consumeDecryptFailure()).toBe(true);
  });

  it('never overwrites an unread v1 blob with plaintext when WebCrypto is missing', async () => {
    const storage = memoryStorage();
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyA);
    notifs.notify('Secure history', 'error');
    await notifs.persistNow();
    const before = storage.getItem('kb.admin.notifications');
    expect(before).toContain('"v":1');

    // Same profile, later visit over plain-HTTP LAN: no subtle, no key yet.
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setSubtleForTests(null);
    await notifs.loadNotifications();
    expect(notifs.hasUnreadEncrypted()).toBe(true);
    expect(notifs.getNotifications()).toHaveLength(0);

    notifs.notify('New session note', 'info');
    await notifs.persistNow();
    // The v1 ciphertext must be untouched — persist stayed memory-only.
    expect(storage.getItem('kb.admin.notifications')).toBe(before);
  });

  it('clears the plaintext fallback flag once the store re-encrypts to v1', async () => {
    const storage = memoryStorage();
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setSubtleForTests(null);
    notifs.notify('Plain note', 'warning');
    await notifs.persistNow();
    expect(notifs.isPlaintextFallback()).toBe(true);

    // Back on a secure context with the key: next persist re-encrypts.
    notifs.setSubtleForTests(undefined);
    notifs.setNotificationKey(keyA);
    await notifs.persistNow();
    expect(storage.getItem('kb.admin.notifications')).toContain('"v":1');
    expect(notifs.isPlaintextFallback()).toBe(false);
  });

  it('drops memory without touching storage on logout unload', async () => {
    vi.useFakeTimers();
    const storage = memoryStorage();
    notifs.resetNotificationsForTests();
    notifs.initNotificationStore({ storage });
    notifs.setNotificationKey(keyA);
    notifs.notify('Secret outcome', 'error');
    await notifs.persistNow();
    const before = storage.getItem('kb.admin.notifications');

    notifs.clearNotificationKey();
    notifs.unloadNotificationsForLogout();
    expect(notifs.getNotifications()).toHaveLength(0);
    expect(notifs.isPlaintextFallback()).toBe(false);
    expect(notifs.hasUnreadEncrypted()).toBe(false);
    // A pending debounce must not persist the cleared list over the blob.
    notifs.notify('Unsaved', 'info');
    notifs.unloadNotificationsForLogout();
    await vi.advanceTimersByTimeAsync(500);
    expect(storage.getItem('kb.admin.notifications')).toBe(before);
  });
});
