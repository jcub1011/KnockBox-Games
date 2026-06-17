// Platform shell — owns the single CONTROL websocket, identity, and the lobby UI. When a game
// starts it requests a lobby-scoped ticket and embeds the game in a cross-origin iframe (the game
// origin). It does NOT bridge gameplay: the game opens its own data websocket via the ticket and
// talks to the server directly. The shell and game are isolated (separate origins) on purpose.
import { PROTOCOL_VERSION, appendPlayLog, buildGameSrc, buildJoinLink, dominantColorFromPixels, gameWsEndpoint, ordinal, parseJoinParam, parseRgbComponents, partitionPlayLogMetadata, pickContrastText, reconnectDelay, rosterAdd, rosterRemove } from './kb-core.js';

// ── Identity (client-side) ───────────────────────────────────────────────────
// The server mints the playerId and a signed token on first connect; we persist the TOKEN (not the
// id) in sessionStorage — per-tab, so each tab is a distinct anonymous player — and resend it to
// prove ownership of that id on reconnect. The token never leaves this (shell) origin; games get a
// scoped ticket instead. (No login by design.)
//
// The display NAME, by contrast, lives in localStorage so it survives closing the browser — a
// returning player doesn't retype it. It is read EXACTLY ONCE into the in-memory `displayName` below
// and thereafter owned by this tab: we write on change but never re-read and never listen for the
// cross-tab `storage` event. That isolation is deliberate — with a host tab (screen-share) and a
// player tab open in the same browser they share the one localStorage key, so reacting to each
// other's writes would flip a tab's name out from under the user. Last writer wins only for the
// NEXT fresh load; live tabs keep whatever name they were given.
//
// Unlike a server browser, joining here is BY CODE ONLY — there is no lobby-listing endpoint, so a
// private lobby is discoverable only to players who were given its code.

let playerId = null;                                  // assigned by the server (Welcome)
let token = sessionStorage.getItem('kb.token');       // signed identity token (anti-spoof), per-tab
let displayName = localStorage.getItem('kb.displayName') || '';   // read once; empty until named
let gameOrigin = null;                                // where game iframes/sockets live (set by Welcome)

// Auto-join (test convenience): a tab opened via middle-click on the room-code button carries
// "?join=CODE". Such a tab must act as a DISTINCT player — but window.open copies the opener's
// sessionStorage, so it would inherit the opener's identity token (and saved lobby). Clear them so
// this tab gets a fresh server-minted identity; we join the code once connected (see Welcome).
let pendingJoinCode = parseJoinParam(location.search);
if (pendingJoinCode) {
  sessionStorage.removeItem('kb.token');
  sessionStorage.removeItem('kb.lobbyId');
  token = null;
  history.replaceState(null, '', location.pathname); // tidy URL so a refresh won't re-trigger
}

const el = (id) => document.getElementById(id);

// Current session state.
let ws = null;
let reconnectAttempt = 0;       // 0-based; drives exponential backoff, reset once a session is confirmed
let games = new Map();          // gameId -> manifest
let lobby = null;               // { lobbyId, gameId, hostId, players: [] } once in a game
const pending = new Map();      // cid -> resolver
let cidSeq = 0;

// ── WebSocket plumbing (control plane) ────────────────────────────────────────
export function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${proto}://${location.host}/ws`);

  ws.onopen = () => {
    el('conn').textContent = 'online';
    // Hello carries the current name (restored from sessionStorage or just typed), so the server
    // is in sync from the first frame and after any reconnect.
    send({ type: 'Hello', displayName, token, proto: PROTOCOL_VERSION });
  };
  ws.onclose = () => {
    // Back off exponentially (matching the SDK's data socket) so a server restart doesn't get
    // hammered at 1 Hz by every connected browser. Reset on a confirmed session (Welcome).
    el('conn').textContent = 'offline — reconnecting…';
    setTimeout(connect, reconnectDelay(reconnectAttempt++));
  };
  ws.onmessage = (e) => handle(JSON.parse(e.data));
}

function send(msg) { ws.send(JSON.stringify(msg)); }

// Send a cid-correlated request and await the matching reply.
function request(type, extra = {}) {
  const cid = 'c' + (++cidSeq);
  return new Promise((resolve) => {
    pending.set(cid, resolve);
    send({ type, cid, ...extra });
  });
}

// Re-announce the chosen display name without cycling the socket. The server binds the name at Hello
// but honours SetName afterwards; sent on rename and just before create/join (WS preserves order, so
// the server applies it before the CreateLobby/JoinLobby that follows).
function sendName() {
  if (ws && ws.readyState === WebSocket.OPEN && displayName.trim()) {
    send({ type: 'SetName', displayName });
  }
}

export function handle(msg) {
  // Resolve any awaiting request first.
  if (msg.cid && pending.has(msg.cid)) {
    pending.get(msg.cid)(msg);
    pending.delete(msg.cid);
    // fall through: some replies (Joined) also drive UI below
  }

  switch (msg.type) {
    case 'Welcome':
      playerId = msg.playerId;
      token = msg.token;
      sessionStorage.setItem('kb.token', token);
      gameOrigin = msg.gameOrigin || location.origin;
      reconnectAttempt = 0; // session confirmed; next drop starts backoff fresh
      // Load the game catalog FIRST, then (re)join. A GameStarting — from an auto-join or a rejoin —
      // makes enterGame resolve the manifest from `games`, which must be populated by then. The
      // server replies in order, so an un-gated JoinLobby/Rejoin would land its GameStarting before
      // the ListGames reply and enterGame would reject it as "Unknown game".
      refreshGames().then(() => {
        // First connect of an auto-join tab joins the URL code; null it after so a later reconnect
        // rejoins the now-saved lobby via tryRejoin() instead of re-running the auto-join.
        if (pendingJoinCode) { const code = pendingJoinCode; pendingJoinCode = null; autoJoin(code); }
        else tryRejoin();
      });
      break;
    case 'PlayerJoined':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = rosterAdd(lobby.players, msg.player);
        updateWaiting();
      }
      break;
    case 'PlayerLeft':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = rosterRemove(lobby.players, msg.playerId);
        updateWaiting();
      }
      break;
    case 'GameStarting':
      enterGame(msg);
      break;
    case 'Error':
      showError(msg.reason || 'Something went wrong.');
      break;
    case 'Kicked':
      // The host removed us. Leave the game, forget the lobby (so we don't auto-rejoin), and say so.
      console.info('[KnockBox shell] Kicked received for lobby', msg.lobbyId);
      if (!lobby || msg.lobbyId === lobby.lobbyId) {
        sessionStorage.removeItem('kb.lobbyId');
        showLobbyView();
        showError('You were kicked from the lobby.');
      }
      break;
    case 'RejoinFailed':
      sessionStorage.removeItem('kb.lobbyId');
      showLobbyView();
      break;
    case 'GameLog':
      // A game we're playing recorded a Play Log entry; the server already stamped game/time/host
      // and routed it back to us. Persist it (browser-local) and refresh the home-page panel.
      recordPlayLog(msg);
      break;
  }
}

// ── Play Log (home page) ───────────────────────────────────────────────────────
// Games push entries via KnockBox.logPlay(); the server stamps gameId/timestamp/isHost and routes
// them to this player's OWN control socket. We keep the most-recent PLAY_LOG_MAX in localStorage
// (per browser, like the display name) and render them on the home page, newest first. Every
// game-supplied string (metadata keys/values, resolved game name) is untrusted and written via
// textContent — never innerHTML — so a game can't inject markup into the shell.
const PLAYLOG_KEY = 'kb.playLog';

function readPlayLog() {
  try {
    const parsed = JSON.parse(localStorage.getItem(PLAYLOG_KEY) || '[]');
    return Array.isArray(parsed) ? parsed : [];
  } catch { return []; }
}

function recordPlayLog(msg) {
  const entry = {
    gameId: msg.gameId || null,
    timestamp: msg.timestamp || null,
    isHost: !!msg.isHost,
    metadata: msg.metadata && typeof msg.metadata === 'object' ? msg.metadata : {},
  };
  const next = appendPlayLog(readPlayLog(), entry);
  try { localStorage.setItem(PLAYLOG_KEY, JSON.stringify(next)); } catch { /* storage full/blocked — skip */ }
  renderPlayLog(); // the panel is hidden while in-game; re-rendering it then is harmless
}

function plChip(text, className) {
  const span = document.createElement('span');
  span.className = className ? `pl-chip ${className}` : 'pl-chip';
  span.textContent = text;
  return span;
}

// Render the stored UTC timestamp in the player's locale, keeping the ISO instant in the <time>
// element's datetime/title for the exact value. Returns null for a missing/unparseable stamp.
function playLogTime(iso) {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  const t = document.createElement('time');
  t.className = 'pl-item-time';
  t.dateTime = iso;
  t.title = iso;
  t.textContent = d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
  return t;
}

function playLogItem(entry) {
  const li = document.createElement('li');
  li.className = 'pl-item';

  const head = document.createElement('div');
  head.className = 'pl-item-head';
  const name = document.createElement('span');
  name.className = 'pl-item-game';
  const manifest = entry.gameId ? games.get(entry.gameId) : null;
  name.textContent = manifest ? manifest.name : (entry.gameId || 'Unknown game');
  head.appendChild(name);
  const time = playLogTime(entry.timestamp);
  if (time) head.appendChild(time);
  li.appendChild(head);

  // Recognized standard keys become dedicated chips; everything else drops to the details table.
  const { standard, extra } = partitionPlayLogMetadata(entry.metadata);
  const chips = document.createElement('div');
  chips.className = 'pl-chips';
  if (entry.isHost) chips.appendChild(plChip('Host', 'pl-chip-host'));
  for (const [key, value] of standard) {
    if (key === 'placement') chips.appendChild(plChip(ordinal(value), 'pl-chip-placement'));
    else if (key === 'playerCount') chips.appendChild(plChip(`${value} player${value === '1' ? '' : 's'}`, 'pl-chip-players'));
    else chips.appendChild(plChip(`${key}: ${value}`));
  }
  if (chips.childElementCount) li.appendChild(chips);

  if (extra.length) {
    const details = document.createElement('details');
    details.className = 'pl-details';
    const summary = document.createElement('summary');
    summary.textContent = `Details (${extra.length})`;
    details.appendChild(summary);
    const table = document.createElement('table');
    table.className = 'pl-meta-table';
    for (const [key, value] of extra) {
      const tr = document.createElement('tr');
      const th = document.createElement('th');
      th.scope = 'row';
      th.textContent = key;
      const td = document.createElement('td');
      td.textContent = value;
      tr.append(th, td);
      table.appendChild(tr);
    }
    details.appendChild(table);
    li.appendChild(details);
  }
  return li;
}

export function renderPlayLog() {
  const list = el('playlog-list');
  const empty = el('playlog-empty');
  if (!list || !empty) return; // panel markup not present (some test fixtures)
  const entries = readPlayLog();
  list.innerHTML = '';
  const hasEntries = entries.length > 0;
  empty.hidden = hasEntries;
  list.hidden = !hasEntries;
  for (const entry of entries) list.appendChild(playLogItem(entry));
}

// ── Home view: name gate, game tiles (host), join-by-code ─────────────────────

// The player must name themselves before hosting or joining (the old CanJoinOrCreate gate).
function applyGate() {
  const ok = !!displayName.trim();
  el('join-btn').disabled = !ok;
  document.querySelectorAll('#games .game-tile').forEach((b) => { b.disabled = !ok; });
}

export async function refreshGames() {
  const reply = await request('ListGames');
  games = new Map((reply.games || []).map((g) => [g.id, g]));
  const host = el('games');
  host.innerHTML = '';
  if (games.size === 0) {
    host.innerHTML = '<p class="games-empty">No games discovered. Drop one in /games.</p>';
    return;
  }
  for (const g of games.values()) {
    const btn = document.createElement('button');
    btn.className = 'game-tile';
    btn.type = 'button';
    btn.setAttribute('aria-label', g.name);
    if (g.thumbnail) {
      const img = document.createElement('img');
      img.className = 'game-tile-img game-tile-surface';
      img.src = `/games/${g.id}/${g.thumbnail}`;
      img.alt = '';
      img.draggable = false;
      img.loading = 'lazy';
      // No tile art → fall back to the hot-pink "needs art" surface showing the name.
      img.onerror = () => { img.replaceWith(fallbackSurface(g.name)); };
      btn.appendChild(img);
    } else {
      btn.appendChild(fallbackSurface(g.name));
    }
    btn.onclick = () => createLobby(g.id);
    host.appendChild(btn);
  }
  applyGate();
}

function fallbackSurface(name) {
  const div = document.createElement('div');
  div.className = 'game-tile-surface game-tile-fallback';
  div.textContent = name;
  return div;
}

export async function createLobby(gameId) {
  if (!displayName.trim()) { showError('Please enter a name to start playing!'); return; }
  sendName();
  const reply = await request('CreateLobby', { gameId });
  if (reply.type === 'LobbyCreated') {
    sessionStorage.setItem('kb.lobbyId', reply.lobbyId);
    lobby = { lobbyId: reply.lobbyId, gameId, hostId: playerId, players: [{ id: playerId, displayName }] };
    showRoom();
  } else {
    showError(reply.reason || 'Could not create lobby.');
  }
}

export async function joinByCode() {
  const code = (el('room-code-input').value || '').trim().toUpperCase();
  if (!displayName.trim()) { showError('Please enter a name to start playing!'); return; }
  if (!code) { showError('Please enter a valid room code.'); return; }
  sendName();
  // Track the target lobby so any PlayerJoined push that races ahead of GameStarting attaches, but
  // DON'T switch to the game view yet — a wrong code must not flash the waiting screen. On success
  // we show the room; the GameStarting that follows swaps in the iframe (it lands after this reply's
  // continuation, so it never clobbers showRoom).
  lobby = { lobbyId: code, gameId: null, hostId: null, players: [{ id: playerId, displayName }] };
  const reply = await request('JoinLobby', { lobbyId: code });
  if (reply.type === 'Joined') {
    sessionStorage.setItem('kb.lobbyId', reply.lobbyId);
    showRoom();
  } else {
    lobby = null;
    showError(reply.reason || 'Could not join lobby.');
  }
}

export function tryRejoin() {
  const saved = sessionStorage.getItem('kb.lobbyId');
  if (saved) request('Rejoin', { lobbyId: saved });
}

// Auto-join the lobby a middle-click "test player" tab was opened for, reusing the normal join path.
function autoJoin(code) {
  // Keep the player's saved name; only invent one when none is set, and never persist it (no
  // localStorage write) so it stays a throwaway. The server makes the name unique within the lobby,
  // so a test tab sharing the opener's name shows up as "Name (2)".
  if (!displayName.trim()) displayName = `Tester ${1000 + Math.floor(Math.random() * 9000)}`;
  el('player-name-input').value = displayName;
  el('room-code-input').value = code;
  joinByCode();
}

// ── Waiting room (shown on create/join, before the game starts) ───────────────
export function showRoom() {
  const manifest = lobby.gameId ? games.get(lobby.gameId) : null;
  el('game-title').textContent = manifest ? manifest.name : (lobby.gameId || `Lobby ${lobby.lobbyId}`);
  el('lobby-code').textContent = lobby.lobbyId;
  el('frame-host').innerHTML = ''; // no iframe until GameStarting
  el('waiting').style.display = 'block';
  updateWaiting();
  themeHeader(manifest);
  document.body.classList.add('in-game');
  el('game-view').style.display = 'block';
  el('lobby-view').style.display = 'none';
}

function updateWaiting() {
  if (!lobby) return;
  // The game loads as soon as you enter and shows its own waiting UI, so this is just a brief
  // pre-iframe placeholder.
  el('waiting').textContent = 'Loading game…';
}

// ── In-game: embed the game on its own origin and hand it a scoped ticket ─────
export async function enterGame(starting) {
  // Only launch games discovered in our catalog (the allowlist refreshGames built). This rejects a
  // GameStarting for an unknown id instead of feeding a server-supplied id straight into the iframe URL.
  const manifest = games.get(starting.gameId);
  if (!manifest) { showError('Unknown game.'); return; }
  lobby = {
    lobbyId: starting.lobbyId,
    gameId: starting.gameId,
    hostId: starting.hostId,
    players: starting.players.slice(),
  };

  el('game-title').textContent = manifest.name;
  el('lobby-code').textContent = starting.lobbyId;
  themeHeader(manifest);

  // Lobby-scoped credential for the game's own data socket. The game never sees our identity token.
  const reply = await request('RequestGameTicket', { lobbyId: starting.lobbyId });
  if (reply.type !== 'GameTicket') { showError(reply.reason || 'Could not start game.'); return; }

  const entry = manifest.entry;
  // Credentials go in the URL fragment (not the query string) so they never leak via Referer/logs.
  const src = buildGameSrc(gameOrigin, starting.gameId, entry, reply.ticket, gameWsEndpoint(gameOrigin));

  el('waiting').style.display = 'none';
  el('frame-host').innerHTML = '';
  const frame = document.createElement('iframe');
  frame.src = src;
  frame.id = 'game-frame';
  if (manifest && manifest.crossOriginIsolated) frame.allow = 'cross-origin-isolated';
  el('frame-host').appendChild(frame);

  document.body.classList.add('in-game');
  el('game-view').style.display = 'block';
  el('lobby-view').style.display = 'none';
}

export function showLobbyView() {
  lobby = null;
  closeCodeModal();
  resetHeaderTheme();
  el('frame-host').innerHTML = '';
  document.body.classList.remove('in-game');
  el('game-view').style.display = 'none';
  el('lobby-view').style.display = 'block';
  renderPlayLog();
}

// ── Per-game header tint ──────────────────────────────────────────────────────
// Make the in-game chrome feel like part of the game: derive a header background from the game
// (an explicit manifest themeColor, else the dominant color of its thumbnail) and a contrasting
// text color (explicit themeTextColor, else auto black/white). Falls back to the default white
// header when nothing resolves. All author-supplied colors are validated before use.
let themeSeq = 0;

export async function themeHeader(manifest) {
  const seq = ++themeSeq;
  let bg = manifest && manifest.themeColor ? colorToRgb(manifest.themeColor) : null;
  if (!bg && manifest && manifest.thumbnail) {
    // The thumbnail is served same-origin (shell origin gates /games/* to it), so we can read its
    // pixels off a canvas without a CORS taint. A plain await keeps enterGame's flow simple.
    bg = await dominantColorFromImage(`/games/${manifest.id}/${manifest.thumbnail}`);
    if (seq !== themeSeq) return; // left the game (or switched) while sampling — drop this result
  }
  if (!bg) { resetHeaderTheme(); return; }

  let fg = manifest && manifest.themeTextColor ? colorToRgb(manifest.themeTextColor) : null;
  if (!fg) fg = pickContrastText(bg);
  applyHeaderColors(bg, fg);
}

export function applyHeaderColors(bg, fg) {
  const h = document.querySelector('.game-header');
  if (!h) return;
  const rgb = (c) => `rgb(${c.r}, ${c.g}, ${c.b})`;
  const rgba = (c, a) => `rgba(${c.r}, ${c.g}, ${c.b}, ${a})`;
  h.style.setProperty('--gh-bg', rgb(bg));
  h.style.setProperty('--gh-fg', rgb(fg));
  h.style.setProperty('--gh-fg-muted', rgba(fg, 0.65));
  h.style.setProperty('--gh-btn-bg', rgba(fg, 0.14));
  h.style.setProperty('--gh-btn-bg-hover', rgba(fg, 0.26));
}

export function resetHeaderTheme() {
  themeSeq++; // cancel any in-flight thumbnail sampling
  const h = document.querySelector('.game-header');
  if (!h) return;
  for (const p of ['--gh-bg', '--gh-fg', '--gh-fg-muted', '--gh-btn-bg', '--gh-btn-bg-hover']) {
    h.style.removeProperty(p);
  }
}

// Validate an author-supplied CSS color via the CSSOM (invalid values are rejected, never injected),
// returning normalized {r,g,b} or null. Non-opaque values (e.g. `transparent`, which normalizes to
// rgba(0,0,0,0)) are rejected too, so theming falls back to thumbnail sampling / the default header
// instead of painting a wrong (black/translucent) tint.
export function colorToRgb(value) {
  if (typeof value !== 'string' || !value) return null;
  const probe = document.createElement('span');
  probe.style.color = value; // CSSOM ignores anything that isn't a valid single color
  if (!probe.style.color) return null;
  probe.style.display = 'none';
  document.body.appendChild(probe);
  const norm = getComputedStyle(probe).color; // always rgb()/rgba()
  probe.remove();
  return parseRgbComponents(norm); // numeric parse + opaque check (pure, in kb-core)
}

// Draw the thumbnail small, hand its pixels to the pure bucketing helper, and resolve the dominant
// color (see dominantColorFromPixels in kb-core). Resolves null on any failure.
export function dominantColorFromImage(url) {
  return new Promise((resolve) => {
    const img = new Image();
    img.onload = () => {
      try {
        const w = 48, h = 48;
        const canvas = document.createElement('canvas');
        canvas.width = w; canvas.height = h;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        ctx.drawImage(img, 0, 0, w, h);
        const { data } = ctx.getImageData(0, 0, w, h);
        resolve(dominantColorFromPixels(data));
      } catch {
        resolve(null); // tainted canvas / decode failure — fall back to default header
      }
    };
    img.onerror = () => resolve(null);
    img.src = url;
  });
}

// ── Transient error toast ─────────────────────────────────────────────────────
function showError(message) {
  const prev = document.querySelector('.home-error-toast');
  if (prev) prev.remove();
  const toast = document.createElement('div');
  toast.className = 'home-error-toast';
  const icon = document.createElement('span');
  icon.className = 'home-error-icon';
  icon.setAttribute('aria-hidden', 'true');
  icon.textContent = '⚠️';
  const text = document.createElement('span');
  text.textContent = message;
  toast.append(icon, text);
  document.body.appendChild(toast);
  // Mirror the .home-error-toast CSS animation duration (3s) then remove.
  setTimeout(() => toast.remove(), 3000);
}

// Brief positive confirmation, mirroring showError's lifecycle but with the success styling.
function flashCopied() {
  const prev = document.querySelector('.home-copy-toast');
  if (prev) prev.remove();
  const toast = document.createElement('div');
  toast.className = 'home-copy-toast';
  toast.textContent = 'Copied!';
  document.body.appendChild(toast);
  setTimeout(() => toast.remove(), 3000);
}

// ── UI wiring ────────────────────────────────────────────────────────────────
const nameInput = el('player-name-input');
nameInput.value = displayName;
nameInput.addEventListener('input', () => {
  displayName = nameInput.value.trim();
  // Persist for the next browser session. This tab keeps its own in-memory name regardless of what
  // other tabs write (no `storage` listener) — see the identity note above.
  localStorage.setItem('kb.displayName', displayName);
  applyGate();
  sendName();
});

// A game iframe (on the game origin) can tell us its session ended terminally — kicked, ticket
// expired, or lobby gone — so we leave the game view even if the control-plane push was missed.
// This only fires on a terminal socket close, never on a normal game-over (the data socket stays up).
window.addEventListener('message', (e) => {
  // Only trust the game origin, and never before Welcome has set it (until then gameOrigin is null,
  // so a same-origin message sent during initial load can't spoof a session-ended).
  if (!gameOrigin || e.origin !== gameOrigin) return;
  if (e.data && e.data.kb === 'session-ended' && lobby) {
    sessionStorage.removeItem('kb.lobbyId');
    showLobbyView();
    showError('The game session ended.');
  }
});

el('join-form').addEventListener('submit', (e) => { e.preventDefault(); joinByCode(); });

export function leaveGame() {
  if (lobby) send({ type: 'LeaveLobby', lobbyId: lobby.lobbyId });
  sessionStorage.removeItem('kb.lobbyId');
  showLobbyView();
}

el('leave').onclick = leaveGame;

// The game name doubles as a "home" link: leave the session and return to the lobby view in-SPA.
// href="/" is the no-JS fallback; we intercept so the control socket stays up.
el('game-title').addEventListener('click', (e) => {
  e.preventDefault();
  leaveGame();
});

// ── Room code button: click crossfades the code; dbl-click opens a big modal; right-click and
// mobile long-press copy to the clipboard. ───────────────────────────────────────────────────
async function copyRoomCode() {
  if (!lobby) return;
  try {
    await navigator.clipboard.writeText(lobby.lobbyId);
    flashCopied();
  } catch {
    showError('Could not copy.');
  }
}

// Shareable auto-join URL for this lobby: opening it lands a player straight in the lobby (see the
// "?join=" handling at startup). Carries only the public room code — no identity token.
function joinLink() {
  return buildJoinLink(location.origin, lobby.lobbyId);
}

async function copyJoinLink() {
  if (!lobby) return;
  try {
    await navigator.clipboard.writeText(joinLink());
    flashCopied();
  } catch {
    showError('Could not copy.');
  }
}

function openCodeModal() {
  if (!lobby) return;
  el('rc-modal-code').textContent = lobby.lobbyId;
  el('rc-modal').hidden = false;
  el('rc-modal-copy').focus();
}

function closeCodeModal() {
  el('rc-modal').hidden = true;
}

const rc = el('room-code-btn');
let longPressTimer = null;
let longPressed = false;
let lastClickAt = 0;
const DBL_MS = 250;

// Single click toggles the crossfade immediately (instant feedback). The second click of a
// double-click lands within DBL_MS — we skip its toggle so dblclick can open the modal without
// reverting the reveal first. DBL_MS is a fixed guess, independent of the OS double-click
// interval: if that interval is longer than 250ms the second click toggles instead of being
// skipped, but the dblclick handler then `remove('revealed')`s anyway, so the stray re-toggle is
// never visible — the modal opens over a hidden code regardless.
rc.addEventListener('click', () => {
  if (longPressed) return; // a long-press already handled this gesture
  const now = performance.now();
  if (now - lastClickAt < DBL_MS) { // second click of a dbl-click; let dblclick handle it
    lastClickAt = 0;
    return;
  }
  lastClickAt = now;
  rc.classList.toggle('revealed');
});

rc.addEventListener('dblclick', () => {
  rc.classList.remove('revealed'); // reset to "Room Code" behind the modal so it's hidden on close
  openCodeModal();
});

rc.addEventListener('contextmenu', (e) => {
  e.preventDefault();
  if (longPressed) return; // long-press already copied
  copyRoomCode();
});

rc.addEventListener('touchstart', () => {
  longPressed = false;
  longPressTimer = setTimeout(() => {
    longPressed = true;
    copyRoomCode();
  }, 500);
}, { passive: true });

const cancelLongPress = () => { if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; } };
rc.addEventListener('touchend', cancelLongPress);
rc.addEventListener('touchmove', cancelLongPress);
rc.addEventListener('touchcancel', cancelLongPress);

// Middle-click opens a new tab that auto-joins this lobby — a quick way to add a test player.
rc.addEventListener('mousedown', (e) => { if (e.button === 1) e.preventDefault(); }); // no autoscroll
rc.addEventListener('auxclick', (e) => {
  if (e.button !== 1 || !lobby) return; // middle button only
  e.preventDefault();
  window.open(joinLink(), '_blank');
});

// Modal controls.
el('rc-modal-copy').addEventListener('click', () => { copyRoomCode(); closeCodeModal(); });
el('rc-modal-copy-link').addEventListener('click', () => { copyJoinLink(); closeCodeModal(); });
el('rc-modal').querySelectorAll('[data-rc-close]').forEach((node) =>
  node.addEventListener('click', closeCodeModal));
document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && !el('rc-modal').hidden) closeCodeModal();
});

applyGate();
renderPlayLog(); // home view is shown by default on load — populate the Play Log from storage

// Start the control socket. On a real page load index.html imports this module and calls bootstrap();
// importing the module on its own no longer opens a socket, so the test suite can drive the exported
// functions (and call connect() itself) without an auto-connect to suppress.
export function bootstrap() {
  connect();
}
