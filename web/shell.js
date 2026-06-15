// Platform shell — owns the single CONTROL websocket, identity, and the lobby UI. When a game
// starts it requests a lobby-scoped ticket and embeds the game in a cross-origin iframe (the game
// origin). It does NOT bridge gameplay: the game opens its own data websocket via the ticket and
// talks to the server directly. The shell and game are isolated (separate origins) on purpose.
import { PROTOCOL_VERSION, buildGameSrc, gameWsEndpoint, reconnectDelay, rosterAdd, rosterRemove } from './kb-core.js';

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

const el = (id) => document.getElementById(id);

// Current session state.
let ws = null;
let reconnectAttempt = 0;       // 0-based; drives exponential backoff, reset once a session is confirmed
let games = new Map();          // gameId -> manifest
let lobby = null;               // { lobbyId, gameId, hostId, players: [] } once in a game
const pending = new Map();      // cid -> resolver
let cidSeq = 0;

// ── WebSocket plumbing (control plane) ────────────────────────────────────────
function connect() {
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

function handle(msg) {
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
      tryRejoin();
      refreshGames();
      break;
    case 'PlayerJoined':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = rosterAdd(lobby.players, msg.player);
        renderRoster();
        updateWaiting();
      }
      break;
    case 'PlayerLeft':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = rosterRemove(lobby.players, msg.playerId);
        renderRoster();
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
  }
}

// ── Home view: name gate, game tiles (host), join-by-code ─────────────────────

// The player must name themselves before hosting or joining (the old CanJoinOrCreate gate).
function applyGate() {
  const ok = !!displayName.trim();
  el('join-btn').disabled = !ok;
  document.querySelectorAll('#games .game-tile').forEach((b) => { b.disabled = !ok; });
}

async function refreshGames() {
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

async function createLobby(gameId) {
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

async function joinByCode() {
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

function tryRejoin() {
  const saved = sessionStorage.getItem('kb.lobbyId');
  if (saved) request('Rejoin', { lobbyId: saved });
}

// ── Waiting room (shown on create/join, before the game starts) ───────────────
function showRoom() {
  const manifest = lobby.gameId ? games.get(lobby.gameId) : null;
  el('game-title').textContent = manifest ? manifest.name : (lobby.gameId || `Lobby ${lobby.lobbyId}`);
  el('lobby-code').textContent = lobby.lobbyId;
  el('frame-host').innerHTML = ''; // no iframe until GameStarting
  el('waiting').style.display = 'block';
  renderRoster();
  updateWaiting();
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
async function enterGame(starting) {
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
  renderRoster();

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

function showLobbyView() {
  lobby = null;
  el('frame-host').innerHTML = '';
  document.body.classList.remove('in-game');
  el('game-view').style.display = 'none';
  el('lobby-view').style.display = 'block';
}

function renderRoster() {
  if (!lobby) return;
  // Build with textContent, never innerHTML: displayName is player-controlled and would otherwise be
  // an XSS vector in the shell origin (where the identity token lives).
  const host = el('roster');
  host.innerHTML = '';
  for (const p of lobby.players) {
    const span = document.createElement('span');
    if (p.id === lobby.hostId) span.className = 'host';
    span.textContent = p.displayName;
    host.appendChild(span);
  }
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

el('leave').onclick = () => {
  if (lobby) send({ type: 'LeaveLobby', lobbyId: lobby.lobbyId });
  sessionStorage.removeItem('kb.lobbyId');
  showLobbyView();
};

// Room code is blurred until hovered/focused (shoulder-surf guard); click pins it open.
const rc = el('room-code-btn');
rc.addEventListener('mouseenter', () => rc.classList.add('revealed'));
rc.addEventListener('mouseleave', () => { if (!rc.dataset.pinned) rc.classList.remove('revealed'); });
rc.addEventListener('focus', () => rc.classList.add('revealed'));
rc.addEventListener('click', () => {
  rc.dataset.pinned = rc.dataset.pinned ? '' : '1';
  rc.classList.toggle('revealed', !!rc.dataset.pinned);
});

applyGate();
connect();
