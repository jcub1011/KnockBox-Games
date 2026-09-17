// Admin portal client logic.
//
// Structure mirrors web/shell.js: pure helpers live in admin-core.js (tested in the Node environment),
// this module owns the DOM, fetch and timers, and nothing runs on import — bootstrap() is exported and
// called from index.html, so the test suite can drive the exported functions without a live poll to
// suppress. Views and tabs are pre-existing markup toggled by class, never rendered from templates; only
// table and list ROWS are built imperatively, always with textContent, because game titles and player
// display names are untrusted input.

import {
  ADMIN_FAVICON, AVAILABILITY, ALL_SETTINGS, BYTE_MULTIPLIERS, BYTE_UNITS, CODE_ALPHABET, LIMIT_FIELDS, SETTINGS_GROUPS, STARTUP_LIMITS, TABS,
  TOP_TABS, TAB_MAPPING,
  UPDATE_MODES, UPDATE_POLICIES, WEBHOOK_EVENTS, appendLogEntries, availabilityLabel, blockedShare,
  checkCodeEntry, checkWebhook, compareSemVer, cpuPercentBetween, downsample, filterCatalog, filterGames, filterLobbies,
  filterPlugins, filterSettings, formatByteLimit, formatBytes, formatClock, formatCount, formatDateTime, formatDuration, formatVersion,
  formatNotificationTime, formatNotificationTimeFull,
  getStoredSidebarCollapsed, hourOptionLabel, isBusyLifecycle, isTerminalJob, jobProgress,
  lifecycleLabel, logLevelClass, logLevelTag,   mergeJobs, mergePluginEntries, mergeSamples,
  noLimitOverrides, playerRange, pluginRestoreWarning, pluginRowBadges, pluginRowSize, pluginRowVersion,
  pluginStatusLabel, ratePerSecond,
  scheduleNote, seriesCpuPercent, seriesValue, setStoredSidebarCollapsed, settingFromHash,
  sortPlugins, sparklinePath, splitBytes, tabFromHash, topTabFromHash, uploadGuard, validateLimits, versionAction, versionOptionValue, versionOptions,
  visibleTagCount,
  webhookEventLabel, webhookLastDelivery,
} from './admin-core.js';

import {
  NOTIFICATION_DRAWER_MS,
  NOTIF_DRAWER_EXIT_MS,
  NOTIF_MODAL_EXIT_MS,
  clearNotificationKey,
  clearNotifications,
  consumeDecryptFailure,
  dismissOneNotification,
  getNotification,
  getNotifications,
  getUnreadCount,
  hasStoredBlob,
  hasUnreadEncrypted,
  initNotificationStore,
  isMemoryOnly,
  isPlaintextFallback,
  loadNotifications,
  markAllNotificationsRead,
  markNotificationRead,
  notify,
  refreshNotificationKey,
  subscribe as subscribeNotifications,
  unloadNotificationsForLogout,
} from './admin-notifications.js';

const el = (id) => document.getElementById(id);

// How often live views refresh.
const POLL_INTERVAL_MS = 5000;
const LOG_VIEW_LIMIT = 500;
const JOB_VIEW_LIMIT = 50;

// ── Module state ──────────────────────────────────────────────────────────────

let pollTimer = null;
let activeTopTab = 'monitoring';
let activeSettingId = 'setting-overview';
let activeTab = 'overview';
// The tab enterTab last ran for, so a scroll that ends where it started re-fetches nothing.
let enteredTab = null;
// Pending scroll-settle timer. Cancelled by an actual tab entry, and by stopScrollSettle().
let settleTimer = null;
let scrollObserver = null;


// Latest payloads, kept so a filter change re-renders without a round trip.
let lobbyData = null;
let gameData = null;
let logEntries = [];
let logCursor = 0;
let catalogData = null;
let jobs = [];
let jobCursor = 0;
// jobIds whose terminal outcome has already been notified, so each finished job raises exactly one —
// whenever the operator first sees it, however many polls later that is.
const reportedJobs = new Set();
let uploadXhr = null;
let uploadFile = null;
let limitsData = null;
let codesData = null;
let announcementData = null;
let webhookData = null;
// The blocklist being edited, which is not what is saved until the operator says so.
let codesDraft = { words: [], patterns: [] };

// ── Plugins & Games tab state ─────────────────────────────────────────────────
// The status tabs slice one merged list, and each remembers its own sort — switching tabs
// restores that tab's last order rather than resetting it.
const PLUGIN_TABS = ['installed', 'updates', 'available'];
// filterPlugins status each tab shows. Problems have no tab of their own: an incompatible
// installed game sits in Installed, an incompatible catalog-only entry in Available, both
// surfaced by badge + the `status` sort rather than by a separate view.
const PLUGIN_TAB_STATUS = { installed: 'installed', updates: 'updateAvailable', available: 'notInstalled' };
let activePluginTab = 'installed';
let pluginSort = { installed: 'name-az', updates: 'name-az', available: 'status' };
// Frozen-list discipline (Visual Studio style): a background poll never moves the rows an
// operator may be about to click. Polls update the caches + notifications and only raise the
// stale pill; the list re-renders on tab switch, sort/search/source change, manual refresh,
// or a user-initiated mutation's completion.
let pluginsDirty = false;
let pluginsRendered = false;
let pluginsLoading = false;
// Fetch generation: a tab switch or second refresh while a load is in flight makes the first
// reply stale, and rendering it would swap the list under the operator's new tab.
let pluginsFetchSeq = 0;

// Previous counter samples, for the rates admin-core derives. `{ value, at }` pairs — see ratePerSecond.
let cpuSample = null;
const gameFrameSamples = new Map();
// The server-side metric history, and the cursor into it. Held here (not re-fetched whole) because the feed
// is cursor-polled — the same shape as the log stream and the job feed.
let historySamples = [];
let historyCursor = 0;

// ── HTTP ──────────────────────────────────────────────────────────────────────

// A 401 on any call means the session went away (expired, or the password file changed, which revokes
// every session by design). Route it back through the auth check so the portal returns to the login view
// instead of silently showing frozen numbers. Centralised here so a new endpoint can't forget it.
async function request(path, init) {
  const res = await fetch(path, init);
  if (res.status === 401) {
    await handleUnauthorized();
    return null;
  }
  return res;
}

// Extracted so the upload path — which uses XMLHttpRequest and therefore cannot go through request() —
// funnels 401 the same way. Forgetting it would leave an operator whose session expired mid-upload
// staring at a modal that never finishes.
function handleUnauthorized() {
  return checkAuthStatus();
}

async function getJson(path) {
  try {
    const res = await request(path);
    if (!res) return null;
    if (!res.ok) {
      showErrorStatus(`Request failed (${res.status})`);
      return null;
    }
    clearStatus();
    return await res.json();
  } catch (err) {
    showErrorStatus('Network error');
    console.error(`GET ${path} failed:`, err);
    return null;
  }
}

/**
 * POSTs an action and reports the outcome as a notification. Returns true when the server accepted it.
 *
 * The JSON content type is always sent because the server's mutation guard requires it — a plain form
 * post is the one shape SameSite=Strict historically leaked on, so the API refuses anything else.
 *
 * `errorEl` redirects the failure message into an inline element instead of a notification. A form's rejection
 * belongs beside the fields that caused it and has to stay on screen while they are corrected, which a
 * transient notification cannot do.
 */
async function postJson(path, body, { errorEl = null } = {}) {
  const fail = (message) => {
    if (!errorEl) { notify(message, 'error'); return false; }
    errorEl.textContent = message;
    errorEl.classList.remove('hidden');
    return false;
  };

  try {
    const res = await request(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body ?? {}),
    });
    if (!res) return false;
    const data = await res.json().catch(() => null);
    if (!res.ok || !data?.success) {
      return fail(data?.error || `That didn't work (${res.status}).`);
    }
    if (errorEl) errorEl.classList.add('hidden');
    // Success with something worth saying: `detail` explains what the action did and did not do (chiefly
    // that disabling a game leaves its running lobbies alone), `warning` that a policy change is live but
    // wasn't written to disk.
    if (data.warning) notify(data.warning, 'warning');
    else notify(data.detail || 'Done.', 'success');
    return true;
  } catch (err) {
    console.error(`POST ${path} failed:`, err);
    return fail('Network error.');
  }
}

// ── Status pill ───────────────────────────────────────────────────────────────

// A FAULT indicator, not a heartbeat: it is absent unless something is wrong. There used to be an
// "Admin Port Active" state, and it could never be read while it was false — this page is served by the
// admin port, so either the pill said "active" or there was no page to read it on. What is actually
// worth surfacing is the degraded case: the portal is up but a request to it just failed.
function clearStatus() {
  el('server-status-pill').hidden = true;
}

function showErrorStatus(msg) {
  el('server-status-text').textContent = msg;
  el('server-status-pill').hidden = false;
}

// ── Notifications ─────────────────────────────────────────────────────────────
// The persistent replacement for the toast system: a bell with an unread badge in the header, an
// arrival drawer with the three newest, a full list modal, and a details modal for one
// notification. The STORE (list, encryption, persistence) lives in admin-notifications.js, which
// this module subscribes to; everything below is rendering over it. Read state changes via the
// explicit buttons, and by opening the details modal (which marks the shown item read); the drawer
// preview and opening the list never mark anything read.

let notifDrawerTimer = null;
let notifDrawerOpen = false;
let notifDetailId = null;

// Glyphs for the icon-only notification buttons, matching the header/tab icon treatment
// (stroke currentColor). The toggle shows the envelope for the state it will move the item to:
// open for "mark read", closed for "mark unread". Dismiss is an x like the modal-close icon.
const NOTIF_ICON_MAIL_OPEN = '<svg class="btn-icon-svg" viewBox="0 0 24 24" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
  + '<path d="M6 12.0001V10.0001H18V12.0001M3.02832 10.0002L10.2246 14.8168C10.8661 15.2444 11.1869 15.4583 '
  + '11.5336 15.5414C11.8399 15.6148 12.1593 15.6148 12.4657 15.5414C12.8124 15.4583 13.1332 15.2444 '
  + '13.7747 14.8168L20.9709 10.0001M10.2981 4.06892L4.49814 7.71139C3.95121 8.05487 3.67775 8.2266 '
  + '3.4794 8.45876C3.30385 8.66424 3.17176 8.90317 3.09111 9.16112C3 9.45256 3 9.77548 3 10.4213V16.8001C3 '
  + '17.9202 3 18.4803 3.21799 18.9081C3.40973 19.2844 3.71569 19.5904 4.09202 19.7821C4.51984 20.0001 '
  + '5.07989 20.0001 6.2 20.0001H17.8C18.9201 20.0001 19.4802 20.0001 19.908 19.7821C20.2843 19.5904 '
  + '20.5903 19.2844 20.782 18.9081C21 18.4803 21 17.9202 21 16.8001V10.4213C21 9.77548 21 9.45256 '
  + '20.9089 9.16112C20.8282 8.90317 20.6962 8.66424 20.5206 8.45876C20.3223 8.2266 20.0488 8.05487 '
  + '19.5019 7.71139L13.7019 4.06891C13.0846 3.68129 12.776 3.48747 12.4449 3.41192C12.152 3.34512 11.848 '
  + '3.34512 11.5551 3.41192C11.224 3.48747 10.9154 3.68129 10.2981 4.06892Z"/></svg>';
const NOTIF_ICON_MAIL_CLOSED = '<svg class="btn-icon-svg" viewBox="0 0 24 24" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
  + '<path d="M21 8L17.4392 9.97822C15.454 11.0811 14.4614 11.6326 13.4102 11.8488C12.4798 12.0401 11.5202 '
  + '12.0401 10.5898 11.8488C9.53864 11.6326 8.54603 11.0811 6.5608 9.97822L3 8M6.2 19H17.8C18.9201 19 '
  + '19.4802 19 19.908 18.782C20.2843 18.5903 20.5903 18.2843 20.782 17.908C21 17.4802 21 16.9201 21 '
  + '15.8V8.2C21 7.0799 21 6.51984 20.782 6.09202C20.5903 5.71569 20.2843 5.40973 19.908 5.21799C19.4802 5 '
  + '18.9201 5 17.8 5H6.2C5.0799 5 4.51984 5 4.09202 5.21799C3.71569 5.40973 3.40973 5.71569 3.21799 '
  + '6.09202C3 6.51984 3 7.07989 3 8.2V15.8C3 16.9201 3 17.4802 3.21799 17.908C3.40973 18.2843 3.71569 '
  + '18.5903 4.09202 18.782C4.51984 19 5.07989 19 6.2 19Z"/></svg>';
const NOTIF_ICON_X = '<svg class="btn-icon-svg" viewBox="0 0 24 24" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">'
  + '<line x1="18" y1="6" x2="6" y2="18"></line><line x1="6" y1="6" x2="18" y2="18"></line></svg>';

/**
 * Paints a mark read/unread toggle as the icon for the action it will take. Icon-only, so the
 * accessible name carries the meaning the text used to.
 */
function paintNotifToggle(btn, read) {
  btn.innerHTML = read ? NOTIF_ICON_MAIL_CLOSED : NOTIF_ICON_MAIL_OPEN;
  const label = read ? 'Mark unread' : 'Mark read';
  btn.setAttribute('aria-label', label);
  btn.title = label;
}

function notifKindLabel(kind) {
  const name = String(kind ?? 'info');
  return name.charAt(0).toUpperCase() + name.slice(1);
}

function refreshNotifBadge() {
  const badge = el('notif-badge');
  if (!badge) return;
  const count = getUnreadCount();
  badge.textContent = count > 99 ? '99+' : String(count);
  badge.classList.toggle('hidden', count === 0);
  el('notif-bell-btn')?.setAttribute(
    'aria-label', count === 0 ? 'Notifications' : `Notifications, ${count} unread`);
}

/**
 * Whether the arrival drawer is meaningful on this device. It is a hover-preview idiom: hover or
 * keyboard focus opens it, leaving dismisses it. On touch devices there is no hover — a tap fires
 * click (and sometimes emulated mouseenter first), so opening the drawer on tap races the modal the
 * tap actually asked for, and the two visibly fight before the modal wins. There the bell skips the
 * drawer entirely and opens the list, and arrivals only move the badge. Missing matchMedia (jsdom)
 * reads as capable, so the test suite exercises the drawer path.
 */
export function canHoverPreview() {
  try {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return true;
    return window.matchMedia('(hover: hover)').matches;
  } catch {
    return true;
  }
}

/**
 * Opens the arrival preview. Always re-renders the three newest, even if they are the same ones
 * the last opening showed — the drawer answers "what just happened", not "what is unread".
 * Suppressed while the list modal owns the operator's attention (its badge still updates),
 * before login (the bell is hidden there anyway), and on touch devices (see canHoverPreview).
 */
function openNotifDrawer() {
  if (!canHoverPreview()) return;
  if (el('dashboard-view')?.classList.contains('hidden')) return;
  if (!el('notifications-backdrop')?.classList.contains('hidden')) return;
  // An arrival while the details modal is open would pop the drawer underneath it (z-90 vs z-200):
  // invisible, but arming the auto-dismiss timer and flipping open state behind the modal.
  if (!el('notification-details-backdrop')?.classList.contains('hidden')) return;
  renderNotifDrawer();
  // A close in flight is cancelled: the exit timer is dropped and the closing class removed, so a
  // reopen never inherits the fade-out it just interrupted.
  notifDrawerExitTimer = clearNotifExitTimer(notifDrawerExitTimer);
  const drawer = el('notif-drawer');
  drawer?.classList.remove('notif-drawer-closing');
  drawer?.classList.remove('hidden');
  notifDrawerOpen = true;
  el('notif-bell-btn')?.setAttribute('aria-expanded', 'true');
  armNotifDrawerTimer();
}

/**
 * Closes the drawer through its exit animation rather than hiding it outright, so dismissal reads
 * the same as arrival. The `hidden` class lands when the animation ends (NOTIF_DRAWER_EXIT_MS,
 * mirroring admin.css) — callers must not assume it is synchronous.
 */
export function closeNotifDrawer() {
  stopNotifDrawerTimer();
  const drawer = el('notif-drawer');
  el('notif-bell-btn')?.setAttribute('aria-expanded', 'false');
  if (!drawer || !notifDrawerOpen) {
    notifDrawerOpen = false;
    return;
  }
  notifDrawerOpen = false;
  drawer.classList.add('notif-drawer-closing');
  notifDrawerExitTimer = clearNotifExitTimer(notifDrawerExitTimer);
  notifDrawerExitTimer = setTimeout(() => {
    notifDrawerExitTimer = null;
    drawer.classList.add('hidden');
    drawer.classList.remove('notif-drawer-closing');
  }, NOTIF_DRAWER_EXIT_MS);
}

/**
 * Cancels the drawer's pending auto-dismiss. Exported for the jsdom tests, which reuse one window
 * per file: a drawer armed by one test would otherwise fire into the next test's DOM — the same
 * trap stopPolling() and stopScrollSettle() exist for.
 */
export function stopNotifDrawerTimer() {
  if (notifDrawerTimer !== null) clearTimeout(notifDrawerTimer);
  notifDrawerTimer = null;
}

let notifDrawerExitTimer = null;
let notifListExitTimer = null;
let notifDetailExitTimer = null;

function clearNotifExitTimer(timer) {
  if (timer !== null) clearTimeout(timer);
  return null;
}

/**
 * Cancels every in-flight exit animation. Exported alongside stopNotifDrawerTimer for the jsdom
 * tests: an exit armed by one test must not hide the next test's freshly opened drawer or modal.
 */
export function stopNotifExitTimers() {
  notifDrawerExitTimer = clearNotifExitTimer(notifDrawerExitTimer);
  notifListExitTimer = clearNotifExitTimer(notifListExitTimer);
  notifDetailExitTimer = clearNotifExitTimer(notifDetailExitTimer);
}

function armNotifDrawerTimer() {
  stopNotifDrawerTimer();
  notifDrawerTimer = setTimeout(closeNotifDrawer, NOTIFICATION_DRAWER_MS);
}

function drawerItem(n) {
  const item = document.createElement('div');
  item.className = `notif-item notif-${n.kind}${n.read ? '' : ' notif-item-unread'}`;
  item.tabIndex = 0;
  item.setAttribute('role', 'button');

  const head = document.createElement('div');
  head.className = 'notif-item-head';
  const kind = document.createElement('span');
  kind.className = 'notif-kind';
  kind.textContent = notifKindLabel(n.kind);
  const time = document.createElement('span');
  time.className = 'notif-time';
  time.textContent = formatNotificationTime(n.at);
  head.append(kind, time);

  const message = document.createElement('div');
  message.className = 'notif-message';
  message.textContent = n.message;
  item.append(head, message);

  const open = () => openNotificationDetails(n.id);
  item.addEventListener('click', open);
  item.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
  });
  return item;
}

function renderNotifDrawer() {
  const host = el('notif-drawer-items');
  if (!host) return;
  host.innerHTML = '';
  const latest = getNotifications().slice(0, 3);
  if (latest.length === 0) {
    const none = document.createElement('div');
    none.className = 'notif-item';
    none.textContent = 'No notifications.';
    host.appendChild(none);
    return;
  }
  for (const n of latest) host.appendChild(drawerItem(n));
}

export function openNotifications() {
  closeNotifDrawer();
  const bd = el('notifications-backdrop');
  notifListExitTimer = clearNotifExitTimer(notifListExitTimer);
  bd?.classList.remove('modal-closing');
  bd?.classList.remove('hidden');
  renderNotifications();
  el('notifications-close')?.focus();
}

/**
 * Animated like the drawer close: the backdrop takes `modal-closing` (NOTIF_MODAL_EXIT_MS, mirroring
 * admin.css) and `hidden` lands when it ends. Guarded against double-arming, and reopening cancels.
 */
export function closeNotifications() {
  const bd = el('notifications-backdrop');
  if (!bd || bd.classList.contains('hidden') || bd.classList.contains('modal-closing')) return;
  bd.classList.add('modal-closing');
  notifListExitTimer = clearNotifExitTimer(notifListExitTimer);
  notifListExitTimer = setTimeout(() => {
    notifListExitTimer = null;
    bd.classList.add('hidden');
    bd.classList.remove('modal-closing');
  }, NOTIF_MODAL_EXIT_MS);
}

function notificationRow(n) {
  const row = document.createElement('div');
  row.className = `notif-row notif-${n.kind}${n.read ? '' : ' notif-item-unread'}`;
  row.tabIndex = 0;
  row.setAttribute('role', 'button');

  const main = document.createElement('div');
  main.className = 'notif-row-main';
  const head = document.createElement('div');
  head.className = 'notif-item-head';
  const kind = document.createElement('span');
  kind.className = 'notif-kind';
  kind.textContent = notifKindLabel(n.kind);
  const time = document.createElement('span');
  time.className = 'notif-time';
  time.textContent = formatNotificationTime(n.at);
  head.append(kind, time);
  const message = document.createElement('div');
  message.className = 'notif-message';
  message.textContent = n.message;
  main.append(head, message);

  const actions = document.createElement('div');
  actions.className = 'notif-row-actions';
  // Disabled alongside the toolbar buttons while an unreadable encrypted blob is stored (see
  // renderNotifications): a per-row delete here could not delete what is actually stored.
  const rowLocked = hasUnreadEncrypted();
  const toggle = document.createElement('button');
  toggle.className = 'btn btn-secondary btn-small btn-icon-only';
  toggle.type = 'button';
  toggle.disabled = rowLocked;
  paintNotifToggle(toggle, n.read);
  toggle.addEventListener('click', (e) => {
    e.stopPropagation();
    markNotificationRead(n.id, !n.read);
  });
  const dismiss = document.createElement('button');
  dismiss.className = 'btn btn-danger btn-small btn-icon-only';
  dismiss.type = 'button';
  dismiss.innerHTML = NOTIF_ICON_X;
  dismiss.setAttribute('aria-label', 'Dismiss notification');
  dismiss.title = 'Dismiss notification';
  dismiss.disabled = rowLocked;
  dismiss.addEventListener('click', (e) => {
    e.stopPropagation();
    dismissOneNotification(n.id);
  });
  actions.append(toggle, dismiss);
  row.append(main, actions);

  const open = () => openNotificationDetails(n.id);
  row.addEventListener('click', (e) => {
    if (e.target.closest('button')) return;
    open();
  });
  row.addEventListener('keydown', (e) => {
    if (e.target.closest('button')) return;
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); open(); }
  });
  return row;
}

function renderNotifications() {
  const list = getNotifications();
  const unread = list.filter((n) => n.read !== true).length;
  el('notifications-unread').textContent = `${unread} unread`;

  const note = el('notifications-note');
  const notes = [];
  // While an encrypted blob is stored but unreadable on this origin, mutating controls stay disabled:
  // any delete or mark-read would only touch memory while the stored blob survives, so the action
  // could not do what its label promises.
  const locked = hasUnreadEncrypted();
  if (locked) {
    notes.push('Encrypted notifications are stored on this browser but cannot be read on this connection '
      + '(plain HTTP over LAN has no WebCrypto). They were left untouched — managing is disabled until '
      + 'you revisit over loopback or HTTPS.');
  } else if (isPlaintextFallback()) {
    notes.push('Stored unencrypted on this connection: this browser cannot do WebCrypto here '
      + '(plain HTTP over LAN), so anyone reading this browser profile can read these.');
  } else if (isMemoryOnly()) {
    notes.push('The encryption key is unavailable, so these live in memory for this session only '
      + 'and will not survive a reload.');
  }
  note.textContent = notes.join(' ');
  note.classList.toggle('hidden', notes.length === 0);

  const host = el('notifications-list');
  host.innerHTML = '';
  for (const n of list) host.appendChild(notificationRow(n));
  host.classList.toggle('hidden', list.length === 0);
  el('notifications-empty').classList.toggle('hidden', list.length > 0);
  el('notifications-mark-all').disabled = unread === 0 || locked;
  el('notifications-dismiss-all').disabled = list.length === 0 || locked;
}

async function dismissAllNotificationsUI() {
  if (getNotifications().length === 0) return;
  // Buttons are disabled while an unreadable encrypted blob is stored; this is the keyboard/forced-click net.
  if (hasUnreadEncrypted()) return;
  if (!await confirmAction(
    'Dismiss every notification? This deletes them permanently.', 'Dismiss All')) return;
  clearNotifications();
}

export function openNotificationDetails(id) {
  notifDetailId = id;
  // Opening the dedicated modal counts as reading: mark unread items read so
  // the badge/list reflect what the operator has now seen. The store emit
  // re-renders badge + list; the explicit render below shows the Read status.
  const current = getNotification(id);
  if (current && !current.read) markNotificationRead(id, true);
  if (!renderNotificationDetails()) return;
  closeNotifDrawer();
  const bd = el('notification-details-backdrop');
  notifDetailExitTimer = clearNotifExitTimer(notifDetailExitTimer);
  bd?.classList.remove('modal-closing');
  bd?.classList.remove('hidden');
}

export function closeNotificationDetails() {
  const bd = el('notification-details-backdrop');
  if (!bd || bd.classList.contains('hidden') || bd.classList.contains('modal-closing')) {
    if (!bd || bd.classList.contains('hidden')) notifDetailId = null;
    return;
  }
  notifDetailId = null;
  bd.classList.add('modal-closing');
  notifDetailExitTimer = clearNotifExitTimer(notifDetailExitTimer);
  notifDetailExitTimer = setTimeout(() => {
    notifDetailExitTimer = null;
    bd.classList.add('hidden');
    bd.classList.remove('modal-closing');
  }, NOTIF_MODAL_EXIT_MS);
}

function renderNotificationDetails() {
  const n = getNotification(notifDetailId);
  if (!n) {
    // Dismissed from the list (or the detail modal itself) while open: close and re-sync the list.
    closeNotificationDetails();
    if (!el('notifications-backdrop')?.classList.contains('hidden')) renderNotifications();
    return false;
  }
  const body = el('notification-details-body');
  body.innerHTML = '';

  const message = document.createElement('div');
  message.className = 'notif-details-message';
  message.textContent = n.message;

  const grid = document.createElement('div');
  grid.className = 'details-grid';
  for (const [label, value] of [
    ['Severity', notifKindLabel(n.kind)],
    ['Status', n.read ? 'Read' : 'Unread'],
    ['Received', formatNotificationTimeFull(n.at)],
    ['Recorded', n.at],
  ]) {
    const field = document.createElement('div');
    field.className = 'details-field';
    const lab = document.createElement('div');
    lab.className = 'details-label';
    lab.textContent = label;
    const val = document.createElement('div');
    val.className = 'details-value';
    val.textContent = value;
    field.append(lab, val);
    grid.appendChild(field);
  }
  body.append(message, grid);
  paintNotifToggle(el('notification-details-toggle'), n.read);
  // Same lock as the list rows: while an unreadable encrypted blob is stored, toggling or dismissing
  // from the details modal could not touch what is actually stored.
  const detailsLocked = hasUnreadEncrypted();
  el('notification-details-toggle').disabled = detailsLocked;
  el('notification-details-dismiss').disabled = detailsLocked;
  return true;
}

/**
 * After a successful login: fetch the store's encryption key, then read the store. A 401 from the
 * key endpoint means the session went away mid-check, so it funnels back through the auth check
 * rather than leaving the portal on a dashboard it is no longer entitled to.
 */
async function initNotificationsAfterAuth() {
  const { key, unauthorized } = await refreshNotificationKey();
  if (unauthorized) {
    await checkAuthStatus();
    return;
  }
  await loadNotifications();
  if (consumeDecryptFailure()) {
    notify('Stored notifications could not be decrypted, so they were cleared. '
      + 'This happens when the admin password changes.', 'warning');
  } else if (!key && (hasUnreadEncrypted() || isMemoryOnly() || hasStoredBlob())) {
    // The key endpoint failed for a non-auth reason (network/500): the portal must not look simply
    // empty — stored history is unreachable and anything new lives in memory for this session only.
    notify('Could not fetch the notification encryption key, so stored notifications are unavailable '
      + 'and new ones live in memory for this session only.', 'warning');
  }
  refreshNotifBadge();
}

// ── Modals ────────────────────────────────────────────────────────────────────

let confirmResolve = null;

/** Resolves true when the operator confirms. Every destructive action goes through this. */
function confirmAction(body, okLabel = 'Confirm', { warning = null, onExport = null, exportLabel = 'Export' } = {}) {
  el('confirm-body').textContent = body;
  el('confirm-ok').textContent = okLabel;
  const warningEl = el('confirm-warning');
  if (warningEl) {
    if (warning) {
      warningEl.textContent = warning;
      warningEl.classList.remove('hidden');
    } else {
      warningEl.textContent = '';
      warningEl.classList.add('hidden');
    }
  }
  const exportBtn = el('confirm-export');
  if (exportBtn) {
    if (onExport) {
      exportBtn.textContent = exportLabel;
      exportBtn.classList.remove('hidden');
      exportBtn.onclick = () => onExport();
    } else {
      exportBtn.classList.add('hidden');
      exportBtn.onclick = null;
    }
  }
  el('confirm-backdrop').classList.remove('hidden');
  el('confirm-ok').focus();
  return new Promise((resolve) => { confirmResolve = resolve; });
}

function settleConfirm(result) {
  el('confirm-backdrop').classList.add('hidden');
  const warningEl = el('confirm-warning');
  if (warningEl) {
    warningEl.textContent = '';
    warningEl.classList.add('hidden');
  }
  const exportBtn = el('confirm-export');
  if (exportBtn) {
    exportBtn.classList.add('hidden');
    exportBtn.onclick = null;
  }
  const resolve = confirmResolve;
  confirmResolve = null;
  if (resolve) resolve(result);
}

export function exportGame(id) {
  const link = document.createElement('a');
  link.href = `/admin/api/games/${encodeURIComponent(id)}/export`;
  link.download = '';
  document.body.appendChild(link);
  link.click();
  link.remove();
}

// ── Auth ──────────────────────────────────────────────────────────────────────

export async function checkAuthStatus() {
  try {
    const res = await fetch('/admin/api/auth/status');
    if (!res.ok) {
      showErrorStatus('Server unreachable on admin port');
      el('admin-top-tabs')?.classList.add('hidden');
      el('logout-btn')?.classList.add('hidden');
      el('notif-bell-wrap')?.classList.add('hidden');
      return;
    }
    const data = await res.json();
    clearStatus();

    if (!data.configured) {
      showView('setup-view');
      el('admin-top-tabs')?.classList.add('hidden');
      el('logout-btn').classList.add('hidden');
      el('notif-bell-wrap')?.classList.add('hidden');
      stopPolling();
    } else if (!data.authenticated) {
      showView('login-view');
      el('admin-top-tabs')?.classList.add('hidden');
      el('logout-btn').classList.add('hidden');
      el('notif-bell-wrap')?.classList.add('hidden');
      stopPolling();
    } else {
      showView('dashboard-view');
      el('admin-top-tabs')?.classList.remove('hidden');
      el('logout-btn').classList.remove('hidden');
      el('notif-bell-wrap')?.classList.remove('hidden');
      selectSetting(settingFromHash(location.hash), { replaceHash: false, scroll: Boolean(location.hash) });
      await initNotificationsAfterAuth();
    }
  } catch (err) {
    showErrorStatus('Network Error');
    el('admin-top-tabs')?.classList.add('hidden');
    el('logout-btn')?.classList.add('hidden');
    el('notif-bell-wrap')?.classList.add('hidden');
    console.error('Failed to check auth status:', err);
  }
}

// A lookup rather than the hard-coded array this replaced: adding a view was previously an edit to a
// literal inside this function, which is exactly the kind of edit that gets missed.
const VIEWS = ['setup-view', 'login-view', 'dashboard-view'];

function showView(id) {
  for (const view of VIEWS) el(view).classList.toggle('hidden', view !== id);
}

// ── Sidebar collapse state ────────────────────────────────────────────────────

/**
 * Sets the sidebar collapse state, updating DOM classes and ARIA attributes.
 * Persists to localStorage by default.
 */
export function setSidebarCollapsed(collapsed, { persist = true } = {}) {
  const isCollapsed = Boolean(collapsed);
  const dashboardView = el('dashboard-view');
  if (dashboardView) {
    dashboardView.classList.toggle('sidebar-collapsed', isCollapsed);
  }
  const toggleBtn = el('sidebar-toggle');
  if (toggleBtn) {
    toggleBtn.setAttribute('aria-expanded', String(!isCollapsed));
    toggleBtn.setAttribute('aria-label', isCollapsed ? 'Expand sidebar' : 'Collapse sidebar');
    toggleBtn.setAttribute('title', isCollapsed ? 'Expand sidebar' : 'Collapse sidebar');
    const label = toggleBtn.querySelector('.sidebar-toggle-label');
    if (label) {
      label.textContent = isCollapsed ? 'Expand' : 'Collapse';
    }
  }
  updateSidebarScrollIndicators();
  if (persist) {
    setStoredSidebarCollapsed(isCollapsed);
  }
}

export function toggleSidebarCollapsed() {
  const dashboardView = el('dashboard-view');
  const isCollapsed = dashboardView ? dashboardView.classList.contains('sidebar-collapsed') : false;
  setSidebarCollapsed(!isCollapsed);
}

export function setMobileSidebarOpen(open) {
  const sidebarNav = el('sidebar-nav');
  const toggleBtn = el('sidebar-toggle');
  if (!sidebarNav) return;
  const isOpen = Boolean(open);
  sidebarNav.classList.toggle('mobile-open', isOpen);
  if (toggleBtn && typeof window !== 'undefined' && window.innerWidth <= 860) {
    toggleBtn.setAttribute('aria-expanded', String(isOpen));
    toggleBtn.setAttribute('aria-label', isOpen ? 'Close settings navigation' : 'Open settings navigation');
    toggleBtn.setAttribute('title', isOpen ? 'Close settings navigation' : 'Open settings navigation');
  }
}

export function toggleMobileSidebar() {
  const sidebarNav = el('sidebar-nav');
  const isOpen = sidebarNav ? sidebarNav.classList.contains('mobile-open') : false;
  setMobileSidebarOpen(!isOpen);
}

export function closeMobileSidebar() {
  setMobileSidebarOpen(false);
}

export function updateSidebarScrollIndicators() {
  const dashboardView = el('dashboard-view');
  const isCollapsed = dashboardView?.classList.contains('sidebar-collapsed');
  const upBtn = el('sidebar-scroll-up');
  const downBtn = el('sidebar-scroll-down');
  const tree = el('sidebar-tree');
  if (!tree || !upBtn || !downBtn) return;

  if (!isCollapsed) {
    upBtn.disabled = true;
    downBtn.disabled = true;
    return;
  }

  const canScrollUp = tree.scrollTop > 4;
  const canScrollDown = tree.scrollTop + tree.clientHeight < tree.scrollHeight - 4;

  upBtn.disabled = !canScrollUp;
  downBtn.disabled = !canScrollDown;
}

export function centerActiveSidebarItem(settingId = activeSettingId) {
  const tree = el('sidebar-tree');
  if (!tree) return;
  const targetId = settingId ?? activeSettingId;
  if (!targetId) return;

  const activeItem = tree.querySelector(`.tree-item[data-setting-id="${targetId}"]`);
  if (!activeItem) return;

  const itemTop = activeItem.offsetTop;
  const itemHeight = activeItem.offsetHeight || 36;
  const treeHeight = tree.clientHeight;
  const targetScrollTop = Math.max(0, itemTop - (treeHeight / 2) + (itemHeight / 2));

  if (typeof tree.scrollTo === 'function') {
    tree.scrollTo({ top: targetScrollTop, behavior: 'smooth' });
  } else {
    tree.scrollTop = targetScrollTop;
  }

  updateSidebarScrollIndicators();
}

// ── Top-Bar Tabs & Settings Routing ──────────────────────────────────────────

export function selectTopTab(topTabKey, { replaceHash = true, scroll = false } = {}) {
  const topTab = topTabFromHash(topTabKey);
  activeTopTab = topTab;

  // Highlight active top-bar tab button
  for (const btn of document.querySelectorAll('.top-tab-btn')) {
    btn.classList.toggle('active', btn.dataset.tab === activeTopTab);
  }

  // Toggle tab panel visibility
  const panels = [
    { id: 'tab-panel-monitoring', tab: 'monitoring' },
    { id: 'tab-panel-logs', tab: 'logs' },
    { id: 'tab-panel-plugins', tab: 'plugins' },
    { id: 'tab-panel-settings', tab: 'settings' },
  ];
  for (const p of panels) {
    const panelEl = el(p.id);
    if (panelEl) panelEl.classList.toggle('hidden', p.tab !== activeTopTab);
  }

  // Update activeTab & activeSettingId
  if (topTab === 'monitoring') {
    activeTab = 'overview';
    activeSettingId = 'setting-overview';
  } else if (topTab === 'logs') {
    activeTab = 'logs';
    activeSettingId = 'setting-logs';
  } else if (topTab === 'plugins') {
    activeTab = 'marketplace';
    activeSettingId = 'setting-games';
  } else if (topTab === 'settings') {
    activeTab = 'platform';
    activeSettingId = 'setting-maintenance';
  }

  // Highlight active tree item & nav items
  for (const item of document.querySelectorAll('.tree-item')) {
    item.classList.toggle('active', item.dataset.settingId === activeSettingId);
  }
  for (const nav of document.querySelectorAll('.nav-item:not(.tree-item)')) {
    nav.classList.toggle('active', nav.dataset.tab === activeTab);
  }

  const setting = ALL_SETTINGS.find((s) => s.id === activeSettingId);
  const panelTitle = el('panel-title');
  if (panelTitle && setting) panelTitle.textContent = setting.label;

  if (replaceHash) {
    if (location.hash !== `#${topTab}`) {
      history.replaceState(null, '', `#${topTab}`);
    }
  }

  // A deep link names the tab, not a place inside it, so it lands at the top. The sidebar path scrolls
  // to its target setting instead; without this, following #plugins landed wherever that panel happened
  // to be left scrolled the last time it was open.
  if (scroll) {
    el(`tab-panel-${topTab}`)?.querySelector('.tab-scroll-container')?.scrollTo?.({ top: 0 });
  }

  enterTab(activeTab, { force: true });
}

let pendingScrollSettingId = null;

/**
 * Robust scroll-into-view helper that accounts for tab unhiding / reflow
 * and async content loading (such as dynamically loaded platform limits).
 */
export function scrollSettingIntoView(settingId, { behavior = 'smooth', block = 'start' } = {}) {
  const targetEl = typeof settingId === 'string' ? el(settingId) : settingId;
  if (!targetEl) return;
  const id = targetEl.id || (typeof settingId === 'string' ? settingId : null);
  pendingScrollSettingId = id;

  const performScroll = () => {
    if (typeof targetEl.scrollIntoView === 'function') {
      targetEl.scrollIntoView({ behavior, block });
    }
  };

  performScroll();

  if (typeof requestAnimationFrame === 'function') {
    requestAnimationFrame(() => {
      performScroll();
    });
  }

  // Backup passes after async DOM rendering settles
  setTimeout(() => {
    if (pendingScrollSettingId === id) {
      performScroll();
    }
  }, 100);

  setTimeout(() => {
    if (pendingScrollSettingId === id) {
      performScroll();
      pendingScrollSettingId = null;
    }
  }, 350);
}

export function selectSetting(settingKey, { replaceHash = true, scroll = false } = {}) {
  const settingId = settingFromHash(settingKey);
  const setting = ALL_SETTINGS.find((s) => s.id === settingId) ?? ALL_SETTINGS[0];
  activeSettingId = setting.id;
  activeTab = setting.legacyTab || 'overview';

  const targetTopTab = setting.topTab || (TAB_MAPPING[setting.legacyTab] || 'monitoring');
  activeTopTab = targetTopTab;

  // Highlight active top-bar tab button
  for (const btn of document.querySelectorAll('.top-tab-btn')) {
    btn.classList.toggle('active', btn.dataset.tab === activeTopTab);
  }

  // Toggle tab panel visibility
  const panels = [
    { id: 'tab-panel-monitoring', tab: 'monitoring' },
    { id: 'tab-panel-logs', tab: 'logs' },
    { id: 'tab-panel-plugins', tab: 'plugins' },
    { id: 'tab-panel-settings', tab: 'settings' },
  ];
  for (const p of panels) {
    const panelEl = el(p.id);
    if (panelEl) panelEl.classList.toggle('hidden', p.tab !== activeTopTab);
  }

  const panelTitle = el('panel-title');
  if (panelTitle) panelTitle.textContent = setting.label;

  const mobileLabel = el('sidebar-mobile-label');
  if (mobileLabel) mobileLabel.textContent = setting.label;

  // Highlight active tree item & nav items
  for (const item of document.querySelectorAll('.tree-item')) {
    item.classList.toggle('active', item.dataset.settingId === activeSettingId);
  }
  for (const nav of document.querySelectorAll('.nav-item:not(.tree-item)')) {
    nav.classList.toggle('active', nav.dataset.tab === activeTab);
  }

  // Ensure parent group in tree view is expanded
  const activeTreeItem = document.querySelector(`.tree-item[data-setting-id="${activeSettingId}"]`);
  const parentGroup = activeTreeItem?.closest('.tree-group');
  if (parentGroup) setGroupExpanded(parentGroup, true);

  // Keep the active setting centered in sidebar
  centerActiveSidebarItem(activeSettingId);

  // Smooth scroll to the target setting if requested
  if (scroll) {
    scrollSettingIntoView(activeSettingId);
  }

  if (replaceHash) {
    const hash = activeSettingId.replace(/^setting-/, '');
    if (location.hash !== `#${hash}`) {
      history.replaceState(null, '', `#${hash}`);
    }
  }

  // The operator picked this, so it happens now and unconditionally
  enterTab(activeTab, { force: true });
}

export function selectTab(tab, { replaceHash = true } = {}) {
  const clean = String(tab || '').replace(/^#/, '').trim().toLowerCase();
  if (TOP_TABS.includes(clean)) {
    selectTopTab(clean, { replaceHash, scroll: true });
    return;
  }
  const settingId = settingFromHash(tab);
  selectSetting(settingId, { replaceHash, scroll: true });
}

export function navigateToSetting(settingId) {
  selectSetting(settingId, { replaceHash: true, scroll: true });
}

// ── Tree View Expand/Collapse ─────────────────────────────────────────────────

export function setGroupExpanded(groupEl, expanded) {
  if (!groupEl) return;
  const isExp = Boolean(expanded);
  const header = groupEl.querySelector('.tree-group-header');
  if (header) header.setAttribute('aria-expanded', String(isExp));
  groupEl.classList.toggle('group-collapsed', !isExp);
}

export function toggleGroup(groupId) {
  const group = document.querySelector(`.tree-group[data-group-id="${groupId}"]`);
  if (!group) return;
  const header = group.querySelector('.tree-group-header');
  const isExpanded = header?.getAttribute('aria-expanded') !== 'false';
  setGroupExpanded(group, !isExpanded);
}

// ── Settings Search (Visual Studio Style) ─────────────────────────────────────

export function applySettingsSearch(query = '') {
  const filter = filterSettings(query, SETTINGS_GROUPS);
  const clearBtn = el('settings-search-clear');
  if (clearBtn) clearBtn.classList.toggle('hidden', !filter.isFiltering);

  // Filter right panel setting cards
  const settingCards = document.querySelectorAll('.setting-card');
  for (const card of settingCards) {
    const id = card.dataset.settingId;
    const visible = !filter.isFiltering || filter.matchingSettingIds.has(id);
    card.classList.toggle('search-hidden', !visible);
  }

  // Filter right panel group sections
  const groupSections = document.querySelectorAll('.settings-group-section');
  for (const section of groupSections) {
    const groupId = section.dataset.groupId;
    const visible = !filter.isFiltering || filter.matchingGroupIds.has(groupId);
    section.classList.toggle('search-hidden', !visible);
  }

  // Filter sidebar tree items
  const treeItems = document.querySelectorAll('.tree-item');
  for (const item of treeItems) {
    const id = item.dataset.settingId;
    const visible = !filter.isFiltering || filter.matchingSettingIds.has(id);
    item.classList.toggle('search-hidden', !visible);
  }

  // Filter sidebar tree groups
  const treeGroups = document.querySelectorAll('.tree-group');
  for (const group of treeGroups) {
    const groupId = group.dataset.groupId;
    const visible = !filter.isFiltering || filter.matchingGroupIds.has(groupId);
    group.classList.toggle('search-hidden', !visible);
    if (filter.isFiltering && visible) {
      setGroupExpanded(group, true);
    }
  }

  // Empty search state
  const emptyBanner = el('settings-search-empty');
  if (emptyBanner) {
    emptyBanner.classList.toggle('hidden', filter.totalMatches > 0);
    const emptyMsg = el('search-empty-msg');
    if (emptyMsg) {
      emptyMsg.textContent = `No settings match "${filter.query}".`;
    }
  }
}

export function clearSettingsSearch() {
  const searchInput = el('settings-search-input');
  if (searchInput) {
    searchInput.value = '';
    applySettingsSearch('');
    searchInput.focus();
  }
}

// ── Scrollspy ─────────────────────────────────────────────────────────────────

export function updateScrollspy() {
  const cards = [...document.querySelectorAll('.setting-card:not(.search-hidden):not(.hidden)')];
  if (!cards.length) return;

  const scrollContainer = el('admin-content-scroll') || document.querySelector('.admin-container') || document.documentElement;
  const containerRect = scrollContainer === document.documentElement
    ? { top: 0 }
    : scrollContainer.getBoundingClientRect();

  const isScrollable = (scrollContainer.scrollHeight - scrollContainer.clientHeight) > 50;
  const atBottom = isScrollable && ((scrollContainer.clientHeight + scrollContainer.scrollTop) >= (scrollContainer.scrollHeight - 50));
  let activeCard = atBottom ? cards[cards.length - 1] : cards[0];

  if (!atBottom) {
    const activationLine = containerRect.top + 150;
    for (let i = cards.length - 1; i >= 0; i--) {
      const rect = cards[i].getBoundingClientRect();
      if (rect.top <= activationLine) {
        activeCard = cards[i];
        break;
      }
    }
  }

  if (activeCard && activeCard.dataset.settingId) {
    const settingId = activeCard.dataset.settingId;
    if (settingId !== activeSettingId) {
      const prevTab = activeTab;
      activeSettingId = settingId;
      const setting = ALL_SETTINGS.find((s) => s.id === settingId);
      if (setting) {
        activeTab = setting.legacyTab || 'overview';
        el('panel-title').textContent = setting.label;
        const mobileLabel = el('sidebar-mobile-label');
        if (mobileLabel) mobileLabel.textContent = setting.label;
        for (const item of document.querySelectorAll('.tree-item')) {
          item.classList.toggle('active', item.dataset.settingId === activeSettingId);
        }
        for (const nav of document.querySelectorAll('.nav-item:not(.tree-item)')) {
          nav.classList.toggle('active', nav.dataset.tab === activeTab);
        }
        const activeTreeItem = document.querySelector(`.tree-item[data-setting-id="${activeSettingId}"]`);
        const parentGroup = activeTreeItem?.closest('.tree-group');
        if (parentGroup && parentGroup.classList.contains('group-collapsed')) {
          setGroupExpanded(parentGroup, true);
        }
        centerActiveSidebarItem(activeSettingId);

        const hash = activeSettingId.replace(/^setting-/, '');
        if (location.hash !== `#${hash}`) {
          history.replaceState(null, '', `#${hash}`);
        }

        if (activeTab !== prevTab) enterTabWhenSettled(activeTab);
      }
    }
  }
}

/**
 * Arriving on a tab: reset the cursor feeds it streams, take its one-off read, and arm its poll.
 *
 * One definition, because the two ways of arriving used to carry a copy each — and the copies did the
 * same expensive things for very different reasons. Clicking a setting is a decision; scrolling past one
 * is not, and `updateScrollspy` fires on every scroll event from three sources, so a single sidebar
 * click (which smooth-scrolls through everything in between) ran this for each tab on the way. That
 * meant repeatedly emptying the log buffer and calling `refreshCatalog`, which reaches the network with
 * a 30-second timeout and is the one read documented as never being on the poll path.
 */
function enterTab(tab, { force = false } = {}) {
  // A click supersedes whatever the scroll it caused was about to conclude.
  if (settleTimer) { clearTimeout(settleTimer); settleTimer = null; }
  if (!force && tab === enteredTab) return;
  enteredTab = tab;
  if (tab === 'logs') { logCursor = 0; logEntries = []; }
  if (tab === 'marketplace' || tab === 'plugins' || tab === 'games') {
    jobCursor = 0;
    jobs = [];
    // The one read that is NOT on the poll path — it reaches the network with a 30-second timeout —
    // so arriving is one of the few moments it happens. enterPluginsTab fetches every feed the cards
    // read (games, catalog, jobs) and renders once through the skeleton path; the shared
    // refreshActiveTab below is skipped so entry doesn't fetch the jobs feed twice. Poll ticks from
    // here on only refresh the caches and raise the stale pill — never re-render.
    enterPluginsTab();
    startPolling();
    return;
  }
  refreshActiveTab();
  startPolling();
}

/** How long the scroll must settle before a tab it passed through counts as one you arrived on. */
const TAB_SETTLE_MS = 250;

/**
 * The scroll path's version: nothing happens until the scrolling stops. Polling is stopped up front
 * rather than left running, because between here and the settle the timer belongs to a tab that is no
 * longer on screen.
 */
function enterTabWhenSettled(tab) {
  stopPolling();
  if (settleTimer) clearTimeout(settleTimer);
  settleTimer = setTimeout(() => {
    settleTimer = null;
    // The tab may have moved on again while this was pending; only the one still on screen wins.
    if (activeTab === tab) enterTab(tab);
  }, TAB_SETTLE_MS);
}

const POLL_MS = {
  overview: 5000,
  lobbies: 5000,
  monitoring: 5000,
  games: 3000,
  marketplace: 3000,
  plugins: 3000,
  logs: 2000,
  platform: 0,
  settings: 0,
};

function startPolling() {
  stopPolling();
  const interval = POLL_MS[activeTab] ?? 5000;
  if (interval > 0) pollTimer = setInterval(refreshActiveTab, interval);
}

export function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
}

/**
 * Cancels a pending scroll settle. Exported for the jsdom tests, which reuse one window per file: a
 * settle armed by the previous test would otherwise fire against the next one's fetch stub — the same
 * trap stopPolling() exists for, one tick further out.
 */
export function stopScrollSettle() {
  if (settleTimer) clearTimeout(settleTimer);
  settleTimer = null;
}

/**
 * The poll tick. Keyed on what the visible PANEL shows, not on which setting inside it is active —
 * the four top tabs each render several of the old tabs at once, so refreshing one of them leaves the
 * rest of the same screen frozen. Monitoring showed live counters above a lobby table fetched once on
 * entry (an operator watching for a stuck lobby saw a list that never moved), and scrolling to Active
 * Lobbies froze the counters and graphs instead.
 *
 * refreshCatalog stays off this path: it reaches the network with a 30-second timeout, and refreshJobs
 * already re-reads it the moment a job goes terminal. See POLL_MS.
 */
async function refreshActiveTab() {
  switch (activeTab) {
    case 'overview':
    case 'monitoring':
    case 'lobbies':
      await refreshOverview();
      await refreshLobbies();
      break;
    case 'games':
    case 'marketplace':
    case 'plugins':
      await refreshJobs();
      await refreshGames();
      break;
    case 'logs':
      await refreshLogs();
      break;
    case 'platform':
    case 'settings':
      await refreshPlatform();
      break;
    default:
      await refreshOverview();
      break;
  }
  const timeStr = `Updated ${new Date().toLocaleTimeString()}`;
  for (const ind of document.querySelectorAll('.refresh-indicator')) {
    // The plugins list is frozen between explicit renders, so its indicator shows the last RENDER
    // (owned by renderPlugins) — stamping it here would claim freshness the rows don't have.
    if (ind.id === 'last-updated-plugins') continue;
    ind.textContent = timeStr;
  }
}

// ── Overview ──────────────────────────────────────────────────────────────────

async function refreshOverview() {
  const status = await getJson('/admin/api/system/status');
  if (status) applyStatus(status);
  const metrics = await getJson('/admin/api/metrics');
  if (metrics) applyMetrics(metrics);
  // Cursor-polled, so an open dashboard fetches one new sample per tick rather than the whole hour.
  const history = await getJson(`/admin/api/metrics/history?after=${historyCursor}`);
  if (history) applyHistory(history);
}

// Each graph: a label, how to derive its series from the samples, and how to format one value.
const GRAPHS = [
  { key: 'cpu', label: 'CPU', series: (s, cores) => seriesCpuPercent(s, cores), format: (v) => `${v.toFixed(1)}%` },
  { key: 'memory', label: 'Working set', series: (s) => seriesValue(s, 'workingSetMb'), format: (v) => `${Math.round(v)} MB` },
  { key: 'players', label: 'Connected players', series: (s) => seriesValue(s, 'players'), format: (v) => Math.round(v).toString() },
  { key: 'lobbies', label: 'Active lobbies', series: (s) => seriesValue(s, 'lobbies'), format: (v) => Math.round(v).toString() },
];

function applyHistory(data) {
  // The server's sequence is authoritative, not a high-water mark of what we have seen. MetricHistory is
  // in-memory, so a restart begins numbering at 1 again — and clamping upward meant every subsequent
  // `?after=<pre-restart seq>` matched nothing and all four graphs froze on the old picture until
  // somebody reloaded the page. That is exactly the moment an operator is watching them. A sequence that
  // went BACKWARDS is the restart signal: drop the samples from the previous process rather than drawing
  // them continuously with the new ones, which would show a gap that never happened.
  const sequence = Number(data.lastSequence) || 0;
  if (sequence < historyCursor) historySamples = [];
  historyCursor = sequence;
  historySamples = mergeSamples(historySamples, data.samples, data.capacity || 240);

  el('history-badge').textContent = data.enabled ? `${historySamples.length} samples` : 'Off';
  el('history-note').textContent = data.enabled
    ? `Sampled every ${data.sampleSeconds}s by the server, keeping ${data.capacity} points `
      + `(~${Math.round((data.capacity * data.sampleSeconds) / 60)} minutes). Survives switching tabs, `
      + 'reloading, and opening the portal somewhere else.'
    : 'History is off (KnockBox:MetricSampleSeconds=0), so there is nothing to graph.';

  const host = el('history-graphs');
  host.innerHTML = '';
  if (!data.enabled) return;

  for (const graph of GRAPHS) {
    const points = downsample(graph.series(historySamples, data.processorCount || 1));
    const { path, max, last } = sparklinePath(points, { width: 240, height: 44 });

    const card = document.createElement('div');
    card.className = 'graph-card';
    card.dataset.graph = graph.key;

    const label = document.createElement('div');
    label.className = 'graph-label';
    label.textContent = graph.label;

    const value = document.createElement('div');
    value.className = 'graph-value';
    // Two samples are needed for a rate, so an empty graph is a real state early on, not a fault.
    value.textContent = last === null ? '--' : graph.format(last);

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('class', 'sparkline');
    svg.setAttribute('viewBox', '0 0 240 44');
    svg.setAttribute('preserveAspectRatio', 'none');
    svg.setAttribute('role', 'img');
    svg.setAttribute('aria-label', `${graph.label} over the retained history`);
    if (path) {
      const line = document.createElementNS('http://www.w3.org/2000/svg', 'path');
      line.setAttribute('d', path);
      line.setAttribute('fill', 'none');
      svg.appendChild(line);
    }

    const scale = document.createElement('div');
    scale.className = 'graph-scale';
    scale.textContent = path ? `peak ${graph.format(max)}` : 'collecting…';

    card.append(label, value, svg, scale);
    host.appendChild(card);
  }
}

function applyStatus(data) {
  el('metric-uptime').textContent = data.uptime || '--';
  el('metric-lobbies').textContent = data.activeLobbies ?? 0;
  el('metric-lobbies-sub').textContent = `${data.authorityLobbies ?? 0} server-authority`;
  el('metric-players').textContent = data.connectedPlayers ?? 0;
  el('metric-sockets').textContent = `Game sockets: ${data.gameSockets ?? 0}`;
  el('metric-games').textContent = data.registeredGames ?? 0;
  el('metric-memory').textContent = `${data.workingSetMb ?? '--'} MB`;
  el('metric-heap').textContent = `Managed heap: ${data.managedHeapMb ?? '--'} MB`;

  // Instantaneous CPU, differenced between polls. The payload's lifetime average is shown as the
  // sub-line: it barely moves once the process has been up a while, so on its own it hides every spike.
  const sample = { value: data.cpuSecondsTotal, at: data.hostTime };
  const live = cpuPercentBetween(cpuSample, sample, data.processorCount);
  cpuSample = sample;
  el('metric-cpu').textContent = live === null ? '--' : `${live.toFixed(1)}%`;
  el('metric-cpu-sub').textContent =
    `${(data.cpuPercentLifetime ?? 0).toFixed(2)}% lifetime avg across ${data.processorCount ?? '--'} cores`;

  const on = !!data.maintenanceMode;
  const badge = el('maintenance-badge');
  badge.textContent = on ? 'On' : 'Off';
  badge.className = `badge ${on ? 'badge-warning' : 'badge-ok'}`;
  el('maintenance-toggle').textContent = on ? 'Turn Off' : 'Turn On';
  el('maintenance-toggle').dataset.enabled = String(on);
  // Don't fight the operator's cursor: leave a message they are mid-way through typing alone.
  const messageInput = el('maintenance-message');
  if (document.activeElement !== messageInput) messageInput.value = data.maintenanceMessage || '';

  renderDiagnostics(data);
}

function renderDiagnostics(data) {
  const host = el('diagnostics-banner');
  host.innerHTML = '';
  const issues = [...(data.diagnostics || [])];
  // scanError and settingsError arrive as their own fields AND (for the games one) as a diagnostics
  // probe. Fold the settings one in here so a policy file the server couldn't read is stated plainly
  // rather than only implied by games mysteriously re-enabling themselves.
  if (data.settingsError && !issues.some((i) => i.detail === data.settingsError)) {
    issues.push({ title: 'Admin settings could not be read', detail: data.settingsError, blocking: false });
  }
  host.classList.toggle('hidden', issues.length === 0);
  for (const issue of issues) {
    const row = document.createElement('div');
    row.className = `diagnostic ${issue.blocking ? 'diagnostic-blocking' : ''}`;
    const title = document.createElement('strong');
    title.textContent = issue.blocking ? `${issue.title} (blocking)` : issue.title;
    const detail = document.createElement('span');
    detail.textContent = issue.detail;
    row.append(title, detail);
    host.appendChild(row);
  }
}

function applyMetrics(data) {
  const body = el('metrics-body');
  body.innerHTML = '';
  const games = data.games || [];
  el('metrics-empty').classList.toggle('hidden', games.length > 0);
  el('metrics-table').classList.toggle('hidden', games.length === 0);

  for (const game of games) {
    const previous = gameFrameSamples.get(game.gameId);
    const sample = { value: game.socketFramesSent, at: data.hostTime };
    const rate = ratePerSecond(previous, sample);
    gameFrameSamples.set(game.gameId, sample);

    const row = document.createElement('tr');
    appendCells(row, [
      game.gameId,
      String(game.lobbies ?? 0),
      String(game.players ?? 0),
      formatCount(game.framesIn),
      formatCount(game.framesOut),
      `${(game.fanOut ?? 0).toFixed(2)}x`,
      formatBytes(game.socketBytesSent),
      rate === null ? '--' : `${rate.toFixed(1)}/s`,
      formatCount(game.framesDropped),
      // A dash, not 0.000s, for a game with no authority module: it runs in the browser and costs this
      // process no CPU at all, which is a different statement from "it used no measurable CPU".
      game.authorityCalls > 0
        ? `${game.authorityCpuSeconds.toFixed(2)}s (${game.authorityAverageMs.toFixed(1)} ms/call)`
        : '--',
    ]);
    // Dropped frames mean a socket couldn't keep up, which is the one number here that is a problem
    // rather than just a measurement.
    if ((game.framesDropped ?? 0) > 0) row.classList.add('row-warn');
    body.appendChild(row);
  }
}

function appendCells(row, values) {
  for (const value of values) {
    const cell = document.createElement('td');
    cell.textContent = value;
    row.appendChild(cell);
  }
}

// ── Lobbies ───────────────────────────────────────────────────────────────────

async function refreshLobbies() {
  const data = await getJson('/admin/api/lobbies');
  if (!data) return;
  lobbyData = data;
  renderLobbies();
}

function renderLobbies() {
  if (!lobbyData) return;
  const filtered = filterLobbies(lobbyData.lobbies, {
    game: el('lobby-filter-game').value,
    code: el('lobby-filter-code').value,
    status: el('lobby-filter-status').value,
  });

  const body = el('lobbies-body');
  body.innerHTML = '';
  const total = (lobbyData.lobbies || []).length;
  el('lobbies-empty').textContent = total === 0 ? 'No active lobbies.' : 'No lobbies match these filters.';
  el('lobbies-empty').classList.toggle('hidden', filtered.length > 0);
  el('lobbies-table').classList.toggle('hidden', filtered.length === 0);
  setNavCount('lobbies', total);
  el('lobbies-note').textContent =
    `Showing ${filtered.length} of ${total}. A lobby counts as stale after ${lobbyData.staleAfterMinutes} `
    + 'minute(s) without activity, or as soon as nobody in it is connected.';

  for (const lobby of filtered) {
    const row = document.createElement('tr');

    const code = document.createElement('td');
    code.className = 'cell-code';
    code.textContent = lobby.code;
    row.appendChild(code);

    const game = document.createElement('td');
    game.textContent = lobby.gameName || lobby.gameId;
    if (lobby.serverAuthority) {
      const tag = document.createElement('span');
      tag.className = 'badge badge-muted';
      tag.textContent = 'authority';
      game.append(' ', tag);
    }
    row.appendChild(game);

    appendCells(row, [`${lobby.players}/${lobby.maxPlayers}`]);

    const status = document.createElement('td');
    const badge = document.createElement('span');
    badge.className = `badge badge-${lobby.status === 'stale' || lobby.status === 'empty' ? 'warning' : 'ok'}`;
    badge.textContent = lobby.status;
    status.appendChild(badge);
    row.appendChild(status);

    appendCells(row, [formatDuration(lobby.ageSeconds), formatDuration(lobby.idleSeconds)]);

    // Members with a kick button each: the operator's usual reason to open this row is one specific
    // player, so make that the click rather than making them close the whole lobby.
    const members = document.createElement('td');
    members.className = 'cell-members';
    for (const member of lobby.members || []) {
      const chip = document.createElement('span');
      chip.className = `member-chip ${member.connected ? '' : 'member-dropped'}`;
      const name = document.createElement('span');
      name.textContent = member.isHost ? `${member.displayName} (owner)` : member.displayName;
      if (!member.connected) name.title = `Disconnected ${formatDuration(member.disconnectedSeconds)} ago`;
      const kick = document.createElement('button');
      kick.className = 'chip-action';
      kick.type = 'button';
      kick.textContent = '×';
      kick.title = `Kick ${member.displayName}`;
      kick.onclick = () => kickPlayer(lobby, member);
      chip.append(name, kick);
      members.appendChild(chip);
    }
    row.appendChild(members);

    const actions = document.createElement('td');
    actions.className = 'col-actions';
    const close = document.createElement('button');
    close.className = 'btn btn-danger btn-small';
    close.type = 'button';
    close.textContent = 'Close';
    close.onclick = () => closeLobby(lobby);
    actions.appendChild(close);
    row.appendChild(actions);

    body.appendChild(row);
  }
}

async function closeLobby(lobby) {
  const name = lobby.gameName || lobby.gameId;
  if (!await confirmAction(
    `Close lobby ${lobby.code} (${name})? Its ${lobby.players} player(s) return to the home page and lose `
    + 'any game in progress.', 'Close Lobby')) return;
  if (await postJson(`/admin/api/lobbies/${encodeURIComponent(lobby.code)}/close`, {})) refreshLobbies();
}

async function kickPlayer(lobby, member) {
  if (!await confirmAction(
    `Remove ${member.displayName} from lobby ${lobby.code}? They are barred from rejoining this lobby.`,
    'Kick Player')) return;
  if (await postJson(`/admin/api/lobbies/${encodeURIComponent(lobby.code)}/kick`,
    { playerId: member.playerId })) refreshLobbies();
}

async function closeAllLobbies() {
  const total = (lobbyData?.lobbies || []).length;
  if (total === 0) { notify('There are no lobbies to close.', 'info'); return; }
  if (!await confirmAction(
    `Close all ${total} lobby/lobbies on the server? Every player in them returns to the home page and `
    + 'loses any game in progress.', 'Close Everything')) return;
  if (await postJson('/admin/api/lobbies/close', {})) refreshLobbies();
}

async function purgeStale() {
  if (await postJson('/admin/api/lobbies/purge-stale', {})) refreshLobbies();
}

function setNavCount(tab, count) {
  const badge = el(`nav-count-${tab}`);
  if (!badge) return;
  badge.textContent = String(count);
  badge.hidden = count === 0;
}

// ── Plugins & Games ───────────────────────────────────────────────────────────

// Session caches for dynamically discovered repo releases and user-selected versions.
// Prevents poll-driven refreshes from blowing away discovered releases or user-selected versions.
const pluginDiscoveredVersions = new Map();
const pluginSelectedVersions = new Map();
let lastGamesSummary = null;
let lastSourceFilterSources = null;
let renderPendingOnBlur = false;

function summarizeGames(games = []) {
  return (games || []).map((g) => `${g.id}:${g.version}:${g.availability}:${g.lifecycle}:${g.activeLobbies}:${g.activePlayers}`).join('|');
}

export function resetPluginStateForTests() {
  pluginDiscoveredVersions.clear();
  pluginSelectedVersions.clear();
  lastGamesSummary = null;
  lastSourceFilterSources = null;
  renderPendingOnBlur = false;
  activePluginTab = 'installed';
  pluginSort = { installed: 'name-az', updates: 'name-az', available: 'status' };
  pluginsDirty = false;
  pluginsRendered = false;
  pluginsLoading = false;
  pluginsFetchSeq = 0;
}

async function refreshGames({ force = false, render = false } = {}) {
  const data = await getJson('/admin/api/games');
  if (!data) return;
  gameData = data;
  const summary = summarizeGames(data.games);
  const changed = force || summary !== lastGamesSummary;
  lastGamesSummary = summary;
  if (changed) {
    // Background polls only raise the stale pill — re-rendering here is what moved rows under
    // the cursor every few seconds. Explicit callers (manual refresh, availability/delete/quota
    // saves) pass render: true for the re-render they asked for.
    if (render) renderPlugins();
    else markPluginsStale();
  }
}

export async function refreshPlugins({ refreshCatalogNow = false } = {}) {
  // The explicit refresh: skeleton, refetch everything, then one render. Poll ticks never come
  // through here — they take refreshGames/refreshJobs directly, which only mark stale.
  const seq = beginPluginsLoad();
  await Promise.all([
    refreshGames({ force: true }),
    refreshCatalog({ refresh: refreshCatalogNow }),
    refreshJobs(),
  ]);
  if (seq !== pluginsFetchSeq) return;
  pluginsLoading = false;
  renderPlugins();
}

/**
 * Entering the panel: skeleton first, then every feed the cards read (games, catalog — which
 * also carries the job set — plus the jobs feed, which may be ahead of the catalog reply),
 * then one render. Fire-and-forget (enterTab is sync) — the seq token drops the reply if the
 * operator left or refreshed again first.
 */
async function enterPluginsTab() {
  const seq = beginPluginsLoad();
  await Promise.all([refreshGames({ force: true }), refreshCatalog(), refreshJobs()]);
  if (seq !== pluginsFetchSeq) return;
  pluginsLoading = false;
  renderPlugins();
}

/**
 * Switches the visible status tab, restoring that tab's remembered sort into the sort control.
 * Instant and fetch-free: it re-slices the cached merge, which is also why it does not clear a
 * stale pill — the data is no fresher than it was, only the slice changed.
 */
export function setPluginTab(name) {
  if (!PLUGIN_TABS.includes(name)) return;
  activePluginTab = name;
  for (const btn of document.querySelectorAll('.plugin-tab-btn')) {
    const on = btn.dataset.ptab === name;
    btn.classList.toggle('active', on);
    btn.setAttribute('aria-selected', String(on));
    btn.tabIndex = on ? 0 : -1;
  }
  const sort = el('plugins-sort');
  if (sort) sort.value = pluginSort[name] ?? 'name-az';
  renderPlugins();
}

/** Tab counts: each tab's share of the search+source-filtered merge, painted on every render. */
function paintPluginCounts(base) {
  const list = base || [];
  for (const tab of PLUGIN_TABS) {
    const countEl = el(`ptab-count-${tab}`);
    if (!countEl) continue;
    const status = PLUGIN_TAB_STATUS[tab];
    const n = filterPlugins(list, { status }).length;
    countEl.textContent = `(${n})`;
  }
}

/** Background data moved behind a rendered list: say so without moving a single row. */
function markPluginsStale() {
  pluginsDirty = true;
  if (!pluginsRendered || pluginsLoading) return;
  el('plugins-stale')?.classList.remove('hidden');
}

function hidePluginsStale() {
  pluginsDirty = false;
  el('plugins-stale')?.classList.add('hidden');
}

function makePluginSkeleton() {
  const skel = document.createElement('div');
  skel.className = 'game-card plugin-card mkt-card plugin-skeleton-card';
  skel.setAttribute('aria-hidden', 'true');
  for (let i = 0; i < 3; i++) {
    const bar = document.createElement('div');
    bar.className = 'plugin-skeleton-bar';
    skel.appendChild(bar);
  }
  return skel;
}

/**
 * Starts a fetch-driven load: skeleton rows + aria-busy, and a seq token the reply must still
 * hold to render. Returns the token.
 */
function beginPluginsLoad() {
  pluginsFetchSeq += 1;
  pluginsLoading = true;
  hidePluginsStale();
  const host = el('plugins-list') || el('mkt-list') || el('games-list');
  if (host) {
    host.setAttribute('aria-busy', 'true');
    host.replaceChildren(...Array.from({ length: 6 }, makePluginSkeleton));
  }
  el('last-updated-plugins').textContent = 'Loading…';
  return pluginsFetchSeq;
}

export function renderPlugins() {
  renderSourceFilter();
  const host = el('plugins-list') || el('mkt-list') || el('games-list');
  if (!host) return;

  // If the user has focus on any control within the plugins list (e.g. open select, active tap),
  // do NOT interrupt them. Defer rendering until they blur.
  if (host.contains(document.activeElement)) {
    renderPendingOnBlur = true;
    return;
  }
  renderPendingOnBlur = false;

  if (!host._focusoutBound) {
    host._focusoutBound = true;
    host.addEventListener('focusout', () => {
      setTimeout(() => {
        if (!host.contains(document.activeElement) && renderPendingOnBlur) {
          renderPlugins();
        }
      }, 50);
    });
  }

  const allEntries = mergePluginEntries(gameData?.games || [], catalogData?.entries || []);

  // Restore discovered releases from session cache
  for (const entry of allEntries) {
    const discovered = pluginDiscoveredVersions.get(entry.id);
    if (discovered) {
      entry.availableVersions = discovered.availableVersions;
      entry.repoReleases = discovered.repoReleases;
      entry.versionsLoaded = true;
    }
  }

  const q = (el('plugins-filter-q') || el('mkt-filter-q') || el('game-filter-q'))?.value || '';
  const source = (el('plugins-filter-source') || el('mkt-filter-source'))?.value || '';
  // The status dropdown is gone: the active tab IS the status filter.
  const status = PLUGIN_TAB_STATUS[activePluginTab] ?? PLUGIN_TAB_STATUS.installed;

  const base = filterPlugins(allEntries, { q, source, status: '' });
  paintPluginCounts(base);
  const filtered = filterPlugins(base, { status });
  const sorted = sortPlugins(filtered, pluginSort[activePluginTab] ?? 'name-az');

  const totalInstalled = (gameData?.games || []).length;
  const emptyEl = el('plugins-empty') || el('mkt-empty') || el('games-empty');
  if (emptyEl) {
    emptyEl.textContent = allEntries.length === 0
      ? 'No plugins discovered or available.'
      : 'No plugins match these filters.';
    emptyEl.classList.toggle('hidden', sorted.length > 0);
  }

  // Preserve any card currently containing user focus (e.g. open select, active tap)
  const existingCards = new Map();
  for (const child of host.children) {
    if (child.dataset?.id) {
      existingCards.set(child.dataset.id, child);
    }
  }

  const newCards = [];
  for (const entry of sorted) {
    const existing = existingCards.get(entry.id);
    if (existing && existing.contains(document.activeElement)) {
      newCards.push(existing);
    } else {
      newCards.push(pluginCard(entry));
    }
  }

  const currentChildren = Array.from(host.children);
  const isIdentical = currentChildren.length === newCards.length
    && currentChildren.every((c, i) => c === newCards[i]);

  if (!isIdentical) {
    host.replaceChildren(...newCards);
  }
  host.removeAttribute('aria-busy');

  const disabledBanner = el('mkt-disabled');
  if (disabledBanner) {
    disabledBanner.classList.toggle('hidden', catalogData?.enabled !== false);
  }

  const noteEl = el('plugins-note') || el('mkt-note') || el('games-note');
  if (noteEl) {
    const parts = [];
    if (gameData?.gamesRoot) {
      parts.push(`Games root: ${gameData.gamesRoot}`);
    }
    if (gameData?.packagesRoot) {
      parts.push(`packages: ${gameData.packagesRoot}`);
    }
    if (catalogData?.managedRoot) {
      parts.push(`managed: ${catalogData.managedRoot}`);
    }
    if (catalogData?.fetchedAt) {
      parts.push(`Catalog read ${formatClock(catalogData.fetchedAt)}`);
    }
    if (gameData?.diskMeasuredAt) {
      parts.push(`Disk measured ${formatClock(gameData.diskMeasuredAt)}`);
    }
    const failed = (catalogData?.sources || []).filter((s) => s.error);
    for (const source of failed) {
      parts.push(`${source.name || source.id}: ${source.error}`);
    }
    noteEl.textContent = parts.join(' · ');
  }

  setNavCount('plugins', updatesAvailable());
  setNavCount('marketplace', updatesAvailable());
  setNavCount('games', totalInstalled);

  // The list now reflects the caches, whatever triggered this render — so any stale pill is
  // answered, the loading state (if this render ends one) resolves, and the panel's timestamp
  // records the render rather than the last background poll.
  pluginsRendered = true;
  pluginsLoading = false;
  hidePluginsStale();
  const updatedEl = el('last-updated-plugins');
  if (updatedEl) updatedEl.textContent = `List updated ${new Date().toLocaleTimeString()}`;
}

export function renderGames() {
  renderPlugins();
}

export function pluginCard(entry) {
  // A compact fixed-height row: constant height regardless of content. All plugin controls live in
  // the metadata modal (openPluginDetails) — the row itself is one big button that opens it,
  // replacing the old ellipsis button. Only rows are built imperatively, always with textContent.
  const card = document.createElement('div');
  card.className = 'game-card plugin-card mkt-card plugin-row';
  card.dataset.id = entry.id;
  card.tabIndex = 0;
  card.setAttribute('role', 'button');
  card.setAttribute('aria-label', `View details for ${entry.name || entry.id}`);
  card.addEventListener('click', () => openPluginDetails(entry));
  card.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); openPluginDetails(entry); }
  });

  const top = document.createElement('div');
  top.className = 'plugin-row-top';

  const titleWrap = document.createElement('div');
  titleWrap.className = 'plugin-row-title';
  const title = document.createElement('span');
  title.className = 'plugin-row-name';
  title.textContent = entry.name || entry.id;
  titleWrap.appendChild(title);
  if (entry.author) {
    const author = document.createElement('span');
    author.className = 'plugin-row-author';
    author.textContent = `by ${entry.author}`;
    author.title = entry.author;
    titleWrap.appendChild(author);
  }
  top.appendChild(titleWrap);

  const tagStrip = document.createElement('div');
  tagStrip.className = 'plugin-row-tags';
  for (const tag of entry.tags || []) {
    const chip = document.createElement('span');
    chip.className = 'badge badge-muted plugin-tag-chip';
    chip.textContent = tag;
    tagStrip.appendChild(chip);
  }
  const tagEllipsis = document.createElement('span');
  tagEllipsis.className = 'badge badge-muted plugin-tag-ellipsis';
  tagEllipsis.textContent = '…';
  tagEllipsis.hidden = true;
  tagStrip.appendChild(tagEllipsis);
  if ((entry.tags || []).length > 0) tagStrip.title = entry.tags.join(', ');
  top.appendChild(tagStrip);

  const version = pluginRowVersion(entry);
  const versionEl = document.createElement('span');
  versionEl.className = `plugin-row-version${version.hasUpdate ? ' plugin-row-update' : ''}`;
  versionEl.textContent = version.text;
  versionEl.title = version.title;
  top.appendChild(versionEl);

  card.appendChild(top);

  // (Status badges live bottom-right via pluginRowBadges; the version indicator above covers
  // updates. Everything else — facts, links, controls — lives in the metadata modal.)

  const bottom = document.createElement('div');
  bottom.className = 'plugin-row-bottom';

  const description = document.createElement('p');
  description.className = 'plugin-row-desc';
  description.textContent = entry.description || 'No description provided.';
  if (entry.description) description.title = entry.description;
  bottom.appendChild(description);

  const meta = document.createElement('div');
  meta.className = 'plugin-row-meta';
  for (const badge of pluginRowBadges(entry, gameData?.serverSdkVersion)) {
    const badgeEl = document.createElement('span');
    badgeEl.className = `${badge.className} plugin-mini-badge`;
    badgeEl.textContent = badge.label;
    if (badge.title) badgeEl.title = badge.title;
    meta.appendChild(badgeEl);
  }
  const size = pluginRowSize(entry);
  const sizeEl = document.createElement('span');
  sizeEl.className = 'plugin-row-size';
  sizeEl.textContent = size.text;
  sizeEl.title = size.title;
  // A package operation running against this game still shows on the list — as one mini badge
  // with the live phase as its tooltip. Progress bar and cancel live in the modal (jobRow there),
  // which is where the controls went; the row only signals that something is happening.
  const pendingJob = jobs.find((j) => j.jobId === entry.pendingJobId && !j.terminal);
  if (pendingJob) {
    const working = document.createElement('span');
    working.className = 'badge badge-warning plugin-mini-badge plugin-job-badge';
    const status = String(pendingJob.status || 'working');
    working.textContent = status.charAt(0).toUpperCase() + status.slice(1);
    working.title = pendingJob.error ? `${pendingJob.phase} ${pendingJob.error}` : (pendingJob.phase || 'Package operation in progress.');
    meta.appendChild(working);
  }
  meta.appendChild(sizeEl);
  bottom.appendChild(meta);

  card.appendChild(bottom);

  fitPluginTags(card);
  return card;
}

// One observer for every compact row's tag strip: when a row resizes (window, sidebar, filter
// bar), its visible tags are re-fit. Observed nodes are looked up back to their card, so a
// re-render that replaces the node simply stops being observed — no other state to clean up.
const pluginTagObserver = typeof ResizeObserver === 'function'
  ? new ResizeObserver((records) => {
    for (const record of records) {
      const card = record.target.closest?.('.plugin-row');
      if (card) fitPluginTags(card);
    }
  })
  : null;

/**
 * Hides the tag chips that overflow the strip, showing the `…` chip (with the full tag list as
 * its tooltip) when any are hidden. Unhides everything first, because a hidden chip measures 0
 * and a re-fit off stale measurements would hide one more chip every resize.
 *
 * Runs after layout — where there is none (jsdom) every width reads 0 and all tags stay visible,
 * which the unit tests assert structurally instead of by pixels.
 */
export function fitPluginTags(card) {
  const strip = card?.querySelector?.('.plugin-row-tags');
  if (!strip) return;
  if (pluginTagObserver && !strip._fitObserved) {
    strip._fitObserved = true;
    pluginTagObserver.observe(strip);
  }
  const chips = [...strip.querySelectorAll('.plugin-tag-chip')];
  const ellipsis = strip.querySelector('.plugin-tag-ellipsis');
  if (!ellipsis) return;
  for (const chip of chips) chip.hidden = false;
  ellipsis.hidden = true;
  if (chips.length === 0 || strip.clientWidth === 0) return;
  const widths = chips.map((chip) => chip.offsetWidth);
  const visible = visibleTagCount(widths, strip.clientWidth, ellipsis.offsetWidth || 0);
  chips.forEach((chip, i) => { chip.hidden = i >= visible; });
  if (visible < chips.length) {
    const all = chips.map((chip) => chip.textContent).join(', ');
    ellipsis.hidden = false;
    ellipsis.title = all;
    strip.title = all;
  }
}

export function gameCard(game) {
  return pluginCard(game);
}

export function openPluginDetails(entry) {
  const modal = el('plugin-details-backdrop');
  if (!modal) return;

  const title = el('plugin-details-title');
  if (title) title.textContent = `${entry.name || entry.id}`;

  const body = el('plugin-details-body');
  if (body) {
    body.innerHTML = '';

    // A running package operation shows its live phase, progress and cancel button at the top of
    // the modal — this is where the card's old inline job row moved with the rest of the controls.
    const pendingBodyJob = jobs.find((j) => j.jobId === entry.pendingJobId && !j.terminal);
    if (pendingBodyJob) body.appendChild(jobRow(pendingBodyJob, { compact: true }));

    // Section 1: Overview & Identity
    const secOverview = document.createElement('div');
    secOverview.className = 'details-section';
    const hOverview = document.createElement('h4');
    hOverview.className = 'details-section-title';
    hOverview.textContent = 'Overview & Identity';
    secOverview.appendChild(hOverview);

    const gridOverview = document.createElement('div');
    gridOverview.className = 'details-grid';
    addDetailField(gridOverview, 'Game ID', entry.id, true);
    addDetailField(gridOverview, 'Status', entry.installed ? availabilityLabel(entry.availability) : pluginStatusLabel(entry.status));
    addDetailField(gridOverview, 'Installed Version', entry.installed ? formatVersion(entry.installedVersion) : 'Not installed');
    addDetailField(gridOverview, 'Available Version', entry.availableVersion ? formatVersion(entry.availableVersion) : 'None');
    addDetailField(gridOverview, 'Source', entry.sourceName || (entry.sourceKind === 'games' ? 'Games Folder' : 'Manual Upload'));
    addDetailField(gridOverview, 'Author', entry.author || '--');
    addDetailField(gridOverview, 'License', entry.license || '--');
    addDetailField(gridOverview, 'Content Rating', entry.contentRating || '--');
    secOverview.appendChild(gridOverview);

    const links = marketplaceLinks(entry);
    if (links) secOverview.appendChild(links);
    body.appendChild(secOverview);

    // Section 2: Description & Tags
    const secDesc = document.createElement('div');
    secDesc.className = 'details-section';
    const hDesc = document.createElement('h4');
    hDesc.className = 'details-section-title';
    hDesc.textContent = 'Description & Tags';
    secDesc.appendChild(hDesc);

    const descP = document.createElement('p');
    descP.className = 'mkt-desc';
    descP.textContent = entry.description || 'No description provided.';
    secDesc.appendChild(descP);

    if (entry.tags && entry.tags.length > 0) {
      const tagHost = document.createElement('div');
      tagHost.className = 'details-badges';
      for (const tag of entry.tags) {
        const tagSpan = document.createElement('span');
        tagSpan.className = 'badge badge-muted';
        tagSpan.textContent = tag;
        tagHost.appendChild(tagSpan);
      }
      secDesc.appendChild(tagHost);
    }
    body.appendChild(secDesc);

    // Section 3: Gameplay & Runtime
    const secGame = document.createElement('div');
    secGame.className = 'details-section';
    const hGame = document.createElement('h4');
    hGame.className = 'details-section-title';
    hGame.textContent = 'Gameplay & Runtime';
    secGame.appendChild(hGame);

    const gridGame = document.createElement('div');
    gridGame.className = 'details-grid';
    addDetailField(gridGame, 'Players', playerRange(entry) || (entry.maxPlayers ? String(entry.maxPlayers) : '--'));
    addDetailField(gridGame, 'Server Authority', entry.serverAuthority ? 'Yes' : 'No');
    if (entry.installed) {
      addDetailField(gridGame, 'Active Lobbies', `${entry.activeLobbies || 0} lobby/lobbies`);
      addDetailField(gridGame, 'Connected Players', `${entry.activePlayers || 0} player(s)`);
      addDetailField(gridGame, 'Lifecycle', lifecycleLabel(entry.lifecycle) || 'Ready');
      addDetailField(gridGame, 'Update Policy', entry.managed ? (entry.updatePolicy || 'manual') : 'N/A (folder game)');
      if (entry.sdkStatus && entry.sdkStatus !== 'unknown') {
        addDetailField(gridGame, 'SDK Status', entry.sdkStatus);
      }
    }
    secGame.appendChild(gridGame);
    body.appendChild(secGame);

    // Section 4: Storage & Sizing
    const secStorage = document.createElement('div');
    secStorage.className = 'details-section';
    const hStorage = document.createElement('h4');
    hStorage.className = 'details-section-title';
    hStorage.textContent = 'Storage & Disk Breakdown';
    secStorage.appendChild(hStorage);

    const gridStorage = document.createElement('div');
    gridStorage.className = 'details-grid';
    if (entry.installed) {
      addDetailField(gridStorage, 'Total Disk Usage', formatBytes(entry.diskBytes));
      addDetailField(gridStorage, 'Extracted Directory', formatBytes(entry.directoryBytes));
      addDetailField(gridStorage, 'Compressed Cache', formatBytes(entry.compressedBytes));
      addDetailField(gridStorage, 'Package Bytes', formatBytes(entry.packageBytes));
      if (entry.backupBytes) {
        addDetailField(gridStorage, 'Retained Backups Size', formatBytes(entry.backupBytes));
      }
      if (entry.directory) {
        addDetailField(gridStorage, 'Location', entry.directory, true);
      }
    } else {
      addDetailField(gridStorage, 'Download Size', formatBytes(entry.sizeBytes));
    }
    secStorage.appendChild(gridStorage);
    body.appendChild(secStorage);

    // Section: Settings
    if (entry.installed) {
      const secSettings = document.createElement('div');
      secSettings.className = 'details-section';
      const hSettings = document.createElement('h4');
      hSettings.className = 'details-section-title';
      hSettings.textContent = 'Settings';
      secSettings.appendChild(hSettings);

      const row = document.createElement('div');
      row.className = 'field-row';

      const label = document.createElement('label');
      label.className = 'limit-label';
      label.textContent = 'Blob Quota Override';
      label.htmlFor = 'plugin-blob-quota-bytes';

      const group = document.createElement('div');
      group.className = 'byte-input-group filter-narrow';

      const input = document.createElement('input');
      input.type = 'text';
      input.inputMode = 'numeric';
      input.className = 'text-input byte-input';
      input.id = 'plugin-blob-quota-bytes';
      input.placeholder = 'Quota (empty to disable)';
      input.title = 'Per-game override of Blob quota per session. Leave empty to disable the override.';

      const select = document.createElement('select');
      select.className = 'text-input byte-scale-select';
      select.id = 'plugin-blob-quota-scale';
      select.title = 'Unit scaling';
      for (const unit of BYTE_UNITS) {
        const opt = document.createElement('option');
        opt.value = unit;
        opt.textContent = unit;
        select.appendChild(opt);
      }

      group.append(input, select);

      const setBtn = document.createElement('button');
      setBtn.type = 'button';
      setBtn.className = 'btn btn-secondary btn-small';
      setBtn.id = 'plugin-blob-quota-set';
      setBtn.textContent = 'Set';

      const hint = document.createElement('span');
      hint.className = 'limit-hint';
      hint.id = 'plugin-blob-quota-hint';

      let quota = (limitsData?.blobQuotas && Object.prototype.hasOwnProperty.call(limitsData.blobQuotas, entry.id))
        ? limitsData.blobQuotas[entry.id]
        : (entry.blobQuota ?? null);

      const updateQuotaUI = () => {
        const defaultBytes = limitsData?.effective?.blobLobbyQuotaBytes ?? limitsData?.defaults?.blobLobbyQuotaBytes;
        const defaultDisplay = formatByteLimit(defaultBytes);

        if (quota === null || quota === undefined) {
          input.value = '';
          select.value = 'BYTE';
          hint.textContent = defaultDisplay !== '--'
            ? `Disabled — uses server default (${defaultDisplay}). Leave empty to disable.`
            : 'Disabled — uses server default. Leave empty to disable.';
        } else if (quota < 0) {
          input.value = String(quota);
          select.value = 'BYTE';
          hint.textContent = 'Overridden — no per-session blob quota cap for this game.';
        } else {
          const split = splitBytes(quota);
          input.value = String(split.value);
          select.value = split.unit;
          hint.textContent = defaultDisplay !== '--'
            ? `Overridden — server default is ${defaultDisplay}.`
            : 'Overridden.';
        }
      };

      updateQuotaUI();

      if (!limitsData) {
        getJson('/admin/api/limits').then((data) => {
          if (!data) return;
          limitsData = data;
          if (document.activeElement !== input && document.activeElement !== select) {
            if (limitsData.blobQuotas && Object.prototype.hasOwnProperty.call(limitsData.blobQuotas, entry.id)) {
              quota = limitsData.blobQuotas[entry.id];
            }
            updateQuotaUI();
          }
        }).catch(() => {});
      }

      input.addEventListener('keydown', (e) => {
        if (['Backspace', 'Delete', 'ArrowLeft', 'ArrowRight', 'Tab', 'Home', 'End'].includes(e.key)) return;
        if (e.ctrlKey || e.metaKey) return;
        if (e.key === '-' && input.selectionStart === 0 && !input.value.includes('-')) return;
        if (e.key === 'Enter') {
          e.preventDefault();
          saveQuota();
          return;
        }
        if (!/^[0-9]$/.test(e.key)) {
          e.preventDefault();
        }
      });

      input.addEventListener('input', () => {
        input.value = input.value.replace(/(?!^-)[^0-9]/g, '');
      });

      const saveQuota = async () => {
        const text = input.value.trim();
        let bytes = null;
        if (text !== '') {
          const rawNumber = Number(text);
          if (!Number.isInteger(rawNumber)) {
            notify('Quota must be a whole number.', 'error');
            return;
          }
          if (rawNumber === 0) {
            notify('Leave quota empty to disable the override, or use a negative value for no cap.', 'error');
            return;
          }
          if (rawNumber < 0) {
            bytes = -1;
          } else {
            const scale = select.value || 'BYTE';
            const multiplier = BYTE_MULTIPLIERS[scale] || 1;
            bytes = rawNumber * multiplier;
          }
        }

        const res = await postJson('/admin/api/blob-quota', { gameId: entry.id, bytes });
        if (!res) return;

        quota = bytes;
        entry.blobQuota = bytes;
        if (limitsData?.blobQuotas) {
          if (bytes === null) {
            delete limitsData.blobQuotas[entry.id];
          } else {
            limitsData.blobQuotas[entry.id] = bytes;
          }
        }
        if (gameData?.games) {
          const g = gameData.games.find((x) => x.id === entry.id);
          if (g) g.blobQuota = bytes;
        }

        updateQuotaUI();
        refreshGames({ render: true });
      };

      setBtn.addEventListener('click', saveQuota);

      row.append(label, group, setBtn, hint);
      secSettings.appendChild(row);
      body.appendChild(secSettings);
    }

    // Section 5: Retained Backups
    if (entry.backups && entry.backups.length > 0) {
      const secBackups = document.createElement('div');
      secBackups.className = 'details-section';
      const hBackups = document.createElement('h4');
      hBackups.className = 'details-section-title';
      hBackups.textContent = 'Retained Version Backups';
      secBackups.appendChild(hBackups);

      const tableWrap = document.createElement('div');
      tableWrap.className = 'table-scroll';
      const table = document.createElement('table');
      table.className = 'data-table';
      table.innerHTML = '<thead><tr><th>Version</th><th>Size</th><th>Retained Date</th></tr></thead>';
      const tbody = document.createElement('tbody');
      for (const b of entry.backups) {
        const tr = document.createElement('tr');
        // textContent, not innerHTML: b.version is parsed out of the backup filename, which is built
        // from GameManifest.Version — a field a .kbg declares and nothing validates. Interpolated, a
        // package could put script in this modal, on the admin origin, with the session cookie.
        for (const text of [formatVersion(b.version), formatBytes(b.bytes), formatDateTime(b.retainedAt)]) {
          const td = document.createElement('td');
          td.textContent = text;
          tr.appendChild(td);
        }
        tbody.appendChild(tr);
      }
      table.appendChild(tbody);
      tableWrap.appendChild(table);
      secBackups.appendChild(tableWrap);
      body.appendChild(secBackups);
    }

    // Section 6: Repository Releases
    if (entry.sourceId && entry.sourceKind !== 'upload') {
      const secReleases = document.createElement('div');
      secReleases.className = 'details-section';
      const hReleases = document.createElement('h4');
      hReleases.className = 'details-section-title';
      hReleases.textContent = 'Repository Releases';
      secReleases.appendChild(hReleases);

      const renderReleasesTable = (releases) => {
        const tableWrap = document.createElement('div');
        tableWrap.className = 'table-scroll';
        const table = document.createElement('table');
        table.className = 'data-table';
        table.innerHTML = '<thead><tr><th>Version</th><th>Tag</th><th>Size</th><th>Released Date</th><th>Action</th></tr></thead>';
        const tbody = document.createElement('tbody');
        for (const rel of releases) {
          const tr = document.createElement('tr');
          const tdVer = document.createElement('td');
          tdVer.textContent = formatVersion(rel.version);
          const tdTag = document.createElement('td');
          const codeTag = document.createElement('code');
          codeTag.textContent = rel.tag || '';
          tdTag.appendChild(codeTag);
          const tdSize = document.createElement('td');
          tdSize.textContent = formatBytes(rel.sizeBytes);
          const tdDate = document.createElement('td');
          tdDate.textContent = rel.publishedAt ? formatDateTime(rel.publishedAt) : '--';

          const tdAction = document.createElement('td');
          if (entry.installed && rel.version === entry.installedVersion) {
            const currentBadge = document.createElement('span');
            currentBadge.className = 'badge badge-ok';
            currentBadge.textContent = 'Installed';
            tdAction.appendChild(currentBadge);
          } else {
            const relBtn = document.createElement('button');
            relBtn.type = 'button';
            const isDown = entry.installed && compareSemVer(rel.version, entry.installedVersion) < 0;
            const btnLabel = !entry.installed ? 'Install' : (isDown ? 'Downgrade' : 'Update');
            relBtn.className = `btn btn-small ${isDown ? 'btn-danger' : 'btn-primary'}`;
            relBtn.textContent = btnLabel;
            relBtn.onclick = () => {
              const act = versionAction(entry, `available:${rel.version}`);
              pluginSelectedVersions.set(entry.id, `available:${rel.version}`);
              runPackageAction(entry, act, 'drain');
              modal.classList.add('hidden');
            };
            tdAction.appendChild(relBtn);
          }

          tr.append(tdVer, tdTag, tdSize, tdDate, tdAction);
          tbody.appendChild(tr);
        }
        table.appendChild(tbody);
        tableWrap.appendChild(table);
        secReleases.appendChild(tableWrap);
      };

      const cached = pluginDiscoveredVersions.get(entry.id);
      if (cached?.repoReleases?.length > 0) {
        entry.availableVersions = cached.availableVersions;
        entry.repoReleases = cached.repoReleases;
        entry.versionsLoaded = true;
        renderReleasesTable(cached.repoReleases);
        body.appendChild(secReleases);
      } else {
        const statusP = document.createElement('p');
        statusP.className = 'mkt-desc';
        statusP.textContent = 'Loading releases from repository…';
        secReleases.appendChild(statusP);
        body.appendChild(secReleases);

        getJson(`/admin/api/marketplace/plugins/${encodeURIComponent(entry.id)}/versions`).then((res) => {
          if (!res?.versions || res.versions.length === 0) {
            statusP.textContent = 'No repository releases found.';
            return;
          }
          statusP.remove();
          const versions = res.versions.map((v) => v.version);
          entry.availableVersions = versions;
          entry.repoReleases = res.versions;
          entry.versionsLoaded = true;
          pluginDiscoveredVersions.set(entry.id, {
            availableVersions: versions,
            repoReleases: res.versions,
          });

          renderReleasesTable(res.versions);

          if (typeof populateModalVersionSelect === 'function') {
            populateModalVersionSelect();
            if (typeof refreshModalAction === 'function') refreshModalAction();
          }
        }).catch(() => {
          statusP.textContent = 'Could not load releases from repository.';
        });
      }
    }
  }

  // Populate Actions in footer
  const actionsHost = el('plugin-details-actions');
  let populateModalVersionSelect = null;
  let refreshModalAction = null;
  if (actionsHost) {
    actionsHost.innerHTML = '';

    const pending = jobs.find((j) => j.jobId === entry.pendingJobId && !j.terminal);

    const versionSelect = document.createElement('select');
    versionSelect.className = 'text-input filter-narrow plugin-version mkt-version';
    populateModalVersionSelect = (preferredValue = null) => {
      versionSelect.innerHTML = '';
      for (const option of versionOptions(entry)) {
        const opt = document.createElement('option');
        opt.value = versionOptionValue(option);
        opt.textContent = option.kind === 'loadMore'
          ? 'Load older versions from repo…'
          : `${formatVersion(option.version)} — ${option.kind}`;
        versionSelect.appendChild(opt);
      }
      if (versionSelect.options.length === 0) {
        versionSelect.disabled = true;
      } else {
        const saved = preferredValue ?? pluginSelectedVersions.get(entry.id);
        if (saved && Array.from(versionSelect.options).some((o) => o.value === saved)) {
          versionSelect.value = saved;
        }
      }
    };
    populateModalVersionSelect();
    actionsHost.appendChild(versionSelect);

    if (entry.installed) {
      const availSelect = document.createElement('select');
      availSelect.className = 'text-input filter-narrow plugin-availability';
      for (const option of AVAILABILITY) {
        const opt = document.createElement('option');
        opt.value = option.value;
        opt.textContent = option.label;
        if (option.value === entry.availability) opt.selected = true;
        availSelect.appendChild(opt);
      }
      availSelect.onchange = () => setAvailability(entry, availSelect.value);
      if (isBusyLifecycle(entry.lifecycle)) {
        availSelect.disabled = true;
        availSelect.title = `${lifecycleLabel(entry.lifecycle)} — availability can't change mid-update.`;
      }
      actionsHost.appendChild(availSelect);
    }

    const modeSelect = document.createElement('select');
    modeSelect.className = 'text-input filter-narrow plugin-mode mkt-mode';
    for (const option of UPDATE_MODES) {
      const opt = document.createElement('option');
      opt.value = option.value;
      opt.textContent = option.label;
      opt.title = option.hint;
      modeSelect.appendChild(opt);
    }
    if ((entry.activeLobbies || 0) === 0) {
      modeSelect.disabled = true;
      modeSelect.title = 'Nobody is playing this game right now, so it applies immediately either way.';
    }
    actionsHost.appendChild(modeSelect);

    // Update policy (installed & managed) — moved here with the rest of the controls.
    if (entry.installed && entry.managed) {
      const policySelect = document.createElement('select');
      policySelect.className = 'text-input filter-narrow plugin-policy mkt-policy';
      for (const option of UPDATE_POLICIES) {
        const opt = document.createElement('option');
        opt.value = option.value;
        opt.textContent = option.label;
        opt.title = option.hint;
        if (option.value === entry.updatePolicy) opt.selected = true;
        policySelect.appendChild(opt);
      }
      policySelect.value = entry.updatePolicy || 'manual';
      policySelect.disabled = Boolean(pending);
      policySelect.onchange = () => postJson(`/admin/api/packages/${encodeURIComponent(entry.id)}/update-policy`,
        { policy: policySelect.value });
      actionsHost.appendChild(policySelect);
    }

    // Staged launch link — moved here with the rest of the controls.
    if (entry.installed && entry.availability === 'staged') {
      const copyLink = document.createElement('button');
      copyLink.type = 'button';
      copyLink.className = 'btn btn-secondary mkt-staged-link';
      copyLink.textContent = 'Copy launch link';
      copyLink.onclick = () => copyStagedLink(entry);
      actionsHost.appendChild(copyLink);
    }

    const actionBtn = document.createElement('button');
    actionBtn.type = 'button';
    actionBtn.className = 'btn plugin-action mkt-action';
    refreshModalAction = () => {
      const decided = versionAction(entry, versionSelect.value,
        catalogData?.canInstall === false ? catalogData?.installBlockedReason || 'Installs are unavailable.' : null);
      actionBtn.textContent = decided.label;
      actionBtn.className = `btn plugin-action mkt-action ${decided.danger ? 'btn-danger' : 'btn-primary'}`;
      actionBtn.disabled = Boolean(pending) || decided.kind === 'none' || Boolean(decided.blockedReason);
      actionBtn.title = decided.blockedReason || '';
      actionBtn.onclick = () => {
        if (versionSelect.value !== 'load:more') {
          pluginSelectedVersions.set(entry.id, versionSelect.value);
        }
        runPackageAction(entry, decided, modeSelect.value);
        modal.classList.add('hidden');
      };
    };
    versionSelect.onchange = () => {
      if (versionSelect.value !== 'load:more') {
        pluginSelectedVersions.set(entry.id, versionSelect.value);
      }
      refreshModalAction();
    };
    refreshModalAction();
    actionsHost.appendChild(actionBtn);

    if (entry.installed) {
      const exportBtn = document.createElement('button');
      exportBtn.type = 'button';
      exportBtn.className = 'btn btn-primary plugin-export game-export mkt-export';
      exportBtn.textContent = 'Export';
      exportBtn.onclick = () => exportGame(entry.id);
      actionsHost.appendChild(exportBtn);

      const deleteBtn = document.createElement('button');
      deleteBtn.type = 'button';
      deleteBtn.className = 'btn btn-danger plugin-delete mkt-uninstall';
      deleteBtn.textContent = entry.root === 'games' ? 'Delete' : 'Uninstall';
      if (isBusyLifecycle(entry.lifecycle)) {
        deleteBtn.disabled = true;
        deleteBtn.title = `${lifecycleLabel(entry.lifecycle)} — wait for the update to finish.`;
      } else if (!entry.deletable) {
        deleteBtn.disabled = true;
        deleteBtn.title = entry.deleteBlockedReason || 'This game cannot be deleted on this deployment.';
      } else {
        deleteBtn.disabled = Boolean(pending);
        if (entry.deleteBlockedReason) deleteBtn.title = entry.deleteBlockedReason;
      }
      deleteBtn.onclick = () => {
        modal.classList.add('hidden');
        if (entry.root === 'games') deleteGame(entry);
        else uninstallGame(entry);
      };
      actionsHost.appendChild(deleteBtn);
    }

    const closeBtn = document.createElement('button');
    closeBtn.type = 'button';
    closeBtn.id = 'plugin-details-close';
    closeBtn.className = 'btn btn-secondary';
    closeBtn.textContent = 'Close';
    closeBtn.onclick = () => modal.classList.add('hidden');
    actionsHost.appendChild(closeBtn);
  }

  modal.classList.remove('hidden');
}

function addDetailField(host, label, value, isCode = false) {
  const field = document.createElement('div');
  field.className = 'details-field';
  const l = document.createElement('span');
  l.className = 'details-label';
  l.textContent = label;
  field.appendChild(l);
  const v = document.createElement(isCode ? 'code' : 'span');
  v.className = 'details-value';
  v.textContent = value;
  field.appendChild(v);
  host.appendChild(field);
}

/**
 * A catalog entry's link fields, but only if they really are https URLs.
 *
 * These strings are author-supplied and arrive from a repository this server does not control. The
 * marketplace schema restricts them to https://, but that is enforced where an entry is PUBLISHED —
 * nothing revalidates them on the way in here, so a portal that trusted them would be one compromised
 * or hand-edited catalog away from rendering a `javascript:` link on an authenticated admin page.
 */
function httpsUrl(value) {
  if (typeof value !== 'string' || value === '') return null;
  try {
    return new URL(value).protocol === 'https:' ? value : null;
  } catch {
    return null;
  }
}

/** The homepage/issues row, or null when the entry offers neither usable link. */
function marketplaceLinks(entry) {
  const targets = [
    ['Homepage', httpsUrl(entry.homepage)],
    ['Report a problem', httpsUrl(entry.bugs)],
  ].filter(([, href]) => href);
  if (targets.length === 0) return null;

  const row = document.createElement('div');
  row.className = 'mkt-links';
  for (const [label, href] of targets) {
    const link = document.createElement('a');
    link.href = href;
    link.textContent = label;
    link.target = '_blank';
    // noreferrer as well as noopener: the destination is chosen by the game's author, and an admin
    // portal URL is not something they need to be told.
    link.rel = 'noopener noreferrer';
    row.appendChild(link);
  }
  return row;
}

async function setAvailability(game, state) {
  if (state === game.availability) return;
  const running = game.activeLobbies;
  if (state !== 'available' && running > 0
    && !await confirmAction(
      `Set ${game.name} to ${availabilityLabel(state)}? It disappears from the player catalogue and new `
      + `lobbies are refused, but its ${running} running lobby/lobbies keep playing until they finish.`,
      `Set ${availabilityLabel(state)}`)) {
    renderGames(); // put the select back where it was
    return;
  }
  if (await postJson(`/admin/api/games/${encodeURIComponent(game.id)}/availability`, { state })) refreshGames({ render: true });
  else renderGames();
}

async function deleteGame(game) {
  // Same predicate as uninstallGame: the two had drifted into disagreeing about the same plugin.
  const warning = pluginRestoreWarning(game);
  if (!await confirmAction(
    `Delete ${game.name} and all ${formatBytes(game.diskBytes)} of its files from disk? `
    + (game.activeLobbies > 0 ? `Its ${game.activeLobbies} running lobby/lobbies are closed first. ` : '')
    + 'This cannot be undone — the game has to be reinstalled to come back.',
    'Delete Permanently',
    {
      warning,
      onExport: () => exportGame(game.id),
    })) return;
  if (await postJson(`/admin/api/games/${encodeURIComponent(game.id)}/delete`, {})) await refreshPlugins();
}

async function copyStagedLink(game) {
  // The shell origin, not this one: the link is for a player's browser. Derived from the games root's
  // sibling rather than guessed — but the admin origin can't know the shell's public URL, so offer the
  // relative form and let the operator paste it against their own host.
  const link = `/?game=${encodeURIComponent(game.id)}`;
  try {
    await navigator.clipboard.writeText(link);
    notify(`Copied "${link}" — append it to your shell's address. Visibility only, not access control.`, 'success');
  } catch {
    notify(`Launch path: ${link}`, 'info');
  }
}

// ── Logs ──────────────────────────────────────────────────────────────────────

async function refreshLogs() {
  if (!el('log-follow').checked && logEntries.length > 0) return;

  const params = new URLSearchParams();
  if (logCursor > 0) params.set('after', String(logCursor));
  const level = el('log-filter-level').value;
  if (level) params.set('level', level);
  const category = el('log-filter-category').value.trim();
  if (category) params.set('category', category);
  const search = el('log-filter-q').value.trim();
  if (search) params.set('q', search);
  params.set('limit', String(LOG_VIEW_LIMIT));

  const data = await getJson(`/admin/api/logs?${params}`);
  if (!data) return;

  logEntries = appendLogEntries(logEntries, data.entries, LOG_VIEW_LIMIT);
  // Advance the cursor past everything the server has, not merely past what matched: with a filter
  // applied, re-asking from the last MATCHING sequence would re-scan the same non-matching entries on
  // every poll and re-deliver anything that matched later.
  logCursor = data.lastSequence ?? logCursor;
  renderLogs(data);
}

function renderLogs(data) {
  const stream = el('log-stream');
  // Only auto-scroll when the operator is already at the bottom — yanking the view down while they are
  // reading something further up is the classic log-viewer annoyance.
  const atBottom = stream.scrollHeight - stream.scrollTop - stream.clientHeight < 40;

  stream.innerHTML = '';
  el('logs-empty').classList.toggle('hidden', logEntries.length > 0);
  for (const entry of logEntries) {
    const line = document.createElement('div');
    line.className = `log-line ${logLevelClass(entry.level)}`;

    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = formatClock(entry.time);

    const level = document.createElement('span');
    level.className = 'log-level';
    level.textContent = logLevelTag(entry.level);

    const category = document.createElement('span');
    category.className = 'log-category';
    // The full category is long and repetitive ("KnockBox.Server.Games.GameCatalog"); the last segment
    // is the part that identifies the subsystem. Full text stays in the tooltip.
    category.textContent = String(entry.category || '').split('.').pop() || '-';
    category.title = entry.category || '';

    const message = document.createElement('span');
    message.className = 'log-message';
    message.textContent = entry.message;

    line.append(time, level, category, message);
    if (entry.exception) {
      const ex = document.createElement('pre');
      ex.className = 'log-exception';
      ex.textContent = entry.exception;
      line.appendChild(ex);
    }
    stream.appendChild(line);
  }

  if (atBottom) stream.scrollTop = stream.scrollHeight;
  el('logs-note').textContent = data
    ? `Showing ${logEntries.length} of ${data.buffered} buffered (${formatCount(data.totalWritten)} logged `
      + 'since start). Older entries are only in the log files.'
    : '';
}

// Re-reading from cursor 0 is what makes a filter change apply to entries ALREADY in the ring rather
// than only to whatever is logged next.
function resetLogStream() {
  logCursor = 0;
  logEntries = [];
  refreshLogs();
}

export function openTerminalWindow() {
  const width = Math.min(1100, Math.floor(window.screen?.availWidth ? window.screen.availWidth * 0.85 : 1100));
  const height = Math.min(750, Math.floor(window.screen?.availHeight ? window.screen.availHeight * 0.8 : 750));
  const left = Math.max(0, Math.floor(((window.screen?.availWidth || 1200) - width) / 2));
  const top = Math.max(0, Math.floor(((window.screen?.availHeight || 800) - height) / 2));
  const features = `width=${width},height=${height},left=${left},top=${top},menubar=no,toolbar=no,location=no,status=no,resizable=yes,scrollbars=yes`;
  const win = window.open('terminal.html', 'KnockBoxLogsTerminal', features);
  if (win) {
    win.focus();
  }
  return win;
}

async function openLogFiles() {
  const data = await getJson('/admin/api/logs/files');
  if (!data) return;
  const host = el('files-list');
  host.innerHTML = '';
  el('files-note').textContent = data.error || `From ${data.logsRoot}`;
  for (const file of data.files || []) {
    const row = document.createElement('a');
    row.className = 'file-row';
    row.href = `/admin/api/logs/files/${encodeURIComponent(file.name)}`;
    row.download = file.name;
    const name = document.createElement('span');
    name.textContent = file.name;
    const meta = document.createElement('span');
    meta.className = 'file-meta';
    meta.textContent = `${formatBytes(file.bytes)} — ${formatClock(file.modified)}`;
    row.append(name, meta);
    host.appendChild(row);
  }
  if (!(data.files || []).length) {
    const empty = document.createElement('p');
    empty.className = 'empty-state';
    empty.textContent = 'No log files found.';
    host.appendChild(empty);
  }
  el('files-backdrop').classList.remove('hidden');
}

// ── Wiring ────────────────────────────────────────────────────────────────────

// ── Marketplace & packages ────────────────────────────────────────────────────

// The catalog can reach the network, so it is NEVER on the poll path — see POLL_MS.
// Like refreshGames, it only re-renders for an explicit caller (render: true); background
// arrivals (notably job completions) update the caches and raise the stale pill instead.
async function refreshCatalog({ refresh = false, render = false } = {}) {
  const data = await getJson(`/admin/api/marketplace/catalog${refresh ? '?refresh=1' : ''}`);
  if (!data) return;
  catalogData = data;
  // The catalog reply carries the current job set too, so entering the tab costs one request rather
  // than two.
  jobs = mergeJobs(jobs, data.jobs, JOB_VIEW_LIMIT);
  jobCursor = Math.max(jobCursor, Number(data.jobsLastSequence) || 0);
  if (render) renderPlugins();
  else markPluginsStale();
}

async function refreshJobs() {
  let data = await getJson(`/admin/api/packages/jobs?after=${jobCursor}`);
  if (!data) return;

  // A sequence that went BACKWARDS means the server restarted: the registry is in-memory, so it begins
  // again at 1. Without this, every real job that follows sorts below the stale rows we are still
  // holding and is sliced away at JOB_VIEW_LIMIT, while the cursor — only ever clamped upward — asks
  // for everything after a sequence the new process will not reach for a long time. The log feed
  // already handles exactly this; the job feed is the same shape and did not.
  if ((Number(data.lastSequence) || 0) < jobCursor) {
    jobs = [];
    jobCursor = 0;
    // Notified ids belong to the old process and will never reappear — drop them so the set cannot
    // grow one entry per finished job for the lifetime of the page.
    reportedJobs.clear();
    // The fetch above used the old process's cursor, so jobs the new process already created were
    // missed: re-read at zero in this same tick rather than leaving per-card progress absent until
    // the next poll.
    data = await getJson(`/admin/api/packages/jobs?after=0`);
    if (!data) return;
  }

  const lastSequence = Number(data.lastSequence) || 0;
  const before = new Set(jobs.filter((j) => j.terminal).map((j) => j.jobId));
  jobs = mergeJobs(jobs, data.jobs, JOB_VIEW_LIMIT);
  jobCursor = lastSequence || jobCursor;
  // Bound the notified set to what is still in view: an id evicted at JOB_VIEW_LIMIT that later
  // reappears notifies again, exactly as after a restart.
  const inView = new Set(jobs.map((j) => j.jobId));
  for (const id of reportedJobs) {
    if (!inView.has(id)) reportedJobs.delete(id);
  }

  // A job reaching a terminal state is the moment the catalog's answer changed — but the frozen
  // list does not flip on its own. The re-read below only refreshes the caches and raises the stale
  // pill; the bell notification (see announceJob) is what tells the operator, and Refresh re-renders.
  // There is no operations list anymore: the feed is polled silently and outcomes surface as
  // notifications (see announceJob below).
  let finished = false;
  for (const job of jobs) {
    if (!job.terminal || before.has(job.jobId)) continue;
    finished = true;
    if (!reportedJobs.has(job.jobId)) {
      reportedJobs.add(job.jobId);
      announceJob(job);
    }
  }

  setNavCount('marketplace', updatesAvailable());
  if (finished) {
    refreshGames();
    refreshCatalog();
  }
}

function announceJob(job) {
  const what = `${job.gameName || job.gameId}`;
  if (job.status === 'succeeded') notify(`${what}: ${job.phase}`, 'success');
  else if (job.status === 'failed') notify(`${what} failed: ${job.error || job.phase}`, 'error');
  else notify(`${what}: ${job.phase}`, 'warning');
}

function updatesAvailable() {
  return (catalogData?.entries || []).filter((e) => e.status === 'updateAvailable').length;
}

function renderSourceFilter() {
  const select = el('plugins-filter-source') || el('mkt-filter-source');
  if (!select) return;
  if (select.contains(document.activeElement)) return;

  const sources = catalogData?.sources || [];
  const sourcesKey = sources.map((s) => `${s.id}:${s.name || ''}`).join('|');
  if (select.options.length > 0 && sourcesKey === lastSourceFilterSources) return;
  lastSourceFilterSources = sourcesKey;

  const current = select.value;
  select.innerHTML = '';
  const any = document.createElement('option');
  any.value = '';
  any.textContent = 'All Sources';
  select.appendChild(any);

  const gamesOpt = document.createElement('option');
  gamesOpt.value = 'games';
  gamesOpt.textContent = 'Games Folder';
  select.appendChild(gamesOpt);

  const uploadOpt = document.createElement('option');
  uploadOpt.value = 'upload';
  uploadOpt.textContent = 'Manual Upload';
  select.appendChild(uploadOpt);

  for (const source of sources) {
    const opt = document.createElement('option');
    opt.value = source.id;
    opt.textContent = source.name || source.id;
    select.appendChild(opt);
  }
  select.value = current;
}

export function renderMarketplace() {
  renderPlugins();
}

export function marketplaceCard(entry) {
  return pluginCard(entry);
}

async function runPackageAction(entry, decided, mode) {
  const name = entry.name || entry.id;
  const version = decided.version;
  if (decided.kind === 'rollback') {
    if (!await confirmAction(
      `Roll ${name} back from ${formatVersion(entry.installedVersion)} to ${formatVersion(version)}? `
      + describeMode(mode, entry.activeLobbies), 'Roll Back')) return;
    if (await postJson(`/admin/api/packages/${encodeURIComponent(entry.id)}/rollback`, { version, mode })) {
      refreshJobs();
    }
    return;
  }

  if (decided.kind === 'downgrade') {
    const runningDesc = entry.activeLobbies > 0 ? ` ${describeMode(mode, entry.activeLobbies)}` : '';
    if (!await confirmAction(
      `Downgrade ${name} from ${formatVersion(entry.installedVersion)} to ${formatVersion(version)}?`
      + ` This replaces the installed version with an older release from the marketplace.${runningDesc}`,
      'Downgrade', { danger: true })) return;

    if (await postJson(`/admin/api/marketplace/install/${encodeURIComponent(entry.id)}`,
      { version: version || null, sourceId: entry.sourceId || null, mode })) refreshJobs();
    return;
  }

  if (decided.incompatible) {
    const runningDesc = entry.activeLobbies > 0 ? ` ${describeMode(mode, entry.activeLobbies)}` : '';
    const reasonText = entry.reason ? ` (${entry.reason})` : '';
    // An update REPLACES a version that is presumably working, which the old wording never said — it read
    // identically whether this was a first install or an overwrite of a running game. And the server
    // stages the result either way, so say that here rather than letting it arrive as a surprise.
    const replaces = (decided.kind === 'update' || decided.kind === 'downgrade')
      ? ` This replaces the installed ${formatVersion(entry.installedVersion)}.`
      : '';
    if (!await confirmAction(
      `Install ${name} ${formatVersion(version)}? This game is unsupported on this server${reasonText} `
      + `and may not work.${replaces} It will be staged — hidden from players until you set it to `
      + `Available.${runningDesc}`,
      'Install Anyways')) return;

    if (await postJson(`/admin/api/marketplace/install/${encodeURIComponent(entry.id)}`,
      { version: version || null, sourceId: entry.sourceId || null, mode })) refreshJobs();
    return;
  }

  if (entry.activeLobbies > 0 && !await confirmAction(
    `${decided.label} ${name}? ${describeMode(mode, entry.activeLobbies)}`, decided.label)) return;

  if (await postJson(`/admin/api/marketplace/install/${encodeURIComponent(entry.id)}`,
    { version: version || null, sourceId: entry.sourceId || null, mode })) refreshJobs();
}

function describeMode(mode, running) {
  if (!running) return 'Nobody is playing it right now, so it applies immediately.';
  switch (mode) {
    case 'force': return `Its ${running} running lobby/lobbies will be CLOSED first.`;
    case 'auto': return `It has ${running} running lobby/lobbies, so nothing will happen until they end.`;
    default: return `New lobbies will be refused, and it applies once the ${running} running one(s) finish.`;
  }
}

async function uninstallGame(entry) {
  const running = entry.activeLobbies;
  const warning = pluginRestoreWarning(entry);
  if (!await confirmAction(
    `Uninstall ${entry.name || entry.id}? Its files, its cached assets and any retained versions are `
    + `deleted from disk${running > 0 ? `, and its ${running} running lobby/lobbies are closed` : ''}.`,
    'Uninstall',
    {
      warning,
      onExport: () => exportGame(entry.id),
    })) return;
  if (await postJson(`/admin/api/packages/${encodeURIComponent(entry.id)}/uninstall`, {})) refreshJobs();
}

// The per-card pending-job row: when a package operation is running against a game, its card
// shows the live phase, progress and the cancel button inline. (The old standalone Operations
// list is gone — outcomes surface as notifications — but progress and cancel belong to the card
// being operated on, so this stays.)
function jobRow(job, { compact = false } = {}) {
  const row = document.createElement('div');
  row.className = 'job-row';
  row.dataset.job = job.jobId;
  if (job.status === 'succeeded') row.classList.add('job-ok');
  if (job.status === 'failed') row.classList.add('job-failed');

  if (!compact) {
    const title = document.createElement('span');
    title.className = 'job-title';
    const versions = job.fromVersion || job.toVersion
      ? ` ${formatVersion(job.fromVersion)} → ${formatVersion(job.toVersion)}`
      : '';
    title.textContent = `${job.kind} · ${job.gameName || job.gameId}${versions}`;
    row.appendChild(title);
  }

  const phase = document.createElement('span');
  phase.className = 'job-phase';
  phase.textContent = job.error ? `${job.phase} ${job.error}` : job.phase;
  row.appendChild(phase);

  const { percent, label } = jobProgress(job);
  if (!job.terminal) {
    const bar = document.createElement('div');
    bar.className = 'job-bar';
    const fill = document.createElement('div');
    // Null percent means the total is unknown — render indeterminate rather than a confident 0%.
    fill.className = percent === null ? 'job-bar-fill job-bar-indeterminate' : 'job-bar-fill';
    if (percent !== null) fill.style.width = `${percent.toFixed(0)}%`;
    bar.appendChild(fill);
    row.appendChild(bar);
  }
  if (label) {
    const meta = document.createElement('span');
    meta.className = 'job-meta';
    meta.textContent = label;
    row.appendChild(meta);
  }

  if (job.cancellable) {
    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'btn btn-secondary btn-small job-cancel';
    cancel.textContent = 'Cancel';
    cancel.onclick = async () => {
      if (await postJson(`/admin/api/packages/jobs/${encodeURIComponent(job.jobId)}/cancel`, {})) {
        refreshJobs();
      }
    };
    row.appendChild(cancel);
  }
  return row;
}

// ── Upload ────────────────────────────────────────────────────────────────────

function openUpload() {
  el('upload-error').classList.add('hidden');
  el('upload-progress').classList.add('hidden');
  el('upload-file').value = '';
  uploadFile = null;
  el('upload-name').textContent = 'Drop a .kbg here, or click to choose one';
  el('upload-abort').classList.add('hidden');
  el('upload-submit').disabled = false;

  const mode = el('upload-mode');
  mode.textContent = '';
  for (const option of UPDATE_MODES) {
    const opt = document.createElement('option');
    opt.value = option.value;
    opt.textContent = option.label;
    opt.title = option.hint;
    mode.appendChild(opt);
  }
  el('upload-backdrop').classList.remove('hidden');
}

function closeUpload() {
  if (uploadXhr) uploadXhr.abort();
  el('upload-backdrop').classList.add('hidden');
}

function showUploadError(message) {
  const error = el('upload-error');
  error.textContent = message;
  error.classList.remove('hidden');
}

function startUpload() {
  const file = uploadFile;
  const guard = uploadGuard(file, { maxBytes: catalogData?.maxUploadBytes ?? 0 });
  if (!guard.ok) {
    // Inline, not a notification: the operator is looking at this modal and has to change the input.
    showUploadError(guard.error);
    return;
  }

  el('upload-error').classList.add('hidden');
  el('upload-submit').disabled = true;
  el('upload-abort').classList.remove('hidden');
  el('upload-progress').classList.remove('hidden');
  el('upload-progress-fill').style.width = '0%';

  // XMLHttpRequest, deliberately, in a file that otherwise uses fetch everywhere: fetch has no
  // upload-progress event (a streaming request body needs HTTP/2 plus duplex:'half' and still reports
  // nothing). A .kbg runs to hundreds of megabytes, and an upload with no progress reads as hung — so
  // the operator clicks again and starts a SECOND one. Do not "fix" this back to fetch.
  const xhr = new XMLHttpRequest();
  uploadXhr = xhr;
  const mode = el('upload-mode').value;
  xhr.open('POST', `/admin/api/packages/upload?mode=${encodeURIComponent(mode)}`
    + `&filename=${encodeURIComponent(file.name)}`);
  xhr.setRequestHeader('Content-Type', 'application/octet-stream');

  xhr.upload.onprogress = (e) => {
    if (!e.lengthComputable) return;
    el('upload-progress-fill').style.width = `${((e.loaded / e.total) * 100).toFixed(0)}%`;
  };
  xhr.onload = () => {
    uploadXhr = null;
    el('upload-submit').disabled = false;
    el('upload-abort').classList.add('hidden');
    // Close the dialog BEFORE the login form goes up: handleUnauthorized swaps the page over, and the
    // upload backdrop would otherwise sit on top of it with nothing to dismiss it.
    if (xhr.status === 401) {
      el('upload-backdrop').classList.add('hidden');
      handleUnauthorized();
      return;
    }

    let body = null;
    try { body = JSON.parse(xhr.responseText); } catch { /* a non-JSON error page */ }
    if (xhr.status >= 200 && xhr.status < 300 && body?.success) {
      el('upload-backdrop').classList.add('hidden');
      notify(body.detail || 'Package accepted.', 'success');
      // Everything after this point happens inside the JOB — a bad archive, an id collision, a full
      // disk. The request is over; the outcome arrives as a notification.
      refreshJobs();
      return;
    }
    showUploadError(body?.error || `The upload was refused (${xhr.status}).`);
  };
  xhr.onerror = () => {
    uploadXhr = null;
    el('upload-submit').disabled = false;
    el('upload-abort').classList.add('hidden');
    showUploadError('The upload could not be sent. Check the connection and try again.');
  };
  xhr.onabort = () => {
    uploadXhr = null;
    el('upload-submit').disabled = false;
    el('upload-abort').classList.add('hidden');
    el('upload-progress').classList.add('hidden');
  };
  xhr.send(file);
}

// The picked file is held here rather than pushed back into the input's `files`, which is read-only
// except via a DataTransfer — a hoop worth avoiding when the drop and the picker can just agree on one
// variable.
function pickUploadFile(file) {
  if (!file) return;
  uploadFile = file;
  el('upload-name').textContent = `${file.name} (${formatBytes(file.size)})`;
  el('upload-error').classList.add('hidden');
}

// ── Sources ───────────────────────────────────────────────────────────────────

function openSources() {
  el('mkt-settings-error').classList.add('hidden');
  renderSources();
  el('mkt-settings-backdrop').classList.remove('hidden');
}

function renderSources() {
  const host = el('mkt-sources');
  host.textContent = '';
  el('mkt-settings-note').textContent = catalogData?.maxSources
    ? `The official marketplace is built in. Up to ${catalogData.maxSources} more can be registered.`
    : 'The official marketplace is built in.';

  for (const source of catalogData?.sources || []) {
    const row = document.createElement('div');
    row.className = 'source-row';
    row.dataset.id = source.id;

    const name = document.createElement('span');
    name.className = 'source-name';
    name.textContent = source.name || source.id;
    const url = document.createElement('span');
    url.className = 'source-url';
    url.textContent = source.catalogUrl;
    row.append(name, url);

    const count = document.createElement('span');
    count.className = 'badge badge-muted';
    // A disabled source is never fetched, so "0 game(s)" would read as an empty catalog rather than as
    // one nobody asked for.
    if (source.enabled === false) count.textContent = 'disabled';
    else count.textContent = source.error ? 'unreachable' : `${source.entries} game(s)`;
    if (source.error) count.title = source.error;
    row.appendChild(count);

    // Every source can be switched off without losing its configuration — and for the built-in one this
    // is the ONLY control, since it can't be removed.
    const toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'btn btn-small source-toggle';
    toggle.textContent = source.enabled === false ? 'Enable' : 'Disable';
      toggle.onclick = async () => {
        const url = `/admin/api/marketplace/sources/${encodeURIComponent(source.id)}/enabled`;
        if (await postJson(url, { enabled: source.enabled === false })) {
          refreshCatalog({ refresh: true, render: true });
        }
      };
    row.appendChild(toggle);

    if (!source.builtIn) {
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'btn btn-danger btn-small source-remove';
      remove.textContent = 'Remove';
      remove.onclick = async () => {
        if (await postJson(`/admin/api/marketplace/sources/${encodeURIComponent(source.id)}/delete`, {})) {
          refreshCatalog({ refresh: true, render: true });
        }
      };
      row.appendChild(remove);
    }
    host.appendChild(row);
  }
}

async function addSource() {
  const body = {
    id: el('mkt-source-id').value.trim(),
    name: el('mkt-source-name').value.trim(),
    catalogUrl: el('mkt-source-url').value.trim(),
    downloadBaseUrl: el('mkt-source-download').value.trim() || 'https://github.com',
  };
  // The URL rule is NOT re-implemented here: it lives in MarketplaceClient, and a second copy in JS is
  // exactly the drift this codebase avoids. The server's message is what the operator sees — shown beside
  // the form it rejected, since that is where the field they have to fix is.
  if (await postJson('/admin/api/marketplace/sources', body, { errorEl: el('mkt-settings-error') })) {
    for (const id of ['mkt-source-id', 'mkt-source-name', 'mkt-source-url', 'mkt-source-download']) {
      el(id).value = '';
    }
    await refreshCatalog({ refresh: true, render: true });
    renderSources();
  }
}

// ── Platform settings ─────────────────────────────────────────────────────────

async function refreshPlatform() {
  const limits = await getJson('/admin/api/limits');
  if (limits) renderLimits(limits);
  // 409 when KnockBox:MarketplaceEnabled=false, which getJson reports as null — the card then says so
  // rather than showing a schedule nothing would ever act on.
  renderSchedule(await getJson('/admin/api/updates/schedule'));
  const announcement = await getJson('/admin/api/announcement');
  if (announcement) renderAnnouncement(announcement);
  const webhooks = await getJson('/admin/api/webhooks');
  if (webhooks) renderWebhooks(webhooks);
  const codes = await getJson('/admin/api/room-codes');
  if (codes) {
    codesData = codes;
    // The draft starts as whatever is saved. Chips are added and removed locally and posted as one list,
    // so a half-finished edit can be abandoned by leaving the tab — no half-applied blocklist.
    codesDraft = { words: [...(codes.words || [])], patterns: [...(codes.patterns || [])] };
    renderRoomCodes();
  }

  // If a setting was requested to scroll into view while data was fetching,
  // re-align after the DOM elements have rendered to prevent height-shift cutoffs
  if (pendingScrollSettingId) {
    const targetEl = el(pendingScrollSettingId);
    if (targetEl && typeof targetEl.scrollIntoView === 'function') {
      targetEl.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  }
}

/**
 * Draws the limits form. Rows are built from LIMIT_FIELDS rather than written out in index.html: eight
 * fields x (label + input + hint + default value) is a lot of markup to keep in step with the server's
 * record by hand, and the table is already the thing a test pins against it.
 *
 * An overridden field shows its number; a field nobody has touched shows an empty box with its default as
 * the placeholder. That is the whole UI for "revert": clear the box and save.
 */
function renderLimits(data) {
  limitsData = data;
  const host = el('limits-fields');
  const focused = document.activeElement?.dataset?.limitKey || null;
  const focusedScale = document.activeElement?.dataset?.limitScaleKey || null;
  const kept = new Map(
    [...host.querySelectorAll('input[data-limit-key]')].map((input) => [input.dataset.limitKey, input.value]));
  const keptScales = new Map(
    [...host.querySelectorAll('select[data-limit-scale-key]')].map((sel) => [sel.dataset.limitScaleKey, sel.value]));
  host.innerHTML = '';

  const overridden = new Set(data.overridden || []);
  for (const field of LIMIT_FIELDS) {
    const row = document.createElement('div');
    row.className = 'field-row';

    const label = document.createElement('label');
    label.className = 'limit-label';
    label.textContent = field.label;
    label.htmlFor = `limit-${field.key}`;

    if (field.dataType === 'bytes') {
      const group = document.createElement('div');
      group.className = 'byte-input-group filter-narrow';

      const input = document.createElement('input');
      input.type = 'text';
      input.inputMode = 'numeric';
      input.className = 'text-input byte-input';
      input.id = `limit-${field.key}`;
      input.dataset.limitKey = field.key;
      const defaultDisplay = formatByteLimit(data.defaults?.[field.key]);
      input.placeholder = `Default: ${defaultDisplay}`;
      input.title = field.hint;

      const select = document.createElement('select');
      select.className = 'text-input byte-scale-select';
      select.id = `limit-${field.key}-scale`;
      select.dataset.limitScaleKey = field.key;
      select.title = 'Unit scaling';
      for (const unit of BYTE_UNITS) {
        const opt = document.createElement('option');
        opt.value = unit;
        opt.textContent = unit;
        select.appendChild(opt);
      }

      if (focused === field.key || focusedScale === field.key) {
        input.value = kept.get(field.key) ?? '';
        select.value = keptScales.get(field.key) ?? 'BYTE';
      } else if (overridden.has(field.key)) {
        const split = splitBytes(data.effective?.[field.key]);
        input.value = split.value === '' ? '' : String(split.value);
        select.value = split.unit;
      } else {
        input.value = '';
        select.value = 'BYTE';
      }

      input.addEventListener('keydown', (e) => {
        if (['Backspace', 'Delete', 'ArrowLeft', 'ArrowRight', 'Tab', 'Home', 'End'].includes(e.key)) return;
        if (e.ctrlKey || e.metaKey) return;
        if (!/^[0-9]$/.test(e.key)) {
          e.preventDefault();
        }
      });
      input.addEventListener('input', () => {
        input.value = input.value.replace(/[^0-9]/g, '');
      });

      group.append(input, select);

      const hint = document.createElement('span');
      hint.className = 'limit-hint';
      hint.textContent = overridden.has(field.key)
        ? `Overridden — the default is ${defaultDisplay}`
        : field.hint;

      row.append(label, group, hint);
      host.appendChild(row);
    } else {
      const input = document.createElement('input');
      input.type = 'text';
      input.inputMode = field.integer ? 'numeric' : 'decimal';
      input.className = 'text-input filter-narrow';
      input.id = `limit-${field.key}`;
      input.dataset.limitKey = field.key;
      input.placeholder = `Default: ${data.defaults?.[field.key] ?? '--'}`;
      input.title = field.hint;
      // Don't fight the operator's cursor — the same rule the maintenance message follows. On entry, or
      // after a save, the server's value wins; a field being edited keeps what is in it.
      input.value = focused === field.key
        ? kept.get(field.key) ?? ''
        : overridden.has(field.key) ? String(data.effective?.[field.key] ?? '') : '';

      const hint = document.createElement('span');
      hint.className = 'limit-hint';
      hint.textContent = overridden.has(field.key)
        ? `Overridden — the default is ${data.defaults?.[field.key] ?? '--'}`
        : field.hint;

      row.append(label, input, hint);
      host.appendChild(row);
    }
  }

  const anyOverridden = (data.overridden || []).length > 0;
  el('limits-badge').hidden = !anyOverridden;
  el('limits-save').disabled = false;
  el('limits-reset').disabled = !anyOverridden;
  el('limits-note').textContent = anyOverridden
    ? `${data.overridden.length} of ${LIMIT_FIELDS.length} limits are overridden. `
      + `${formatCount(data.activeLobbies)} lobbies and ${formatCount(data.connectedPlayers)} players right now.`
    : 'Every limit is at its default.';
  // Bytes actually held, against the aggregate cap. The only question anyone asks about a server-wide
  // quota is whether it is close to biting, and an upload refused with 507 reaches an operator as "a
  // player says their map will not load" -- which is not a clue.
  if (data.blobsEnabled) {
    const cap = data.effective?.blobTotalQuotaBytes;
    el('limits-note').textContent += ` Blobs: ${formatBytes(data.blobBytesUsed || 0)} held`
      + `${cap > 0 ? ` of ${formatBytes(cap)}` : ' (no server-wide cap)'}.`;
  }

  const startupBody = el('limits-startup-body');
  startupBody.innerHTML = '';
  for (const field of STARTUP_LIMITS) {
    const row = document.createElement('tr');
    appendCells(row, [field.label, String(data[field.key] ?? '--')]);
    startupBody.appendChild(row);
  }
}

async function saveLimits() {
  const raw = {};
  const scales = {};
  for (const input of document.querySelectorAll('#limits-fields input[data-limit-key]')) {
    raw[input.dataset.limitKey] = input.value;
  }
  for (const select of document.querySelectorAll('#limits-fields select[data-limit-scale-key]')) {
    scales[select.dataset.limitScaleKey] = select.value;
  }
  const checked = validateLimits(raw, LIMIT_FIELDS, scales);
  if (!checked.ok) { notify(checked.error, 'error'); return; }

  // Tightening a limit is not destructive, but it is felt immediately by everyone connected, so the two
  // that can refuse a player outright get a confirmation naming what is running right now.
  const capping = checked.values.maxLobbies !== null || checked.values.maxLobbiesPerGame !== null
    || checked.values.authorityMaxLobbies !== null;
  if (capping && !noLimitOverrides(checked.values) && !await confirmAction(
    `Apply these limits now? They take effect for connections that are already open. `
    + `${formatCount(limitsData?.activeLobbies)} lobbies are running; a cap below that number lets them `
    + `finish but starts no new ones until the count falls under it.`, 'Apply Limits')) return;

  if (await postJson('/admin/api/limits', checked.values)) refreshPlatform();
}

/**
 * Draws the update-schedule card. Day and hour are shown whatever the cadence but disabled when it
 * ignores them, rather than hidden: a control that vanishes makes an operator wonder whether the value
 * went with it, and switching weekly → daily → weekly has to come back to the day they picked.
 */
function renderSchedule(data) {
  const cadence = el('schedule-cadence');
  const day = el('schedule-day');
  const hour = el('schedule-hour');

  // Built here rather than in index.html: 24 <option>s of markup to say "0..23", each also carrying the
  // reader's local equivalent (see hourOptionLabel).
  if (!hour.options.length) {
    for (let h = 0; h < 24; h++) {
      const option = document.createElement('option');
      option.value = String(h);
      option.textContent = hourOptionLabel(h);
      hour.appendChild(option);
    }
  }

  const available = !!data;
  for (const control of [cadence, day, hour, el('schedule-save'), el('schedule-reset')]) {
    control.disabled = !available;
  }
  el('schedule-badge').hidden = !data?.overridden;

  if (!available) {
    el('schedule-note').textContent =
      'The marketplace is switched off (KnockBox:MarketplaceEnabled=false), so nothing is checked on a '
      + 'schedule.';
    return;
  }

  // Don't fight the operator's cursor — the same rule the limits and announcement fields follow.
  if (document.activeElement !== cadence) cadence.value = data.cadence || 'daily';
  if (document.activeElement !== day) day.value = data.dayOfWeek || 'sunday';
  if (document.activeElement !== hour) hour.value = String(data.hourUtc ?? 3);

  applyScheduleCadence();
  el('schedule-note').textContent = scheduleNote(data);
}

/** Greys out the fields the chosen cadence does not use. Driven by the select, not by the last save. */
function applyScheduleCadence() {
  const cadence = el('schedule-cadence').value;
  el('schedule-day').disabled = cadence !== 'weekly';
  el('schedule-hour').disabled = cadence !== 'weekly' && cadence !== 'daily';
}

async function saveSchedule(revert = false) {
  const body = revert ? {} : {
    cadence: el('schedule-cadence').value,
    dayOfWeek: el('schedule-day').value,
    hourUtc: Number(el('schedule-hour').value),
  };
  if (await postJson('/admin/api/updates/schedule', body)) refreshPlatform();
}

function renderAnnouncement(data) {
  announcementData = data;
  const live = !!data.text;

  const badge = el('announce-badge');
  badge.textContent = live ? 'Live' : 'None';
  badge.className = `badge ${live ? 'badge-warning' : 'badge-muted'}`;

  // Don't fight the operator's cursor — same rule as the maintenance message and the limit fields.
  const text = el('announce-text');
  if (document.activeElement !== text) text.value = data.text || '';
  text.maxLength = data.maxLength || 200;

  const severity = el('announce-severity');
  if (document.activeElement !== severity) severity.value = data.severity === 'warning' ? 'warning' : 'info';

  // The scope selector is built from the games the server reported, so it can't offer one that would be
  // refused as unknown.
  const scope = el('announce-game');
  const wanted = document.activeElement === scope ? scope.value : (data.gameId || '');
  scope.innerHTML = '';
  const all = document.createElement('option');
  all.value = '';
  all.textContent = 'All games';
  scope.appendChild(all);
  for (const game of data.games || []) {
    const option = document.createElement('option');
    option.value = game.id;
    option.textContent = game.name;
    scope.appendChild(option);
  }
  scope.value = [...scope.options].some((o) => o.value === wanted) ? wanted : '';

  el('announce-clear').disabled = !live;
  el('announce-note').textContent = live
    ? `Posted ${formatClock(data.postedAt)}. ${formatCount(data.connectedPlayers)} player(s) connected now.`
    : `No announcement. ${formatCount(data.connectedPlayers)} player(s) connected — they would see one immediately.`;
}

async function postAnnouncement() {
  const text = el('announce-text').value.trim();
  if (!text) { notify('Enter the message players should see.', 'error'); return; }

  if (await postJson('/admin/api/announcement', {
    text,
    severity: el('announce-severity').value,
    gameId: el('announce-game').value || null,
  })) refreshPlatform();
}

async function clearAnnouncement() {
  if (!announcementData?.text) return;
  if (!await confirmAction(
    'Take the banner down for every player? Anyone reading it now loses it immediately.',
    'Clear Banner')) return;
  if (await postJson('/admin/api/announcement/delete', {})) refreshPlatform();
}

function renderWebhooks(data) {
  webhookData = data;
  const endpoints = data.endpoints || [];

  el('hook-badge').textContent = `${endpoints.length} / ${data.maxEndpoints ?? '--'}`;
  el('hook-empty').classList.toggle('hidden', endpoints.length > 0);
  el('hook-table').classList.toggle('hidden', endpoints.length === 0);
  el('hook-note').textContent = data.enabled
    ? `Delivered ${formatCount(data.delivered)}, failed ${formatCount(data.failed)}, dropped `
      + `${formatCount(data.dropped)}, error alerts suppressed ${formatCount(data.suppressed)} `
      + `(cap ${data.errorsPerMinute}/min, ${data.timeoutSeconds}s timeout). One attempt per event, no retries.`
    : 'Webhooks are switched off (KnockBox:WebhooksEnabled=false). Saved endpoints are listed but nothing is sent.';

  // The checkbox row is rebuilt from the server's own event list, so a new event kind needs no markup here.
  const eventsHost = el('hook-events');
  if (!eventsHost.dataset.built) {
    for (const value of data.knownEvents || []) {
      const label = document.createElement('label');
      label.className = 'checkbox-label';
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.value = value;
      box.dataset.hookEvent = value;
      const text = document.createElement('span');
      text.textContent = webhookEventLabel(value);
      label.title = WEBHOOK_EVENTS.find((e) => e.value === value)?.hint || '';
      label.append(box, text);
      eventsHost.appendChild(label);
    }
    eventsHost.dataset.built = '1';
  }

  const body = el('hook-body');
  body.innerHTML = '';
  for (const endpoint of endpoints) {
    const row = document.createElement('tr');
    row.dataset.hookId = endpoint.id;

    const name = document.createElement('td');
    const strong = document.createElement('strong');
    strong.textContent = endpoint.name || endpoint.id;
    const url = document.createElement('div');
    url.className = 'source-url';
    // The URL is a bearer credential (anyone with a Discord webhook URL can post to that channel), so only
    // its origin is shown — enough to tell two endpoints apart, without putting the secret on screen.
    url.textContent = originOf(endpoint.url);
    name.append(strong, url);

    const events = document.createElement('td');
    events.textContent = (endpoint.events || []).length === 0
      ? 'All events'
      : endpoint.events.map(webhookEventLabel).join(', ');

    const last = document.createElement('td');
    const delivery = webhookLastDelivery(endpoint);
    last.textContent = delivery ? `${delivery} · ${formatClock(endpoint.lastAt)}` : 'Never sent';
    if (delivery && endpoint.lastOk === false) row.classList.add('row-warn');

    const actions = document.createElement('td');
    actions.className = 'col-actions';
    const test = document.createElement('button');
    test.className = 'btn btn-secondary btn-small';
    test.textContent = 'Test';
    test.addEventListener('click', () => testWebhook(endpoint.id));
    const remove = document.createElement('button');
    remove.className = 'btn btn-danger btn-small';
    remove.textContent = 'Remove';
    remove.addEventListener('click', () => removeWebhook(endpoint));
    actions.append(test, remove);

    if (!endpoint.enabled) {
      const disabled = document.createElement('span');
      disabled.className = 'badge badge-muted';
      disabled.textContent = 'Disabled';
      name.appendChild(disabled);
    }

    row.append(name, events, last, actions);
    body.appendChild(row);
  }
}

/** Just the origin of a URL — see the note in renderWebhooks about why the path is not shown. */
function originOf(url) {
  try { return new URL(url).origin; } catch { return url || ''; }
}

async function addWebhook() {
  const checked = checkWebhook({ id: el('hook-id').value, url: el('hook-url').value });
  if (!checked.ok) { notify(checked.error, 'error'); return; }

  const events = [...document.querySelectorAll('#hook-events input[data-hook-event]')]
    .filter((box) => box.checked)
    .map((box) => box.value);

  if (await postJson('/admin/api/webhooks', {
    id: checked.id,
    name: el('hook-name').value.trim() || checked.id,
    url: checked.url,
    events,
  })) {
    el('hook-id').value = '';
    el('hook-name').value = '';
    el('hook-url').value = '';
    refreshPlatform();
  }
}

async function removeWebhook(endpoint) {
  if (!await confirmAction(
    `Remove '${endpoint.name || endpoint.id}'? Events stop being posted there immediately. `
    + 'The URL is not stored anywhere else, so you would have to paste it again.',
    'Remove Endpoint')) return;
  if (await postJson(`/admin/api/webhooks/${encodeURIComponent(endpoint.id)}/delete`, {})) refreshPlatform();
}

async function testWebhook(id) {
  // Awaited by the server through the real delivery path, so the notification is the actual answer rather than
  // "queued" — which is what an operator clicking Test wants to know.
  if (await postJson(`/admin/api/webhooks/${encodeURIComponent(id)}/test`, {})) refreshPlatform();
}

function renderRoomCodes() {
  const alphabet = codesData?.alphabet || CODE_ALPHABET;
  const unreachable = new Set(codesData?.unreachable || []);

  for (const [host, entries, pattern] of [
    [el('code-words'), codesDraft.words, false],
    [el('code-patterns'), codesDraft.patterns, true],
  ]) {
    host.innerHTML = '';
    for (const entry of entries) {
      const chip = document.createElement('span');
      const flagged = unreachable.has(entry) || checkCodeEntry(entry, { pattern, alphabet }).unreachable;
      chip.className = `member-chip ${flagged ? 'chip-unreachable' : ''}`;
      if (flagged) {
        // Distinct characters: "has no O" reads as an explanation, "has no O, O" reads as a bug.
        const missing = [...new Set([...entry].filter((c) => c !== '?' && c !== '*' && !alphabet.includes(c)))];
        chip.title = `The code alphabet has no ${missing.join(', ')}, so this can never match a generated code.`;
      }

      const text = document.createElement('span');
      text.textContent = entry;
      const remove = document.createElement('button');
      remove.className = 'chip-action';
      remove.textContent = '×';
      remove.title = `Remove ${entry}`;
      remove.addEventListener('click', () => {
        const list = pattern ? codesDraft.patterns : codesDraft.words;
        list.splice(list.indexOf(entry), 1);
        renderRoomCodes();
      });
      chip.append(text, remove);
      host.appendChild(chip);
    }
    if (entries.length === 0) {
      const none = document.createElement('span');
      none.className = 'limit-hint';
      none.textContent = pattern ? 'No patterns blocked.' : 'No words blocked.';
      host.appendChild(none);
    }
  }

  const blocked = codesData?.blocked ?? 0;
  const share = blockedShare(blocked, codesData?.codeSpace);
  const badge = el('codes-badge');
  const total = codesDraft.words.length + codesDraft.patterns.length;
  badge.hidden = total === 0;
  badge.textContent = `${total} / ${codesData?.maxEntries ?? '--'}`;
  badge.className = 'badge badge-muted';

  const saved = (codesData?.words?.length ?? 0) + (codesData?.patterns?.length ?? 0);
  const dirty = total !== saved
    || codesDraft.words.some((w) => !(codesData?.words || []).includes(w))
    || codesDraft.patterns.some((p) => !(codesData?.patterns || []).includes(p));
  el('codes-note').textContent = dirty
    ? 'Unsaved changes. Nothing is blocked until you save.'
    : blocked > 0
      ? `Blocking ${formatCount(blocked)} of ${formatCount(codesData?.codeSpace)} possible codes`
        + `${share === null ? '' : ` (${share.toFixed(1)}%)`}. The limit is `
        + `${codesData?.maxBlockedPercent ?? '--'}%.`
      : 'No codes are blocked.';
}

function addRoomCode(pattern) {
  const input = el(pattern ? 'code-pattern' : 'code-word');
  const checked = checkCodeEntry(input.value, { pattern, alphabet: codesData?.alphabet });
  if (!checked.ok) { notify(checked.error, 'error'); return; }

  const list = pattern ? codesDraft.patterns : codesDraft.words;
  if (list.includes(checked.value)) { notify(`${checked.value} is already blocked.`, 'warning'); return; }
  list.push(checked.value);
  input.value = '';
  // Said at the moment of typing, where it can still be changed, rather than as a footnote after saving.
  if (checked.unreachable) {
    notify(`${checked.value} can never be generated — the code alphabet has no O, 0, I or 1.`, 'warning');
  }
  renderRoomCodes();
}

async function saveRoomCodes() {
  if (await postJson('/admin/api/room-codes', codesDraft)) refreshPlatform();
}

async function clearRoomCodes() {
  if (!(codesDraft.words.length || codesDraft.patterns.length)) return;
  if (!await confirmAction(
    'Remove every blocked word and pattern? The generator will be able to produce any code again.',
    'Clear All')) return;

  // POST the empty list, and only adopt it as the draft once the server took it. Emptying the draft
  // first left a rejected clear showing every chip on screen (saveRoomCodes only re-renders on success)
  // over a draft that was already empty — so the operator's next Save deleted the whole blocklist
  // without asking, which is precisely what they had just been told did not happen.
  if (await postJson('/admin/api/room-codes', { words: [], patterns: [] })) {
    codesDraft = { words: [], patterns: [] };
    refreshPlatform();
  }
}

async function revertLimits() {
  if (!await confirmAction(
    'Drop every limit override and go back to the defaults? Applies immediately.',
    'Revert All')) return;
  const cleared = {};
  for (const field of LIMIT_FIELDS) cleared[field.key] = null;
  if (await postJson('/admin/api/limits', cleared)) refreshPlatform();
}

function wire() {
  el('setup-form').addEventListener('submit', onSetupSubmit);
  el('login-form').addEventListener('submit', onLoginSubmit);
  el('logout-btn').addEventListener('click', onLogout);
  el('sidebar-toggle')?.addEventListener('click', toggleSidebarCollapsed);

  if (getStoredSidebarCollapsed()) {
    setSidebarCollapsed(true, { persist: false });
  }

  // Tree View & Navigation
  for (const toggleBtn of document.querySelectorAll('[data-group-toggle]')) {
    toggleBtn.addEventListener('click', (e) => {
      e.preventDefault();
      toggleGroup(toggleBtn.dataset.groupToggle);
    });
  }

  // Top-bar Tab Buttons
  for (const tabBtn of document.querySelectorAll('.top-tab-btn')) {
    tabBtn.addEventListener('click', () => {
      selectTopTab(tabBtn.dataset.tab);
    });
  }

  // Cross-tab deep links to settings (e.g. data-goto-setting="setting-schedule")
  document.addEventListener('click', (e) => {
    const jumpLink = e.target.closest('[data-goto-setting]');
    if (jumpLink) {
      e.preventDefault();
      navigateToSetting(jumpLink.dataset.gotoSetting);
    }
  });

  for (const item of document.querySelectorAll('.tree-item')) {
    item.addEventListener('click', (e) => {
      e.preventDefault();
      const settingId = item.dataset.settingId;
      if (settingId) {
        selectSetting(settingId, { replaceHash: true, scroll: true });
      }
      closeMobileSidebar();
    });
  }

  for (const button of document.querySelectorAll('.nav-item:not(.tree-item)')) {
    button.addEventListener('click', (e) => {
      e.preventDefault();
      const settingId = settingFromHash(button.dataset.tab);
      if (settingId) {
        selectSetting(settingId, { replaceHash: true, scroll: true });
      }
      closeMobileSidebar();
    });
  }

  // Click outside to close mobile sidebar dropdown
  document.addEventListener('click', (e) => {
    const sidebarNav = el('sidebar-nav');
    if (sidebarNav?.classList.contains('mobile-open') && !sidebarNav.contains(e.target)) {
      closeMobileSidebar();
    }
  });

  // Escape key closes mobile sidebar dropdown
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && el('sidebar-nav')?.classList.contains('mobile-open')) {
      closeMobileSidebar();
    }
  });

  // Close mobile dropdown when expanding to desktop width
  window.addEventListener('resize', () => {
    if (window.innerWidth > 860 && el('sidebar-nav')?.classList.contains('mobile-open')) {
      closeMobileSidebar();
    }
  }, { passive: true });

  // Settings Search
  const searchInput = el('settings-search-input');
  if (searchInput) {
    searchInput.addEventListener('input', (e) => {
      applySettingsSearch(e.target.value);
    });
  }
  el('settings-search-clear')?.addEventListener('click', clearSettingsSearch);
  el('search-empty-clear')?.addEventListener('click', clearSettingsSearch);

  // Sidebar scroll indicators (for collapsed state)
  const tree = el('sidebar-tree');
  if (tree) {
    tree.addEventListener('scroll', updateSidebarScrollIndicators, { passive: true });
  }
  el('sidebar-scroll-up')?.addEventListener('click', () => {
    tree?.scrollBy({ top: -80, behavior: 'smooth' });
  });
  el('sidebar-scroll-down')?.addEventListener('click', () => {
    tree?.scrollBy({ top: 80, behavior: 'smooth' });
  });
  updateSidebarScrollIndicators();

  const contentScroll = el('admin-content-scroll');
  if (contentScroll) {
    contentScroll.addEventListener('scroll', updateScrollspy, { passive: true });
  }
  const adminContainer = document.querySelector('.admin-container');
  if (adminContainer) {
    adminContainer.addEventListener('scroll', updateScrollspy, { passive: true });
  }
  window.addEventListener('scroll', updateScrollspy, { passive: true });
  window.addEventListener('hashchange', () => selectSetting(settingFromHash(location.hash), { replaceHash: false, scroll: true }));

  el('maintenance-toggle').addEventListener('click', async () => {
    const turningOn = el('maintenance-toggle').dataset.enabled !== 'true';
    if (turningOn && !await confirmAction(
      'Turn on maintenance mode? No player will be able to start a new game on any title until you turn '
      + 'it off. Sessions already running are unaffected.', 'Turn On')) return;
    if (await postJson('/admin/api/maintenance',
      { enabled: turningOn, message: el('maintenance-message').value })) refreshOverview();
  });

  el('lobby-filter-game').addEventListener('input', renderLobbies);
  el('lobby-filter-code').addEventListener('input', renderLobbies);
  el('lobby-filter-status').addEventListener('change', renderLobbies);
  el('close-all-btn').addEventListener('click', closeAllLobbies);
  el('purge-stale-btn').addEventListener('click', purgeStale);

  el('plugins-filter-q')?.addEventListener('input', renderPlugins);
  el('plugins-filter-source')?.addEventListener('change', renderPlugins);
  el('plugins-sort')?.addEventListener('change', (e) => {
    // Per-tab memory: the choice belongs to the tab it was made on, and returning to that tab
    // restores it (see setPluginTab).
    pluginSort[activePluginTab] = e.target.value;
    renderPlugins();
  });
  el('plugins-stale')?.addEventListener('click', () => refreshPlugins({ refreshCatalogNow: true }));
  for (const btn of document.querySelectorAll('.plugin-tab-btn')) {
    btn.addEventListener('click', () => setPluginTab(btn.dataset.ptab));
    // WAI-APG tabs pattern with automatic activation: arrows move and select, Home/End jump.
    btn.addEventListener('keydown', (e) => {
      const order = [...document.querySelectorAll('.plugin-tab-btn')];
      const at = order.indexOf(btn);
      let next = -1;
      if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = (at + 1) % order.length;
      else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = (at - 1 + order.length) % order.length;
      else if (e.key === 'Home') next = 0;
      else if (e.key === 'End') next = order.length - 1;
      if (next < 0) return;
      e.preventDefault();
      order[next].focus();
      setPluginTab(order[next].dataset.ptab);
    });
  }

  el('plugin-details-close')?.addEventListener('click', () => el('plugin-details-backdrop')?.classList.add('hidden'));
  el('plugin-details-close-x')?.addEventListener('click', () => el('plugin-details-backdrop')?.classList.add('hidden'));
  el('plugin-details-backdrop')?.addEventListener('click', (e) => {
    if (e.target === el('plugin-details-backdrop')) el('plugin-details-backdrop').classList.add('hidden');
  });

  el('game-filter-q')?.addEventListener('input', renderGames);
  el('game-filter-availability')?.addEventListener('change', renderGames);
  el('rescan-btn')?.addEventListener('click', async () => {
    // Give the catalog a beat to republish before re-reading, or the operator sees the pre-rescan list
    // and concludes the button does nothing.
    if (await postJson('/admin/api/games/rescan', {})) setTimeout(refreshPlugins, 800);
  });

  el('mkt-filter-q')?.addEventListener('input', renderMarketplace);
  el('mkt-filter-status')?.addEventListener('change', renderMarketplace);
  el('mkt-filter-source')?.addEventListener('change', renderMarketplace);
  el('mkt-refresh-btn')?.addEventListener('click', () => refreshPlugins({ refreshCatalogNow: true }));
  el('mkt-upload-btn')?.addEventListener('click', openUpload);
  el('mkt-settings-btn')?.addEventListener('click', openSources);
  el('mkt-settings-close')?.addEventListener('click', () => el('mkt-settings-backdrop').classList.add('hidden'));
  el('mkt-source-add')?.addEventListener('click', addSource);

  el('upload-close')?.addEventListener('click', closeUpload);
  el('upload-abort')?.addEventListener('click', () => uploadXhr?.abort());
  el('upload-submit')?.addEventListener('click', startUpload);
  el('upload-drop')?.addEventListener('click', () => el('upload-file').click());
  el('upload-file')?.addEventListener('change', () => pickUploadFile(el('upload-file').files?.[0]));
  el('upload-drop')?.addEventListener('dragover', (e) => {
    e.preventDefault();
    el('upload-drop').classList.add('drop-active');
  });
  el('upload-drop')?.addEventListener('dragleave', () => el('upload-drop').classList.remove('drop-active'));
  el('upload-drop')?.addEventListener('drop', (e) => {
    e.preventDefault();
    el('upload-drop').classList.remove('drop-active');
    pickUploadFile(e.dataTransfer?.files?.[0]);
  });
  // A drop that MISSES the zone would otherwise navigate the portal away to a binary download, losing
  // whatever was in flight. One line, and the classic version of this bug.
  document.addEventListener('dragover', (e) => e.preventDefault());
  document.addEventListener('drop', (e) => e.preventDefault());

  el('limits-save')?.addEventListener('click', saveLimits);
  el('limits-reset')?.addEventListener('click', revertLimits);
  el('limits-refresh')?.addEventListener('click', refreshPlatform);

  el('hook-add')?.addEventListener('click', addWebhook);

  el('schedule-cadence')?.addEventListener('change', applyScheduleCadence);
  el('schedule-save')?.addEventListener('click', () => saveSchedule());
  el('schedule-reset')?.addEventListener('click', () => saveSchedule(true));
  el('schedule-refresh')?.addEventListener('click', refreshPlatform);

  el('announce-post')?.addEventListener('click', postAnnouncement);
  el('announce-clear')?.addEventListener('click', clearAnnouncement);

  el('code-word-add')?.addEventListener('click', () => addRoomCode(false));
  el('code-pattern-add')?.addEventListener('click', () => addRoomCode(true));
  // Enter in the box adds the entry rather than doing nothing: this is a list you build by typing.
  el('code-word')?.addEventListener('keydown', (e) => { if (e.key === 'Enter') addRoomCode(false); });
  el('code-pattern')?.addEventListener('keydown', (e) => { if (e.key === 'Enter') addRoomCode(true); });
  el('codes-save')?.addEventListener('click', saveRoomCodes);
  el('codes-clear')?.addEventListener('click', clearRoomCodes);

  el('log-filter-level')?.addEventListener('change', resetLogStream);
  el('log-filter-category')?.addEventListener('input', resetLogStream);
  el('log-filter-q')?.addEventListener('input', resetLogStream);
  el('log-follow')?.addEventListener('change', () => { if (el('log-follow').checked) refreshLogs(); });
  el('log-files-btn')?.addEventListener('click', openLogFiles);
  el('log-popout-btn')?.addEventListener('click', openTerminalWindow);
  el('files-close')?.addEventListener('click', () => el('files-backdrop').classList.add('hidden'));

  // ── Notifications: bell, drawer, list and details modals ──
  initNotificationStore({ onNew: () => openNotifDrawer() });
  subscribeNotifications(() => {
    refreshNotifBadge();
    if (notifDrawerOpen) {
      renderNotifDrawer();
      armNotifDrawerTimer();
    }
    if (!el('notifications-backdrop')?.classList.contains('hidden')) renderNotifications();
    if (notifDetailId !== null && !el('notification-details-backdrop')?.classList.contains('hidden')) {
      renderNotificationDetails();
    }
  });
  refreshNotifBadge();

  el('notif-bell-btn')?.addEventListener('click', openNotifications);
  // Hover or keyboard focus previews the three newest — the same drawer an arrival opens, so
  // hovering the auto-opened one naturally holds it. Holding only pauses the dismiss timer: the
  // moment hover or focus leaves, the drawer dismisses at once (through its exit animation) rather
  // than starting a second grace period the operator never asked to wait through.
  el('notif-bell-btn')?.addEventListener('mouseenter', openNotifDrawer);
  el('notif-bell-btn')?.addEventListener('focus', openNotifDrawer);
  const drawer = el('notif-drawer');
  drawer?.addEventListener('mouseenter', stopNotifDrawerTimer);
  drawer?.addEventListener('mouseleave', closeNotifDrawer);
  drawer?.addEventListener('focusin', stopNotifDrawerTimer);
  drawer?.addEventListener('focusout', closeNotifDrawer);
  el('notif-drawer-close')?.addEventListener('click', closeNotifDrawer);
  el('notif-drawer-all')?.addEventListener('click', openNotifications);

  el('notifications-close')?.addEventListener('click', closeNotifications);
  el('notifications-close-x')?.addEventListener('click', closeNotifications);
  el('notifications-mark-all')?.addEventListener('click', () => markAllNotificationsRead());
  el('notifications-dismiss-all')?.addEventListener('click', dismissAllNotificationsUI);

  el('notification-details-close')?.addEventListener('click', closeNotificationDetails);
  el('notification-details-close-x')?.addEventListener('click', closeNotificationDetails);
  el('notification-details-toggle')?.addEventListener('click', () => {
    if (notifDetailId === null || hasUnreadEncrypted()) return;
    const current = getNotification(notifDetailId);
    if (current) markNotificationRead(notifDetailId, !current.read);
  });
  el('notification-details-dismiss')?.addEventListener('click', () => {
    if (notifDetailId !== null && !hasUnreadEncrypted()) dismissOneNotification(notifDetailId);
  });

  el('confirm-ok')?.addEventListener('click', () => settleConfirm(true));
  el('confirm-cancel')?.addEventListener('click', () => settleConfirm(false));
  el('confirm-backdrop')?.addEventListener('click', (e) => {
    if (e.target === el('confirm-backdrop')) settleConfirm(false);
  });
  for (const id of ['upload-backdrop', 'mkt-settings-backdrop', 'plugin-details-backdrop']) {
    el(id)?.addEventListener('click', (e) => {
      if (e.target === el(id)) el(id).classList.add('hidden');
    });
  }
  // The notification modals close through their animated close functions, not a bare hide, so a
  // backdrop click dismisses them the same way their buttons do.
  el('notifications-backdrop')?.addEventListener('click', (e) => {
    if (e.target === el('notifications-backdrop')) closeNotifications();
  });
  el('notification-details-backdrop')?.addEventListener('click', (e) => {
    if (e.target === el('notification-details-backdrop')) closeNotificationDetails();
  });
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    if (!el('confirm-backdrop')?.classList.contains('hidden')) settleConfirm(false);
    el('files-backdrop')?.classList.add('hidden');
    el('mkt-settings-backdrop')?.classList.add('hidden');
    el('plugin-details-backdrop')?.classList.add('hidden');
    closeNotificationDetails();
    closeNotifications();
    // Not closeUpload(): Escape must not silently abort a transfer that is halfway through. The Cancel
    // button is the deliberate way out.
    if (!uploadXhr) el('upload-backdrop')?.classList.add('hidden');
  });
}

async function onSetupSubmit(e) {
  e.preventDefault();
  const error = el('setup-error');
  error.classList.add('hidden');

  const password = el('setup-password').value;
  if (password !== el('confirm-password').value) {
    error.textContent = 'Passwords do not match.';
    error.classList.remove('hidden');
    return;
  }

  try {
    const res = await fetch('/admin/api/auth/setup', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password }),
    });
    const data = await res.json();
    if (!res.ok || !data.success) {
      error.textContent = data.error || 'Failed to setup admin password.';
      error.classList.remove('hidden');
      return;
    }
    el('setup-password').value = '';
    el('confirm-password').value = '';
    await checkAuthStatus();
  } catch (err) {
    error.textContent = 'Network error setting up password.';
    error.classList.remove('hidden');
  }
}

async function onLoginSubmit(e) {
  e.preventDefault();
  const error = el('login-error');
  error.classList.add('hidden');

  try {
    const res = await fetch('/admin/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password: el('login-password').value }),
    });
    const data = await res.json();
    if (!res.ok || !data.success) {
      error.textContent = data.error || 'Invalid password.';
      error.classList.remove('hidden');
      return;
    }
    el('login-password').value = '';
    await checkAuthStatus();
  } catch (err) {
    error.textContent = 'Network error during login.';
    error.classList.remove('hidden');
  }
}

async function onLogout() {
  try {
    // The JSON content type is sent for the same reason postJson always sends it: the server's write
    // guard requires it on the auth routes outright, so a plain bodyless POST is refused with 415.
    await fetch('/admin/api/auth/logout', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: '{}',
    });
  } catch (err) {
    console.error('Logout error:', err);
  }
  stopPolling();
  // The store's encryption key is memory-only by design: dropping it here is what makes the stored
  // ciphertext unreadable until the next login re-fetches it. Decrypted items are plaintext
  // regardless of the at-rest form, so memory is dropped too (the stored blob is left intact for
  // the next login; unsaved memory-only items are intentionally discarded). The unload emits, so
  // the badge/list re-render empty behind the login view.
  clearNotificationKey();
  unloadNotificationsForLogout();
  closeNotifDrawer();
  closeNotifications();
  closeNotificationDetails();
  await checkAuthStatus();
}

function applyAdminFavicon() {
  let link = document.head.querySelector('link[rel="icon"]');
  if (!link) {
    link = document.createElement('link');
    link.rel = 'icon';
    link.type = 'image/png';
    document.head.appendChild(link);
  }
  link.href = ADMIN_FAVICON;
}

/** Wires the page and runs the first auth check. Called from index.html. */
export function bootstrap() {
  applyAdminFavicon();
  wire();
  checkAuthStatus();
}
