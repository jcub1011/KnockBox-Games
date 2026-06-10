// Platform shell — owns the single WebSocket, identity, and lobby UI, and bridges the embedded
// game's postMessage calls to/from WebSocket relay envelopes. The HTML5 game itself only talks to
// this shell via /knockbox.js; it never sees the socket.

// ── Identity (client-side only) ─────────────────────────────────────────────
// Stored in sessionStorage, which is scoped to a single tab, so every tab is a
// distinct player even within the same browser. (Persistent cross-session identity
// in localStorage is deferred — see plan §9.)
function id() {
  let v = sessionStorage.getItem('kb.playerId');
  if (!v) { v = crypto.randomUUID().replace(/-/g, ''); sessionStorage.setItem('kb.playerId', v); }
  return v;
}
function name() {
  let v = sessionStorage.getItem('kb.displayName');
  if (!v) { v = 'Player-' + Math.floor(1000 + Math.random() * 9000); sessionStorage.setItem('kb.displayName', v); }
  return v;
}

let playerId = id();
let displayName = name();

const el = (id) => document.getElementById(id);
const setStatus = (t) => { el('status').textContent = t; };

// Current session state.
let ws = null;
let games = new Map();          // gameId -> manifest
let lobby = null;               // { lobbyId, gameId, hostId, players: [] } once in a game
let gameLoaded = false;         // iframe announced ready?
const pending = new Map();      // cid -> resolver
let cidSeq = 0;

// ── WebSocket plumbing ───────────────────────────────────────────────────────
function connect() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${proto}://${location.host}/ws`);

  ws.onopen = () => {
    el('conn').textContent = 'online';
    send({ type: 'Hello', playerId, displayName });
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
      tryRejoin();
      refreshGames();
      refreshLobbies();
      break;
    case 'PlayerJoined':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        if (!lobby.players.some((p) => p.id === msg.player.id)) lobby.players.push(msg.player);
        renderRoster();
        updateWaiting();
        forwardToGame({ kb: true, kind: 'playerJoined', player: msg.player, players: lobby.players });
      }
      break;
    case 'PlayerLeft':
      if (lobby && msg.lobbyId === lobby.lobbyId) {
        lobby.players = lobby.players.filter((p) => p.id !== msg.playerId);
        renderRoster();
        updateWaiting();
        forwardToGame({ kb: true, kind: 'playerLeft', playerId: msg.playerId, players: lobby.players });
      }
      break;
    case 'GameStarting':
      enterGame(msg);
      break;
    case 'Relay':
      forwardToGame({ kb: true, kind: 'relay', from: msg.from, payload: msg.payload });
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
      <div class="muted">${g.minPlayers}–${g.maxPlayers} players</div>`;
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
  // Enter the room optimistically so pushes (PlayerJoined/GameStarting) have a lobby to attach to,
  // and the user immediately sees they're in a room rather than "nothing happening".
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
  const manifest = games.get(lobby.gameId);
  const min = manifest ? manifest.minPlayers : 2;
  el('waiting').textContent = `Waiting for players… (${lobby.players.length}/${min})`;
}

// ── In-game (iframe + bridge) ────────────────────────────────────────────────
function enterGame(starting) {
  const manifest = games.get(starting.gameId);
  lobby = {
    lobbyId: starting.lobbyId,
    gameId: starting.gameId,
    hostId: starting.hostId,
    players: starting.players.slice(),
  };
  gameLoaded = false;

  el('game-title').textContent = manifest ? manifest.name : starting.gameId;
  el('lobby-code').textContent = starting.lobbyId;
  renderRoster();

  const entry = manifest ? manifest.entry : 'index.html';
  el('waiting').style.display = 'none';
  el('frame-host').innerHTML = '';
  const frame = document.createElement('iframe');
  frame.src = `/games/${starting.gameId}/${entry}`;
  frame.id = 'game-frame';
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

function frame() { return document.getElementById('game-frame'); }

function forwardToGame(msg) {
  const f = frame();
  if (f && f.contentWindow) f.contentWindow.postMessage(msg, location.origin);
}

function sendInit() {
  forwardToGame({
    kb: true, kind: 'init',
    playerId, players: lobby.players, isHost: playerId === lobby.hostId, lobbyId: lobby.lobbyId,
  });
}

// Messages coming up from the embedded game.
window.addEventListener('message', (e) => {
  if (e.origin !== location.origin) return;
  const msg = e.data;
  if (!msg || msg.kb !== true) return;

  if (msg.kind === 'gameLoaded') {
    gameLoaded = true;
    sendInit();
  } else if (msg.kind === 'relay' && lobby) {
    send({ type: 'Relay', lobbyId: lobby.lobbyId, to: msg.to, payload: msg.payload });
  }
});

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
