// Admin portal client logic.
//
// Structure mirrors web/shell.js: pure helpers live in admin-core.js (tested in the Node environment),
// this module owns the DOM, fetch and timers, and nothing runs on import — bootstrap() is exported and
// called from index.html, so the test suite can drive the exported functions without a live poll to
// suppress. Views and tabs are pre-existing markup toggled by class, never rendered from templates; only
// table and list ROWS are built imperatively, always with textContent, because game titles and player
// display names are untrusted input.

import {
  AVAILABILITY, TABS, UPDATE_MODES, UPDATE_POLICIES, appendLogEntries, availabilityLabel,
  cpuPercentBetween, filterCatalog, filterGames, filterLobbies, formatBytes, formatClock, formatCount,
  formatDuration, formatVersion, isBusyLifecycle, isTerminalJob, jobProgress, lifecycleClass,
  lifecycleLabel, logLevelClass, logLevelTag, mergeJobs, pluginStatusClass, pluginStatusHint,
  pluginStatusLabel, ratePerSecond, tabFromHash, uploadGuard, versionAction, versionOptions,
} from './admin-core.js';

const el = (id) => document.getElementById(id);

// How often each tab refreshes while it is the visible one. Only the visible tab polls: five tabs each
// polling every five seconds would multiply the request rate for four panels nobody is looking at, and
// the games tab in particular can trigger a disk walk.
//
// The marketplace entry is the fastest of the five, but it hits ONLY the in-memory job feed. The catalog
// — which can reach the network, with a 30-second timeout — is re-read on tab entry, on Refresh, and
// whenever a job finishes, which is what flips a card from "Update to 1.3.0" to "Up to date" the moment
// it is true rather than up to 20 seconds later.
const POLL_MS = { overview: 5000, lobbies: 5000, games: 20000, marketplace: 3000, logs: 2000 };
const LOG_VIEW_LIMIT = 500;
const JOB_VIEW_LIMIT = 50;

// ── Module state ──────────────────────────────────────────────────────────────

let pollTimer = null;
let activeTab = TABS[0];

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

// Previous counter samples, for the rates admin-core derives. `{ value, at }` pairs — see ratePerSecond.
let cpuSample = null;
const gameFrameSamples = new Map();

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
    showOkStatus();
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
 */
async function postJson(path, body) {
  try {
    const res = await request(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body ?? {}),
    });
    if (!res) return false;
    const data = await res.json().catch(() => null);
    if (!res.ok || !data?.success) {
      toast(data?.error || `That didn't work (${res.status}).`, 'error');
      return false;
    }
    // Success with something worth saying: `detail` explains what the action did and did not do (chiefly
    // that disabling a game leaves its running lobbies alone), `warning` that a policy change is live but
    // wasn't written to disk.
    if (data.warning) toast(data.warning, 'warning');
    else toast(data.detail || 'Done.', 'success');
    return true;
  } catch (err) {
    toast('Network error.', 'error');
    console.error(`POST ${path} failed:`, err);
    return false;
  }
}

// ── Status pill ───────────────────────────────────────────────────────────────

// One function per state, both routed through setStatus. The previous shape was an
// updateServerStatus(online) whose offline branch nothing ever reached — every offline path called
// showErrorStatus — so the file carried two ways to paint the dot red and a reader had to check both to
// learn which one ran.
function showOkStatus() {
  setStatus('Admin Port Active', 'var(--success-color)');
}

function showErrorStatus(msg) {
  setStatus(msg, 'var(--error-color)');
}

function setStatus(text, color) {
  const dot = document.querySelector('.status-dot');
  el('server-status-text').textContent = text;
  if (!dot) return;
  dot.style.backgroundColor = color;
  dot.style.boxShadow = `0 0 8px ${color}`;
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
function confirmAction(body, okLabel = 'Confirm') {
  el('confirm-body').textContent = body;
  el('confirm-ok').textContent = okLabel;
  el('confirm-backdrop').classList.remove('hidden');
  el('confirm-ok').focus();
  return new Promise((resolve) => { confirmResolve = resolve; });
}

function settleConfirm(result) {
  el('confirm-backdrop').classList.add('hidden');
  const resolve = confirmResolve;
  confirmResolve = null;
  if (resolve) resolve(result);
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
    showOkStatus();

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
      selectTab(tabFromHash(location.hash), { replaceHash: false });
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

// ── Tabs ──────────────────────────────────────────────────────────────────────

const TAB_TITLES = {
  overview: 'System Overview',
  lobbies: 'Active Lobbies',
  games: 'Game Catalog',
  marketplace: 'Marketplace & Packages',
  logs: 'System Logs',
};

export function selectTab(tab, { replaceHash = true } = {}) {
  activeTab = TABS.includes(tab) ? tab : TABS[0];
  el('panel-title').textContent = TAB_TITLES[activeTab];

  for (const button of document.querySelectorAll('.nav-item')) {
    button.classList.toggle('active', button.dataset.tab === activeTab);
  }
  for (const name of TABS) {
    el(`tab-${name}`).classList.toggle('hidden', name !== activeTab);
  }
  if (replaceHash && location.hash !== `#${activeTab}`) {
    history.replaceState(null, '', `#${activeTab}`);
  }

  // Re-reading the log stream from cursor 0 on entry means switching away and back shows the buffer
  // rather than an empty panel waiting for the next event.
  if (activeTab === 'logs') { logCursor = 0; logEntries = []; }
  // Same reasoning for the job feed: a cursor of 0 asks for the whole retained set, so an operator who
  // spent ten minutes on another tab comes back to the outcomes they missed rather than an empty strip.
  if (activeTab === 'marketplace') { jobCursor = 0; jobs = []; refreshCatalog(); }

  refreshActiveTab();
  startPolling();
}

function startPolling() {
  stopPolling();
  pollTimer = setInterval(refreshActiveTab, POLL_MS[activeTab] ?? 5000);
}

/**
 * Stops the visible tab's refresh timer.
 *
 * Exported for the test suite, which imports a FRESH copy of this module per case: without it the
 * previous case's interval keeps firing against the next one's fetch stub, and a test that advances
 * timers records requests it never made. Nothing in the page needs to call it — selectTab and the
 * logout path both go through startPolling, which stops the old timer first.
 */
export function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
}

async function refreshActiveTab() {
  switch (activeTab) {
    case 'overview': await refreshOverview(); break;
    case 'lobbies': await refreshLobbies(); break;
    case 'games': await refreshGames(); break;
    case 'marketplace': await refreshJobs(); break;
    case 'logs': await refreshLogs(); break;
  }
  el('last-updated').textContent = `Updated ${new Date().toLocaleTimeString()}`;
}

// ── Overview ──────────────────────────────────────────────────────────────────

async function refreshOverview() {
  const status = await getJson('/admin/api/system/status');
  if (status) applyStatus(status);
  const metrics = await getJson('/admin/api/metrics');
  if (metrics) applyMetrics(metrics);
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
  if (!await confirmAction(
    `Delete ${game.name} and all ${formatBytes(game.diskBytes)} of its files from disk? `
    + (game.activeLobbies > 0 ? `Its ${game.activeLobbies} running lobby/lobbies are closed first. ` : '')
    + 'This cannot be undone — the game has to be reinstalled to come back.', 'Delete Permanently')) return;
  if (await postJson(`/admin/api/games/${encodeURIComponent(game.id)}/delete`, {})) refreshGames();
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

  const before = new Set(jobs.filter((j) => j.terminal).map((j) => j.jobId));
  jobs = mergeJobs(jobs, data.jobs, JOB_VIEW_LIMIT);
  jobCursor = Number(data.lastSequence) || jobCursor;

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
  if (entry.activeLobbies > 0) addFact(facts, 'Running now', `${entry.activeLobbies} lobby/lobbies`, '');
  card.appendChild(facts);

  const pending = jobs.find((j) => j.jobId === entry.pendingJobId && !j.terminal);
  const actions = document.createElement('div');
  actions.className = 'game-actions';

  const version = document.createElement('select');
  version.className = 'text-input filter-narrow mkt-version';
  for (const option of versionOptions(entry)) {
    const opt = document.createElement('option');
    opt.value = option.version ?? '';
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
    const decided = versionAction(entry, version.value);
    action.textContent = decided.label;
    action.className = `btn btn-small mkt-action ${decided.danger ? 'btn-danger' : 'btn-primary'}`;
    action.disabled = Boolean(pending) || decided.kind === 'none' || Boolean(decided.blockedReason);
    action.title = decided.blockedReason || '';
    action.onclick = () => runPackageAction(entry, decided, version.value, mode.value);
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

  if (entry.installed && entry.managed) {
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

async function runPackageAction(entry, decided, version, mode) {
  const name = entry.name || entry.id;
  if (decided.kind === 'rollback') {
    if (!await confirmAction(
      `Roll ${name} back from ${formatVersion(entry.installedVersion)} to ${formatVersion(version)}? `
      + describeMode(mode, entry.activeLobbies), 'Roll Back')) return;
    if (await postJson(`/admin/api/packages/${encodeURIComponent(entry.id)}/rollback`, { version, mode })) {
      refreshJobs();
    }
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
  if (!await confirmAction(
    `Uninstall ${entry.name || entry.id}? Its files, its cached assets and any retained versions are `
    + `deleted from disk${running > 0 ? `, and its ${running} running lobby/lobbies are closed` : ''}.`,
    'Uninstall')) return;
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
    if (xhr.status === 401) { handleUnauthorized(); return; }

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
    count.textContent = source.error ? 'unreachable' : `${source.entries} game(s)`;
    if (source.error) count.title = source.error;
    row.appendChild(count);

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
  // exactly the drift this codebase avoids. The server's message is what the operator sees.
  if (await postJson('/admin/api/marketplace/sources', body)) {
    for (const id of ['mkt-source-id', 'mkt-source-name', 'mkt-source-url', 'mkt-source-download']) {
      el(id).value = '';
    }
    await refreshCatalog({ refresh: true });
    renderSources();
  } else {
    el('mkt-settings-error').classList.add('hidden');
  }
}

function wire() {
  el('setup-form').addEventListener('submit', onSetupSubmit);
  el('login-form').addEventListener('submit', onLoginSubmit);
  el('logout-btn').addEventListener('click', onLogout);

  for (const button of document.querySelectorAll('.nav-item')) {
    button.addEventListener('click', () => selectTab(button.dataset.tab));
  }
  window.addEventListener('hashchange', () => selectTab(tabFromHash(location.hash), { replaceHash: false }));

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
    await fetch('/admin/api/auth/logout', { method: 'POST' });
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
