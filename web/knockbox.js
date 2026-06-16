// KnockBox Networking API — the game's "just send/receive over a websocket" client library.
//
// Loaded inside a game served from the GAME ORIGIN, as an ES module (so it can share kb-core.js):
//   <script type="module" src="/knockbox.js"></script>
//   <script type="module" src="game.js"></script>   <!-- runs after, reads window.KnockBox -->
//
// It reads a lobby-scoped ticket + endpoint from its own URL FRAGMENT (the shell put them there;
// the fragment, unlike a query string, never leaks via Referer or server logs), opens its OWN
// websocket to the server, authenticates with the ticket, and exposes a tiny API. The game never
// sees a lobby id, the player's identity, or the shell — the server resolves all routing from the
// bound connection.
//
//   KnockBox.onReady(({ playerId, players, isHost }) => { ... })
//   KnockBox.onMessage(({ from, payload }) => { ... })
//   KnockBox.onPlayerJoined(player => ...) / onPlayerLeft(playerId => ...)
//   KnockBox.sendToHost(payload)        // -> the authoritative host (intent)
//   KnockBox.sendToAll(payload)         // -> everyone incl. self (state)
//   KnockBox.sendTo(playerId, payload)  // -> one specific player
//   KnockBox.log.info('message')        // -> the SERVER log (also warn/error/debug/trace/critical)
//
// After onReady fires, KnockBox.playerId / players / isHost are populated.
//
// Engine note: this is the reference (vanilla-JS) client. A Godot addon (WebSocketPeer) or a Unity
// jslib package speaks the same JSON protocol: send {type:"Attach",ticket}; then exchange
// {type:"Game",to,payload} frames; read {type:"Ready",...} / {type:"GamePlayerJoined|Left",...}.
import {
  PROTOCOL_VERSION,
  parseLaunchParams,
  defaultEndpoint,
  reconnectDelay,
  isTerminalClose,
  makeLogger,
  rosterAdd,
  rosterRemove,
} from './kb-core.js';

(function () {
  const launch = parseLaunchParams(location.hash);
  const ticket = launch.ticket;
  const endpoint = launch.endpoint || defaultEndpoint(location.protocol, location.host);

  // The ticket/endpoint are now captured in memory; scrub them from the address bar so they don't
  // linger in browser history or stay readable via location.hash by anything that loads later
  // (analytics, third-party scripts). replaceState keeps the fragment out of the history entry.
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);

  const handlers = { ready: [], message: [], playerJoined: [], playerLeft: [] };
  let ready = false;
  let ws = null;
  let attempt = 0;        // consecutive failed/transient connects, for backoff
  let stopped = false;    // set on a terminal close — don't reconnect
  let attached = false;          // Attach sent on the live socket — data frames are accepted now
  const pendingLogs = [];        // logs emitted before Attach; flushed on attach (best-effort, bounded)
  const MAX_PENDING_LOGS = 100;  // cap so a game logging while disconnected can't grow this unbounded

  const KnockBox = {
    playerId: null,
    players: [],
    isHost: false,

    onReady(cb) { handlers.ready.push(cb); if (ready) cb(snapshot()); },
    onMessage(cb) { handlers.message.push(cb); },
    onPlayerJoined(cb) { handlers.playerJoined.push(cb); },
    onPlayerLeft(cb) { handlers.playerLeft.push(cb); },

    sendToHost(payload) { send('host', payload); },
    sendToAll(payload) { send('all', payload); },
    sendTo(playerId, payload) { send(playerId, payload); },

    // Host-only: set whether the lobby accepts new joins (open = listed + joinable). The game
    // owns this; the server never changes it on its own.
    setLobbyOpen(open) {
      if (ws && ws.readyState === WebSocket.OPEN)
        ws.send(JSON.stringify({ type: 'SetLobbyOpen', open: !!open }));
    },

    // Console-like logging to the SERVER (not the player's console): log.info / warn / error /
    // debug / trace / critical. Lines land in the server's log sink with the game/lobby/player
    // context stamped on. A log emitted before the socket attaches (or while reconnecting) is
    // queued and flushed on attach — bounded (drop-oldest) so it can't grow without limit. Logging
    // is best-effort: it must never block game state, and the queue is dropped on a terminal close.
    log: makeLogger(sendLog),
  };

  function snapshot() {
    return { playerId: KnockBox.playerId, players: KnockBox.players, isHost: KnockBox.isHost };
  }

  function send(to, payload) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'Game', to, payload }));
  }

  function sendLog(frame) {
    const json = JSON.stringify(frame);
    if (ws && attached && ws.readyState === WebSocket.OPEN) { ws.send(json); return; }
    // Not attached (initial connect or reconnecting): queue, dropping the OLDEST at the cap.
    // Logging is best-effort — it must never grow without bound or block game state.
    pendingLogs.push(json);
    if (pendingLogs.length > MAX_PENDING_LOGS) pendingLogs.shift();
  }

  function flushPendingLogs() {
    for (let i = 0; i < pendingLogs.length; i++) ws.send(pendingLogs[i]);
    pendingLogs.length = 0;
  }

  function scheduleReconnect() {
    if (stopped) return;
    const delay = reconnectDelay(attempt++);
    setTimeout(connect, delay);
  }

  function connect() {
    if (!ticket) { console.error('[KnockBox] missing kbTicket — cannot attach.'); return; }
    ws = new WebSocket(endpoint);

    ws.onopen = () => {
      ws.send(JSON.stringify({ type: 'Attach', ticket, proto: PROTOCOL_VERSION }));
      attached = true;
      flushPendingLogs();
    };
    ws.onmessage = (e) => handle(JSON.parse(e.data));
    ws.onerror = () => { /* a failed connect surfaces as a close; reconnect is handled there */ };
    ws.onclose = (e) => {
      attached = false;
      if (isTerminalClose(e.code)) {
        // The ticket is invalid or our lobby membership ended — retrying is pointless.
        stopped = true;
        pendingLogs.length = 0; // give up — these logs will never send
        console.warn('[KnockBox] data socket closed permanently:', e.reason || e.code);
        return;
      }
      scheduleReconnect();
    };
  }

  function handle(msg) {
    switch (msg.type) {
      case 'Ready':
        KnockBox.playerId = msg.playerId;
        KnockBox.players = msg.players || [];
        KnockBox.isHost = !!msg.isHost;
        ready = true;
        attempt = 0; // healthy connection — reset backoff
        handlers.ready.forEach((cb) => cb(snapshot()));
        break;
      case 'Game':
        handlers.message.forEach((cb) => cb({ from: msg.from, payload: msg.payload }));
        break;
      case 'GamePlayerJoined':
        KnockBox.players = rosterAdd(KnockBox.players, msg.player);
        handlers.playerJoined.forEach((cb) => cb(msg.player));
        break;
      case 'GamePlayerLeft':
        KnockBox.players = rosterRemove(KnockBox.players, msg.playerId);
        handlers.playerLeft.forEach((cb) => cb(msg.playerId));
        break;
    }
  }

  window.KnockBox = KnockBox;
  connect();
})();
