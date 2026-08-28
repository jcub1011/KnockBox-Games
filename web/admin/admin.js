// Admin portal client logic.
//
// Structure mirrors web/shell.js: pure helpers live in admin-core.js (tested in the Node environment),
// this module owns the DOM, fetch and timers, and nothing runs on import — bootstrap() is exported and
// called from index.html, so the test suite can drive the exported functions without a live poll to
// suppress. Views and tabs are pre-existing markup toggled by class, never rendered from templates; only
// table and list ROWS are built imperatively, always with textContent, because game titles and player
// display names are untrusted input.

import {
  AVAILABILITY, ALL_SETTINGS, CODE_ALPHABET, LIMIT_FIELDS, SETTINGS_GROUPS, STARTUP_LIMITS, TABS,
  TOP_TABS, TAB_MAPPING,
  UPDATE_MODES, UPDATE_POLICIES, WEBHOOK_EVENTS, appendLogEntries, availabilityLabel, blockedShare,
  checkCodeEntry, checkWebhook, cpuPercentBetween, downsample, filterCatalog, filterGames, filterLobbies,
  filterSettings, formatBytes, formatClock, formatCount, formatDuration, formatVersion,
  getStoredSidebarCollapsed, hourOptionLabel, isBusyLifecycle, isTerminalJob, jobProgress,
  lifecycleClass, lifecycleLabel, logLevelClass, logLevelTag, mergeJobs, mergeSamples, sdkBadge,
  noLimitOverrides, pluginStatusClass, pluginStatusHint, pluginStatusLabel, ratePerSecond,
  scheduleNote, seriesCpuPercent, seriesValue, setStoredSidebarCollapsed, settingFromHash,
  sparklinePath, tabFromHash, topTabFromHash, uploadGuard, validateLimits, versionAction, versionOptionValue, versionOptions,
  webhookEventLabel, webhookLastDelivery,
} from './admin-core.js';

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
// jobIds whose terminal outcome has already been toasted, so each finished job raises exactly one —
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
 * POSTs an action and reports the outcome as a toast. Returns true when the server accepted it.
 *
 * The JSON content type is always sent because the server's mutation guard requires it — a plain form
 * post is the one shape SameSite=Strict historically leaked on, so the API refuses anything else.
 *
 * `errorEl` redirects the failure message into an inline element instead of a toast. A form's rejection
 * belongs beside the fields that caused it and has to stay on screen while they are corrected, which a
 * toast that fades cannot do.
 */
async function postJson(path, body, { errorEl = null } = {}) {
  const fail = (message) => {
    if (!errorEl) { toast(message, 'error'); return false; }
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
    if (data.warning) toast(data.warning, 'warning');
    else toast(data.detail || 'Done.', 'success');
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

// ── Toasts ────────────────────────────────────────────────────────────────────

export function toast(message, kind = 'info') {
  const host = el('toast-host');
  if (!host) return;
  const div = document.createElement('div');
  div.className = `toast toast-${kind}`;
  div.textContent = message;
  host.appendChild(div);
  // Matches the CSS fade-out duration; a longer hold for errors, which are the ones worth reading.
  setTimeout(() => div.remove(), kind === 'error' ? 8000 : 4000);
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
      return;
    }
    const data = await res.json();
    clearStatus();

    if (!data.configured) {
      showView('setup-view');
      el('logout-btn').classList.add('hidden');
      stopPolling();
    } else if (!data.authenticated) {
      showView('login-view');
      el('logout-btn').classList.add('hidden');
      stopPolling();
    } else {
      showView('dashboard-view');
      el('logout-btn').classList.remove('hidden');
      selectSetting(settingFromHash(location.hash), { replaceHash: false, scroll: Boolean(location.hash) });
    }
  } catch (err) {
    showErrorStatus('Network Error');
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
  if (tab === 'marketplace' || tab === 'plugins') { jobCursor = 0; jobs = []; refreshCatalog(); refreshGames(); }
  if (tab === 'monitoring' || tab === 'overview') { refreshOverview(); refreshLobbies(); }
  if (tab === 'platform' || tab === 'settings') { refreshPlatform(); }
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
  games: 20000,
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

async function refreshActiveTab() {
  switch (activeTab) {
    case 'overview':
    case 'monitoring':
      await refreshOverview();
      break;
    case 'lobbies':
      await refreshLobbies();
      break;
    case 'games':
      await refreshGames();
      break;
    case 'marketplace':
    case 'plugins':
      await refreshJobs();
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
  if (total === 0) { toast('There are no lobbies to close.', 'info'); return; }
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

// ── Games ─────────────────────────────────────────────────────────────────────

async function refreshGames() {
  const data = await getJson('/admin/api/games');
  if (!data) return;
  gameData = data;
  renderGames();
}

function renderGames() {
  if (!gameData) return;
  const filtered = filterGames(gameData.games, {
    q: el('game-filter-q').value,
    availability: el('game-filter-availability').value,
  });

  const host = el('games-list');
  host.innerHTML = '';
  const total = (gameData.games || []).length;
  el('games-empty').textContent = total === 0
    ? 'No games discovered. Drop a folder or a .kbg package into the games directory.'
    : 'No games match these filters.';
  el('games-empty').classList.toggle('hidden', filtered.length > 0);
  setNavCount('games', total);
  el('games-note').textContent =
    `Games root: ${gameData.gamesRoot} — packages extracted to: ${gameData.packagesRoot}. `
    + `Disk figures measured ${formatClock(gameData.diskMeasuredAt)} `
    + `(compressed cache ${formatBytes(gameData.compressedCacheBytes)}, logs ${formatBytes(gameData.logsBytes)}).`;

  for (const game of filtered) host.appendChild(gameCard(game));
}

function gameCard(game) {
  const card = document.createElement('div');
  card.className = 'game-card';

  const header = document.createElement('div');
  header.className = 'game-card-header';
  const title = document.createElement('h3');
  title.textContent = game.name;
  const id = document.createElement('code');
  id.textContent = game.id;
  header.append(title, id);
  if (game.version) {
    const version = document.createElement('span');
    version.className = 'badge badge-muted';
    version.textContent = `v${game.version}`;
    header.appendChild(version);
  }
  if (game.serverAuthority) {
    const authority = document.createElement('span');
    authority.className = 'badge badge-muted';
    authority.textContent = 'server authority';
    header.appendChild(authority);
  }
  // Only the actionable states get a badge — see sdkBadge. A game with no stamp shows nothing.
  const sdk = sdkBadge(game, gameData?.serverSdkVersion);
  if (sdk) {
    const badge = document.createElement('span');
    badge.className = sdk.className;
    badge.textContent = sdk.label;
    badge.title = sdk.title;
    header.appendChild(badge);
  }
  const state = document.createElement('span');
  state.className = `badge badge-${game.availability === 'available' ? 'ok' : 'warning'}`;
  state.textContent = availabilityLabel(game.availability);
  header.appendChild(state);

  // Engine state, separate from the availability badge and deliberately absent from the select below —
  // that control is a command, and offering a value the server would refuse is worse than not offering
  // it. 'ready' has an empty label and renders nothing.
  const busy = isBusyLifecycle(game.lifecycle);
  if (busy) {
    const lifecycle = document.createElement('span');
    lifecycle.className = `badge ${lifecycleClass(game.lifecycle)}`;
    lifecycle.textContent = lifecycleLabel(game.lifecycle);
    header.appendChild(lifecycle);
  }
  // Read from the catalog snapshot the portal already holds, never fetched here: the games tab polls
  // every 20 seconds because it triggers a disk walk, and a catalog fetch carries a 30-second timeout.
  // Absent until the operator has visited the Marketplace tab, which is the right trade — this is a
  // pointer to that tab, not the place the work happens.
  const offered = (catalogData?.entries || []).find(
    (e) => e.id === game.id && e.status === 'updateAvailable');
  if (offered) {
    const update = document.createElement('span');
    update.className = 'badge badge-warning';
    update.textContent = `update ${formatVersion(offered.availableVersion)}`;
    header.appendChild(update);
  }
  card.appendChild(header);

  const facts = document.createElement('div');
  facts.className = 'game-facts';
  addFact(facts, 'Disk', formatBytes(game.diskBytes),
    `Files ${formatBytes(game.directoryBytes)} + compressed ${formatBytes(game.compressedBytes)}`
    + (game.packageBacked ? ` + package ${formatBytes(game.packageBytes)}` : ''));
  addFact(facts, 'Running now', `${game.activeLobbies} lobby/lobbies`, `${game.activePlayers} player(s)`);
  addFact(facts, 'Max players', String(game.maxPlayers), '');
  addFact(facts, 'Installed from', game.root === 'packages' ? '.kbg package' : 'games folder', game.directory);
  card.appendChild(facts);

  const actions = document.createElement('div');
  actions.className = 'game-actions';

  const select = document.createElement('select');
  select.className = 'text-input filter-narrow';
  for (const option of AVAILABILITY) {
    const opt = document.createElement('option');
    opt.value = option.value;
    opt.textContent = option.label;
    opt.title = option.hint;
    if (option.value === game.availability) opt.selected = true;
    select.appendChild(opt);
  }
  select.onchange = () => setAvailability(game, select.value);
  if (busy) {
    // An availability write racing a directory swap is arbitration the engine shouldn't have to do.
    select.disabled = true;
    select.title = `${lifecycleLabel(game.lifecycle)} — availability can't change mid-update.`;
  }
  actions.appendChild(select);

  const hint = document.createElement('span');
  hint.className = 'game-hint';
  hint.textContent = AVAILABILITY.find((a) => a.value === game.availability)?.hint ?? '';
  actions.appendChild(hint);

  // A staged game is only reachable through a link carrying its id, so hand that link over rather than
  // making the operator construct it. It is deliberately NOT presented as a secret.
  if (game.availability === 'staged') {
    const copy = document.createElement('button');
    copy.className = 'btn btn-secondary btn-small';
    copy.type = 'button';
    copy.textContent = 'Copy launch link';
    copy.onclick = () => copyStagedLink(game);
    actions.appendChild(copy);
  }

  const spacer = document.createElement('span');
  spacer.className = 'filter-spacer';
  actions.appendChild(spacer);

  const exportBtn = document.createElement('button');
  exportBtn.className = 'btn btn-primary btn-small game-export';
  exportBtn.type = 'button';
  exportBtn.textContent = 'Export';
  exportBtn.onclick = () => exportGame(game.id);
  actions.appendChild(exportBtn);

  const remove = document.createElement('button');
  remove.className = 'btn btn-danger btn-small';
  remove.type = 'button';
  remove.textContent = 'Delete';
  if (busy) {
    remove.disabled = true;
    remove.title = `${lifecycleLabel(game.lifecycle)} — wait for the update to finish.`;
  } else if (game.deletable) {
    remove.onclick = () => deleteGame(game);
  } else {
    // Say why rather than offering a button that always fails: in production the games folder is a
    // read-only mount, and no amount of clicking will change that.
    remove.disabled = true;
    remove.title = game.deleteBlockedReason || 'This game cannot be deleted on this deployment.';
  }
  actions.appendChild(remove);
  card.appendChild(actions);

  if (busy) {
    const why = document.createElement('p');
    why.className = 'game-hint game-hint-block';
    why.textContent = `${lifecycleLabel(game.lifecycle)} — follow it in the Marketplace tab.`;
    const jump = document.createElement('button');
    jump.type = 'button';
    jump.className = 'btn btn-secondary btn-small';
    jump.textContent = 'View operation';
    // Jumps rather than duplicating the controls: one place owns package operations.
    jump.onclick = () => { el('mkt-filter-q').value = game.id; location.hash = '#marketplace'; };
    why.appendChild(jump);
    card.appendChild(why);
  } else if (!game.deletable && game.deleteBlockedReason) {
    const why = document.createElement('p');
    why.className = 'game-hint game-hint-block';
    why.textContent = `Delete unavailable: ${game.deleteBlockedReason} Disable the game instead.`;
    card.appendChild(why);
  }

  return card;
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

/** "2–8", "4", "up to 8", "2+", or '' when the entry declared no range at all. */
function playerRange(entry) {
  const min = entry.minPlayers;
  const max = entry.maxPlayers;
  if (!min && !max) return '';
  if (min && max) return min === max ? `${min}` : `${min}–${max}`;
  return max ? `up to ${max}` : `${min}+`;
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

function addFact(host, label, value, sub) {
  const fact = document.createElement('div');
  fact.className = 'game-fact';
  const l = document.createElement('span');
  l.className = 'game-fact-label';
  l.textContent = label;
  const v = document.createElement('span');
  v.className = 'game-fact-value';
  v.textContent = value;
  fact.append(l, v);
  if (sub) {
    const s = document.createElement('span');
    s.className = 'game-fact-sub';
    s.textContent = sub;
    fact.appendChild(s);
  }
  host.appendChild(fact);
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
  if (await postJson(`/admin/api/games/${encodeURIComponent(game.id)}/availability`, { state })) refreshGames();
  else renderGames();
}

async function deleteGame(game) {
  const isManuallyUploaded = game.root === 'games' || game.packageRoot !== 'managed'
    || !catalogData?.entries?.some((e) => e.id === game.id && e.sourceId);
  const warning = isManuallyUploaded
    ? 'This plugin was manually uploaded and may not be re-downloadable via the marketplace.'
    : null;
  if (!await confirmAction(
    `Delete ${game.name} and all ${formatBytes(game.diskBytes)} of its files from disk? `
    + (game.activeLobbies > 0 ? `Its ${game.activeLobbies} running lobby/lobbies are closed first. ` : '')
    + 'This cannot be undone — the game has to be reinstalled to come back.',
    'Delete Permanently',
    {
      warning,
      onExport: () => exportGame(game.id),
    })) return;
  if (await postJson(`/admin/api/games/${encodeURIComponent(game.id)}/delete`, {})) await refreshGames();
}

async function copyStagedLink(game) {
  // The shell origin, not this one: the link is for a player's browser. Derived from the games root's
  // sibling rather than guessed — but the admin origin can't know the shell's public URL, so offer the
  // relative form and let the operator paste it against their own host.
  const link = `/?game=${encodeURIComponent(game.id)}`;
  try {
    await navigator.clipboard.writeText(link);
    toast(`Copied "${link}" — append it to your shell's address. Visibility only, not access control.`, 'success');
  } catch {
    toast(`Launch path: ${link}`, 'info');
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
async function refreshCatalog({ refresh = false } = {}) {
  const data = await getJson(`/admin/api/marketplace/catalog${refresh ? '?refresh=1' : ''}`);
  if (!data) return;
  catalogData = data;
  // The catalog reply carries the current job set too, so entering the tab costs one request rather
  // than two.
  jobs = mergeJobs(jobs, data.jobs, JOB_VIEW_LIMIT);
  jobCursor = Math.max(jobCursor, Number(data.jobsLastSequence) || 0);
  renderSourceFilter();
  renderMarketplace();
  renderJobs();
}

async function refreshJobs() {
  const data = await getJson(`/admin/api/packages/jobs?after=${jobCursor}`);
  if (!data) return;

  // A sequence that went BACKWARDS means the server restarted: the registry is in-memory, so it begins
  // again at 1. Without this, every real job that follows sorts below the stale rows we are still
  // holding and is sliced away at JOB_VIEW_LIMIT, while the cursor — only ever clamped upward — asks
  // for everything after a sequence the new process will not reach for a long time. The log feed
  // already handles exactly this; the job feed is the same shape and did not.
  const lastSequence = Number(data.lastSequence) || 0;
  if (lastSequence < jobCursor) { jobs = []; jobCursor = 0; }

  const before = new Set(jobs.filter((j) => j.terminal).map((j) => j.jobId));
  jobs = mergeJobs(jobs, data.jobs, JOB_VIEW_LIMIT);
  jobCursor = lastSequence || jobCursor;

  // A job reaching a terminal state is the moment the catalog's answer changed — re-read it so the
  // card flips from "Update to 1.3.0" to "Up to date" now rather than on the next tab entry.
  let finished = false;
  for (const job of jobs) {
    if (!job.terminal || before.has(job.jobId)) continue;
    finished = true;
    if (!reportedJobs.has(job.jobId)) {
      reportedJobs.add(job.jobId);
      announceJob(job);
    }
  }

  renderJobs();
  setNavCount('marketplace', updatesAvailable());
  if (finished) refreshCatalog();
}

function announceJob(job) {
  const what = `${job.gameName || job.gameId}`;
  if (job.status === 'succeeded') toast(`${what}: ${job.phase}`, 'success');
  else if (job.status === 'failed') toast(`${what} failed: ${job.error || job.phase}`, 'error');
  else toast(`${what}: ${job.phase}`, 'warning');
}

function updatesAvailable() {
  return (catalogData?.entries || []).filter((e) => e.status === 'updateAvailable').length;
}

function renderSourceFilter() {
  const select = el('mkt-filter-source');
  const current = select.value;
  select.textContent = '';
  const any = document.createElement('option');
  any.value = '';
  any.textContent = 'Any source';
  select.appendChild(any);
  for (const source of catalogData?.sources || []) {
    const opt = document.createElement('option');
    opt.value = source.id;
    opt.textContent = source.name || source.id;
    select.appendChild(opt);
  }
  select.value = current;
}

function renderMarketplace() {
  const list = el('mkt-list');
  list.textContent = '';
  el('mkt-disabled').classList.toggle('hidden', catalogData?.enabled !== false);

  const entries = filterCatalog(catalogData?.entries, {
    q: el('mkt-filter-q').value,
    status: el('mkt-filter-status').value,
    source: el('mkt-filter-source').value,
  });
  el('mkt-empty').classList.toggle('hidden', entries.length > 0);
  for (const entry of entries) list.appendChild(marketplaceCard(entry));

  const failed = (catalogData?.sources || []).filter((s) => s.error);
  const parts = [];
  if (catalogData?.fetchedAt) parts.push(`Catalog read ${formatClock(catalogData.fetchedAt)}`);
  if (catalogData?.appVersion) parts.push(`server v${catalogData.appVersion}`);
  if (catalogData?.managedRoot) parts.push(`installs into ${catalogData.managedRoot}`);
  if (catalogData?.backupRetention !== undefined) {
    parts.push(catalogData.backupRetention > 0
      ? `keeping ${catalogData.backupRetention} previous version(s) for rollback`
      : 'not keeping previous versions (KnockBox:PackageBackupCount=0)');
  }
  for (const source of failed) parts.push(`${source.name || source.id}: ${source.error}`);
  el('mkt-note').textContent = parts.join(' · ');
  setNavCount('marketplace', updatesAvailable());
}

function marketplaceCard(entry) {
  const card = document.createElement('div');
  card.className = 'game-card mkt-card';
  card.dataset.id = entry.id;

  const header = document.createElement('div');
  header.className = 'game-card-header';
  const title = document.createElement('h3');
  title.textContent = entry.name;
  const id = document.createElement('code');
  id.textContent = entry.id;
  header.append(title, id);

  const status = document.createElement('span');
  status.className = `badge ${pluginStatusClass(entry.status)}`;
  status.textContent = pluginStatusLabel(entry.status);
  status.title = pluginStatusHint(entry.status);
  header.appendChild(status);

  if (entry.contentRating) {
    const rating = document.createElement('span');
    rating.className = 'badge badge-muted';
    rating.textContent = entry.contentRating;
    // Said plainly, because "everyone" beside a game name reads like a guarantee otherwise.
    rating.title = 'Content rating the game declares for itself — not an ESRB or PEGI rating.';
    header.appendChild(rating);
  }

  for (const tag of (entry.tags || []).slice(0, 3)) {
    const chip = document.createElement('span');
    chip.className = 'badge badge-muted';
    chip.textContent = tag;
    header.appendChild(chip);
  }
  card.appendChild(header);

  if (entry.description) {
    const description = document.createElement('p');
    description.className = 'mkt-desc';
    description.textContent = entry.description;
    card.appendChild(description);
  }

  const facts = document.createElement('div');
  facts.className = 'game-facts';
  addFact(facts, 'Installed', entry.installed ? formatVersion(entry.installedVersion) : 'Not installed', '');
  addFact(facts, 'Available', entry.availableVersion ? formatVersion(entry.availableVersion) : 'Not offered',
    entry.sourceName || entry.sourceId || '');
  if (entry.sizeBytes) addFact(facts, 'Download', formatBytes(entry.sizeBytes), '');
  if (entry.author) addFact(facts, 'Author', entry.author, '');
  // Both describe the version being OFFERED — the point is deciding whether to install it.
  const players = playerRange(entry);
  if (players) addFact(facts, 'Players', players, '');
  if (entry.license) addFact(facts, 'License', entry.license, '');
  if (entry.activeLobbies > 0) addFact(facts, 'Running now', `${entry.activeLobbies} lobby/lobbies`, '');
  card.appendChild(facts);

  const links = marketplaceLinks(entry);
  if (links) card.appendChild(links);

  const pending = jobs.find((j) => j.jobId === entry.pendingJobId && !j.terminal);
  const actions = document.createElement('div');
  actions.className = 'game-actions';

  const version = document.createElement('select');
  version.className = 'text-input filter-narrow mkt-version';
  for (const option of versionOptions(entry)) {
    const opt = document.createElement('option');
    // Kind AND version: the version alone is not unique once a backup of the installed version exists,
    // and versionAction resolved the collision to whichever came first — see versionOptionValue.
    opt.value = versionOptionValue(option);
    opt.textContent = `${formatVersion(option.version)} — ${option.kind}`;
    version.appendChild(opt);
  }
  if (version.options.length === 0) version.disabled = true;
  actions.appendChild(version);

  // Always rendered, disabled with a reason when there is nothing running: a control that appears and
  // disappears between polls is worse than one that is visibly inert.
  const mode = document.createElement('select');
  mode.className = 'text-input filter-narrow mkt-mode';
  for (const option of UPDATE_MODES) {
    const opt = document.createElement('option');
    opt.value = option.value;
    opt.textContent = option.label;
    opt.title = option.hint;
    mode.appendChild(opt);
  }
  if (entry.activeLobbies === 0) {
    mode.disabled = true;
    mode.title = 'Nobody is playing this game right now, so it applies immediately either way.';
  }
  actions.appendChild(mode);

  const action = document.createElement('button');
  action.type = 'button';
  action.className = 'btn btn-small mkt-action';
  const refresh = () => {
    // The block reason is response-level, not per entry — see versionAction.
    const decided = versionAction(entry, version.value,
      catalogData?.canInstall === false ? catalogData?.installBlockedReason || 'Installs are unavailable.' : null);
    action.textContent = decided.label;
    action.className = `btn btn-small mkt-action ${decided.danger ? 'btn-danger' : 'btn-primary'}`;
    action.disabled = Boolean(pending) || decided.kind === 'none' || Boolean(decided.blockedReason);
    action.title = decided.blockedReason || '';
    // decided.version, not version.value: the select's value carries the kind too, and what the rollback
    // route wants is the version versionAction actually resolved.
    action.onclick = () => runPackageAction(entry, decided, mode.value);
  };
  version.onchange = refresh;
  refresh();
  actions.appendChild(action);

  if (entry.installed && entry.managed) {
    const policy = document.createElement('select');
    policy.className = 'text-input filter-narrow mkt-policy';
    for (const option of UPDATE_POLICIES) {
      const opt = document.createElement('option');
      opt.value = option.value;
      opt.textContent = option.label;
      opt.title = option.hint;
      if (option.value === entry.updatePolicy) opt.selected = true;
      policy.appendChild(opt);
    }
    policy.value = entry.updatePolicy || 'manual';
    policy.disabled = Boolean(pending);
    policy.onchange = () => postJson(`/admin/api/packages/${encodeURIComponent(entry.id)}/update-policy`,
      { policy: policy.value });
    actions.appendChild(policy);
  }

  const spacer = document.createElement('span');
  spacer.className = 'filter-spacer';
  actions.appendChild(spacer);

  if (entry.installed) {
    const exportBtn = document.createElement('button');
    exportBtn.type = 'button';
    exportBtn.className = 'btn btn-primary btn-small mkt-export';
    exportBtn.textContent = 'Export';
    exportBtn.onclick = () => exportGame(entry.id);
    actions.appendChild(exportBtn);

    const uninstall = document.createElement('button');
    uninstall.type = 'button';
    uninstall.className = 'btn btn-danger btn-small mkt-uninstall';
    uninstall.textContent = 'Uninstall';
    uninstall.disabled = Boolean(pending);
    uninstall.onclick = () => uninstallGame(entry);
    actions.appendChild(uninstall);
  }
  card.appendChild(actions);

  if (entry.shadowedBy) {
    const shadow = document.createElement('p');
    shadow.className = 'game-hint game-hint-block';
    shadow.textContent =
      `Also offered by '${entry.shadowedBy}', which takes precedence — installing here uses that copy.`;
    card.appendChild(shadow);
  }
  if (entry.reason && entry.status !== 'installedOnly') {
    const why = document.createElement('p');
    why.className = 'game-hint game-hint-block';
    why.textContent = entry.reason;
    card.appendChild(why);
  }
  if (pending) card.appendChild(jobRow(pending, { compact: true }));

  return card;
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

  if (decided.incompatible) {
    const runningDesc = entry.activeLobbies > 0 ? ` ${describeMode(mode, entry.activeLobbies)}` : '';
    const reasonText = entry.reason ? ` (${entry.reason})` : '';
    // An update REPLACES a version that is presumably working, which the old wording never said — it read
    // identically whether this was a first install or an overwrite of a running game. And the server
    // stages the result either way, so say that here rather than letting it arrive as a surprise.
    const replaces = decided.kind === 'update'
      ? ` This replaces the installed ${formatVersion(entry.installedVersion)}.`
      : '';
    if (!await confirmAction(
      `Install ${name} ${formatVersion(version)}? This game is unsupported on this server${reasonText} `
      + `and may not work.${replaces} It will be staged — hidden from players until you set it to `
      + `Available.${runningDesc}`,
      'Install Anyways')) return;

    if (await postJson(`/admin/api/marketplace/install/${encodeURIComponent(entry.id)}`,
      { sourceId: entry.sourceId || null, mode })) refreshJobs();
    return;
  }

  if (entry.activeLobbies > 0 && !await confirmAction(
    `${decided.label} ${name}? ${describeMode(mode, entry.activeLobbies)}`, decided.label)) return;

  if (await postJson(`/admin/api/marketplace/install/${encodeURIComponent(entry.id)}`,
    { sourceId: entry.sourceId || null, mode })) refreshJobs();
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
  const isManuallyUploaded = !entry.sourceId || entry.status === 'installedOnly' || !entry.availableVersion;
  const warning = isManuallyUploaded
    ? 'This plugin was manually uploaded and may not be re-downloadable via the marketplace.'
    : null;
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

function renderJobs() {
  const host = el('mkt-jobs');
  host.textContent = '';
  el('mkt-jobs-card').classList.toggle('hidden', jobs.length === 0);
  for (const job of jobs) host.appendChild(jobRow(job));
}

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
    // Inline, not a toast: the operator is looking at this modal and has to change the input.
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
      toast(body.detail || 'Package accepted.', 'success');
      // Everything after this point happens inside the JOB — a bad archive, an id collision, a full
      // disk. The request is over; the operations list owns the outcome.
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
        refreshCatalog({ refresh: true });
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
          refreshCatalog({ refresh: true });
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
    await refreshCatalog({ refresh: true });
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
  const kept = new Map(
    [...host.querySelectorAll('input[data-limit-key]')].map((input) => [input.dataset.limitKey, input.value]));
  host.innerHTML = '';

  const overridden = new Set(data.overridden || []);
  for (const field of LIMIT_FIELDS) {
    const row = document.createElement('div');
    row.className = 'field-row';

    const label = document.createElement('label');
    label.className = 'limit-label';
    label.textContent = field.label;
    label.htmlFor = `limit-${field.key}`;

    const input = document.createElement('input');
    input.type = 'text';
    input.inputMode = 'decimal';
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

  const anyOverridden = (data.overridden || []).length > 0;
  el('limits-badge').hidden = !anyOverridden;
  el('limits-save').disabled = false;
  el('limits-reset').disabled = !anyOverridden;
  el('limits-note').textContent = anyOverridden
    ? `${data.overridden.length} of ${LIMIT_FIELDS.length} limits are overridden. `
      + `${formatCount(data.activeLobbies)} lobbies and ${formatCount(data.connectedPlayers)} players right now.`
    : 'Every limit is at its default.';

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
  for (const input of document.querySelectorAll('#limits-fields input[data-limit-key]')) {
    raw[input.dataset.limitKey] = input.value;
  }
  const checked = validateLimits(raw);
  if (!checked.ok) { toast(checked.error, 'error'); return; }

  // Tightening a limit is not destructive, but it is felt immediately by everyone connected, so the two
  // that can refuse a player outright get a confirmation naming what is running right now.
  const capping = checked.values.maxLobbies !== null || checked.values.maxLobbiesPerGame !== null;
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
  if (!text) { toast('Enter the message players should see.', 'error'); return; }

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
  if (!checked.ok) { toast(checked.error, 'error'); return; }

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
  // Awaited by the server through the real delivery path, so the toast is the actual answer rather than
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
  if (!checked.ok) { toast(checked.error, 'error'); return; }

  const list = pattern ? codesDraft.patterns : codesDraft.words;
  if (list.includes(checked.value)) { toast(`${checked.value} is already blocked.`, 'warning'); return; }
  list.push(checked.value);
  input.value = '';
  // Said at the moment of typing, where it can still be changed, rather than as a footnote after saving.
  if (checked.unreachable) {
    toast(`${checked.value} can never be generated — the code alphabet has no O, 0, I or 1.`, 'warning');
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
      const targetEl = el(settingId);
      if (targetEl && typeof targetEl.scrollIntoView === 'function') {
        targetEl.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    });
  }

  for (const button of document.querySelectorAll('.nav-item:not(.tree-item)')) {
    button.addEventListener('click', (e) => {
      e.preventDefault();
      const settingId = settingFromHash(button.dataset.tab);
      const targetEl = el(settingId);
      if (targetEl && typeof targetEl.scrollIntoView === 'function') {
        targetEl.scrollIntoView({ behavior: 'smooth', block: 'start' });
      }
    });
  }

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

  el('game-filter-q').addEventListener('input', renderGames);
  el('game-filter-availability').addEventListener('change', renderGames);
  el('rescan-btn').addEventListener('click', async () => {
    // Give the catalog a beat to republish before re-reading, or the operator sees the pre-rescan list
    // and concludes the button does nothing.
    if (await postJson('/admin/api/games/rescan', {})) setTimeout(refreshGames, 800);
  });

  el('mkt-filter-q').addEventListener('input', renderMarketplace);
  el('mkt-filter-status').addEventListener('change', renderMarketplace);
  el('mkt-filter-source').addEventListener('change', renderMarketplace);
  el('mkt-refresh-btn').addEventListener('click', () => refreshCatalog({ refresh: true }));
  el('mkt-upload-btn').addEventListener('click', openUpload);
  el('mkt-settings-btn').addEventListener('click', openSources);
  el('mkt-settings-close').addEventListener('click', () => el('mkt-settings-backdrop').classList.add('hidden'));
  el('mkt-source-add').addEventListener('click', addSource);

  el('upload-close').addEventListener('click', closeUpload);
  el('upload-abort').addEventListener('click', () => uploadXhr?.abort());
  el('upload-submit').addEventListener('click', startUpload);
  el('upload-drop').addEventListener('click', () => el('upload-file').click());
  el('upload-file').addEventListener('change', () => pickUploadFile(el('upload-file').files?.[0]));
  el('upload-drop').addEventListener('dragover', (e) => {
    e.preventDefault();
    el('upload-drop').classList.add('drop-active');
  });
  el('upload-drop').addEventListener('dragleave', () => el('upload-drop').classList.remove('drop-active'));
  el('upload-drop').addEventListener('drop', (e) => {
    e.preventDefault();
    el('upload-drop').classList.remove('drop-active');
    pickUploadFile(e.dataTransfer?.files?.[0]);
  });
  // A drop that MISSES the zone would otherwise navigate the portal away to a binary download, losing
  // whatever was in flight. One line, and the classic version of this bug.
  document.addEventListener('dragover', (e) => e.preventDefault());
  document.addEventListener('drop', (e) => e.preventDefault());

  el('limits-save').addEventListener('click', saveLimits);
  el('limits-reset').addEventListener('click', revertLimits);
  el('limits-refresh').addEventListener('click', refreshPlatform);

  el('hook-add').addEventListener('click', addWebhook);

  el('schedule-cadence').addEventListener('change', applyScheduleCadence);
  el('schedule-save').addEventListener('click', () => saveSchedule());
  el('schedule-reset').addEventListener('click', () => saveSchedule(true));
  el('schedule-refresh').addEventListener('click', refreshPlatform);

  el('announce-post').addEventListener('click', postAnnouncement);
  el('announce-clear').addEventListener('click', clearAnnouncement);

  el('code-word-add').addEventListener('click', () => addRoomCode(false));
  el('code-pattern-add').addEventListener('click', () => addRoomCode(true));
  // Enter in the box adds the entry rather than doing nothing: this is a list you build by typing.
  el('code-word').addEventListener('keydown', (e) => { if (e.key === 'Enter') addRoomCode(false); });
  el('code-pattern').addEventListener('keydown', (e) => { if (e.key === 'Enter') addRoomCode(true); });
  el('codes-save').addEventListener('click', saveRoomCodes);
  el('codes-clear').addEventListener('click', clearRoomCodes);

  el('log-filter-level').addEventListener('change', resetLogStream);
  el('log-filter-category').addEventListener('input', resetLogStream);
  el('log-filter-q').addEventListener('input', resetLogStream);
  el('log-follow').addEventListener('change', () => { if (el('log-follow').checked) refreshLogs(); });
  el('log-files-btn').addEventListener('click', openLogFiles);
  el('files-close').addEventListener('click', () => el('files-backdrop').classList.add('hidden'));

  el('confirm-ok').addEventListener('click', () => settleConfirm(true));
  el('confirm-cancel').addEventListener('click', () => settleConfirm(false));
  el('confirm-backdrop').addEventListener('click', (e) => {
    if (e.target === el('confirm-backdrop')) settleConfirm(false);
  });
  for (const id of ['upload-backdrop', 'mkt-settings-backdrop']) {
    el(id).addEventListener('click', (e) => {
      if (e.target === el(id)) el(id).classList.add('hidden');
    });
  }
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    if (!el('confirm-backdrop').classList.contains('hidden')) settleConfirm(false);
    el('files-backdrop').classList.add('hidden');
    el('mkt-settings-backdrop').classList.add('hidden');
    // Not closeUpload(): Escape must not silently abort a transfer that is halfway through. The Cancel
    // button is the deliberate way out.
    if (!uploadXhr) el('upload-backdrop').classList.add('hidden');
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
  await checkAuthStatus();
}

/** Wires the page and runs the first auth check. Called from index.html. */
export function bootstrap() {
  wire();
  checkAuthStatus();
}
