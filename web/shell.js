// Platform shell — owns the single CONTROL websocket, identity, and the lobby UI. When a game
// starts it requests a lobby-scoped ticket and embeds the game in a cross-origin iframe (the game
// origin). It does NOT bridge gameplay: the game opens its own data websocket via the ticket and
// talks to the server directly. The shell and game are isolated (separate origins) on purpose.
import { buildGameSrc, gameWsEndpoint, rosterAdd, rosterRemove } from './kb-core.js';

// ── Identity (client-side) ───────────────────────────────────────────────────
// The server mints the playerId and a signed token on first connect; we persist the TOKEN (not the
// id) in sessionStorage — per-tab, so each tab is a distinct anonymous player — and resend it to
// prove ownership of that id on reconnect. The token never leaves this (shell) origin; games get a
// scoped ticket instead. (No login by design.)
function displayNameInit() {
  let v = sessionStorage.getItem('kb.displayName');
  if (!v) { v = 'Player-' + Math.floor(1000 + Math.random() * 9000); sessionStorage.setItem('kb.displayName', v); }
  return v;
}

let playerId = null;                                  // assigned by the server (Welcome)
let token = sessionStorage.getItem('kb.token');       // signed identity token (anti-spoof)
let displayName = displayNameInit();
let gameOrigin = location.origin;                     // where game iframes/sockets live (from Welcome)

const el = (id) => document.getElementById(id);
const setStatus = (t) => { el('status').textContent = t; };

// Current session state.
let ws = null;
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
    send({ type: 'Hello', displayName, token });
  };
  ws.onclose = () => {
    el('conn').textContent = 'offline — reconnecting…';
    setTimeout(connect, 1000);
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
      gameOrigin = msg.gameOrigin || gameOrigin;
      tryRejoin();
      refreshGames();
      refreshLobbies();
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
      setStatus('⚠ ' + msg.reason);
      break;
    case 'RejoinFailed':
      sessionStorage.removeItem('kb.lobbyId');
      showLobbyView();
      break;
  }
}

// ── Lobby browser ──────────────────────────────────────────────────────────
async function refreshGames() {
  const reply = await request('ListGames');
  games = new Map((reply.games || []).map((g) => [g.id, g]));
  const host = el('games');
  host.innerHTML = '';
  if (games.size === 0) host.innerHTML = '<p class="muted">No games discovered. Drop one in /games.</p>';
  for (const g of games.values()) {
    const card = document.createElement('div');
    card.className = 'card';
    const thumb = g.thumbnail ? `/games/${g.id}/${g.thumbnail}` : '';
    card.innerHTML = `
      <img src="${thumb}" alt="" onerror="this.style.visibility='hidden'" />
      <div class="name">${g.name}</div>
      <div class="muted">${g.maxPlayers === 1 ? 'Single player' : `Up to ${g.maxPlayers} players`}</div>`;
    const btn = document.createElement('button');
    btn.textContent = 'Create lobby';
    btn.onclick = () => createLobby(g.id);
    card.appendChild(btn);
    host.appendChild(card);
  }
}

async function refreshLobbies() {
  const reply = await request('ListLobbies');
  const host = el('lobbies');
  host.innerHTML = '';
  const list = reply.lobbies || [];
  if (list.length === 0) { host.innerHTML = '<p class="muted">No open lobbies.</p>'; return; }
  for (const l of list) {
    const game = games.get(l.gameId);
    const row = document.createElement('div');
    row.className = 'row';
    row.innerHTML = `<span><code>${l.lobbyId}</code> · ${game ? game.name : l.gameId}
      <span class="muted">(${l.players} in)</span></span>`;
    const btn = document.createElement('button');
    btn.textContent = 'Join';
    btn.onclick = () => joinLobby(l.lobbyId, l.gameId);
    row.appendChild(btn);
    host.appendChild(row);
  }
}

async function createLobby(gameId) {
  const reply = await request('CreateLobby', { gameId });
  if (reply.type === 'LobbyCreated') {
    sessionStorage.setItem('kb.lobbyId', reply.lobbyId);
    lobby = { lobbyId: reply.lobbyId, gameId, hostId: playerId, players: [{ id: playerId, displayName }] };
    showRoom();
  } else {
    setStatus('⚠ ' + (reply.reason || 'Could not create lobby'));
  }
}

async function joinLobby(lobbyId, gameId) {
  // Enter the room optimistically so pushes (PlayerJoined/GameStarting) have a lobby to attach to.
  lobby = { lobbyId, gameId, hostId: null, players: [{ id: playerId, displayName }] };
  showRoom();
  const reply = await request('JoinLobby', { lobbyId });
  if (reply.type === 'Joined') {
    sessionStorage.setItem('kb.lobbyId', reply.lobbyId);
  } else {
    lobby = null;
    showLobbyView();
    setStatus('⚠ ' + (reply.reason || 'Could not join lobby'));
  }
}

function tryRejoin() {
  const saved = sessionStorage.getItem('kb.lobbyId');
  if (saved) request('Rejoin', { lobbyId: saved });
}

// ── Waiting room (shown on create/join, before the game starts) ───────────────
function showRoom() {
  const manifest = games.get(lobby.gameId);
  el('game-title').textContent = manifest ? manifest.name : lobby.gameId;
  el('lobby-code').textContent = lobby.lobbyId;
  el('frame-host').innerHTML = ''; // no iframe until GameStarting
  el('waiting').style.display = 'block';
  renderRoster();
  updateWaiting();
  el('game-view').style.display = 'block';
  el('lobby-view').style.display = 'none';
  setStatus('');
}

function updateWaiting() {
  if (!lobby) return;
  // The game loads as soon as you enter and shows its own waiting UI, so this is just a brief
  // pre-iframe placeholder.
  el('waiting').textContent = 'Loading game…';
}

// ── In-game: embed the game on its own origin and hand it a scoped ticket ─────
async function enterGame(starting) {
  const manifest = games.get(starting.gameId);
  lobby = {
    lobbyId: starting.lobbyId,
    gameId: starting.gameId,
    hostId: starting.hostId,
    players: starting.players.slice(),
  };

  el('game-title').textContent = manifest ? manifest.name : starting.gameId;
  el('lobby-code').textContent = starting.lobbyId;
  renderRoster();

  // Lobby-scoped credential for the game's own data socket. The game never sees our identity token.
  const reply = await request('RequestGameTicket', { lobbyId: starting.lobbyId });
  if (reply.type !== 'GameTicket') { setStatus('⚠ ' + (reply.reason || 'Could not start game')); return; }

  const entry = manifest ? manifest.entry : 'index.html';
  // Credentials go in the URL fragment (not the query string) so they never leak via Referer/logs.
  const src = buildGameSrc(gameOrigin, starting.gameId, entry, reply.ticket, gameWsEndpoint(gameOrigin));

  el('waiting').style.display = 'none';
  el('frame-host').innerHTML = '';
  const frame = document.createElement('iframe');
  frame.src = src;
  frame.id = 'game-frame';
  if (manifest && manifest.crossOriginIsolated) frame.allow = 'cross-origin-isolated';
  el('frame-host').appendChild(frame);

  el('game-view').style.display = 'block';
  el('lobby-view').style.display = 'none';
  setStatus('');
}

function showLobbyView() {
  lobby = null;
  el('frame-host').innerHTML = '';
  el('game-view').style.display = 'none';
  el('lobby-view').style.display = 'block';
  refreshLobbies();
}

function renderRoster() {
  if (!lobby) return;
  el('roster').innerHTML = lobby.players
    .map((p) => `<span class="${p.id === lobby.hostId ? 'host' : ''}">${p.displayName}</span>`)
    .join('');
}

// ── UI wiring ────────────────────────────────────────────────────────────────
el('name').value = displayName;
el('name').onchange = (e) => {
  displayName = e.target.value.trim() || displayName;
  sessionStorage.setItem('kb.displayName', displayName);
};
el('refresh').onclick = refreshLobbies;
el('leave').onclick = () => {
  if (lobby) send({ type: 'LeaveLobby', lobbyId: lobby.lobbyId });
  sessionStorage.removeItem('kb.lobbyId');
  showLobbyView();
};

connect();

// Keep the lobby browser fresh so newly created lobbies appear without manual refresh.
setInterval(() => {
  if (!lobby && ws && ws.readyState === WebSocket.OPEN) refreshLobbies();
}, 3000);
