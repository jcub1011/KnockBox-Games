// Admin portal client logic.
//
// Structure mirrors web/shell.js: pure helpers live in admin-core.js (tested in the Node environment),
// this module owns the DOM, fetch and timers, and nothing runs on import — bootstrap() is exported and
// called from index.html, so the test suite can drive the exported functions without a live poll to
// suppress. Views and tabs are pre-existing markup toggled by class, never rendered from templates; only
// table and list ROWS are built imperatively, always with textContent, because game titles and player
// display names are untrusted input.

import {
  AVAILABILITY, TABS, appendLogEntries, availabilityLabel, cpuPercentBetween, filterGames, filterLobbies,
  formatBytes, formatClock, formatCount, formatDuration, logLevelClass, logLevelTag, ratePerSecond,
  tabFromHash,
} from './admin-core.js';

const el = (id) => document.getElementById(id);

// How often each tab refreshes while it is the visible one. Only the visible tab polls: four tabs each
// polling every five seconds would quadruple the request rate for three panels nobody is looking at, and
// the games tab in particular can trigger a disk walk.
const POLL_MS = { overview: 5000, lobbies: 5000, games: 20000, logs: 2000 };
const LOG_VIEW_LIMIT = 500;

// ── Module state ──────────────────────────────────────────────────────────────

let pollTimer = null;
let activeTab = TABS[0];

// Latest payloads, kept so a filter change re-renders without a round trip.
let lobbyData = null;
let gameData = null;
let logEntries = [];
let logCursor = 0;

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
    await checkAuthStatus();
    return null;
  }
  return res;
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

  refreshActiveTab();
  startPolling();
}

function startPolling() {
  stopPolling();
  pollTimer = setInterval(refreshActiveTab, POLL_MS[activeTab] ?? 5000);
}

function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
}

async function refreshActiveTab() {
  switch (activeTab) {
    case 'overview': await refreshOverview(); break;
    case 'lobbies': await refreshLobbies(); break;
    case 'games': await refreshGames(); break;
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
  if (game.deletable) {
    remove.onclick = () => deleteGame(game);
  } else {
    // Say why rather than offering a button that always fails: in production the games folder is a
    // read-only mount, and no amount of clicking will change that.
    remove.disabled = true;
    remove.title = game.deleteBlockedReason || 'This game cannot be deleted on this deployment.';
  }
  actions.appendChild(remove);
  card.appendChild(actions);

  if (!game.deletable && game.deleteBlockedReason) {
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
  document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;
    if (!el('confirm-backdrop').classList.contains('hidden')) settleConfirm(false);
    el('files-backdrop').classList.add('hidden');
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
