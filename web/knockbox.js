// KnockBox Networking API — the game's "just send/receive over a websocket" client library.
//
// Loaded inside a game served from the GAME ORIGIN, as an ES module (so it can share kb-protocol.js):
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
//   KnockBox.logPlay({ placement: '1' }) // -> the player's home-page Play Log (arbitrary metadata)
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
  normalizeReady,
  rosterAdd,
  rosterRemove,
  blobBaseUrl,
  sha256Hex,
} from './kb-protocol.js';

(function () {
  const launch = parseLaunchParams(location.hash);
  const ticket = launch.ticket;
  const endpoint = launch.endpoint || defaultEndpoint(location.protocol, location.host);
  const httpBase = blobBaseUrl(endpoint);
  const blobUrls = new Map();

  async function responseError(res) {
    try {
      const data = await res.json();
      if (data && data.error) return data.error;
    } catch {}
    return res.statusText ? `${res.status} ${res.statusText}` : String(res.status);
  }

  // The ticket/endpoint are now captured in memory; scrub them from the address bar so they don't
  // linger in browser history or stay readable via location.hash by anything that loads later
  // (analytics, third-party scripts). replaceState keeps the fragment out of the history entry.
  if (location.hash) history.replaceState(null, '', location.pathname + location.search);

  const handlers = { ready: [], message: [], playerJoined: [], playerLeft: [], playerDisconnected: [], playerConnected: [], ownerChanged: [] };
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
    // Who runs the game's authoritative logic: 'host' (a member's browser — the default) or
    // 'server' (the game's authority module runs server-side; every client — including the lobby
    // creator — is a guest and isHost is false). Don't branch game logic on isHost in server mode.
    authority: 'host',
    // The member holding the lobby powers (setLobbyOpen, kick) — the creator until the game's
    // authority module reassigns it. A separate concept from the authority: gate owner-only UI on
    // isOwner, never isHost.
    ownerId: null,
    isOwner: false,

    onReady(cb) { handlers.ready.push(cb); if (ready) cb(snapshot()); },
    onMessage(cb) { handlers.message.push(cb); },
    onPlayerJoined(cb) { handlers.playerJoined.push(cb); },
    onPlayerLeft(cb) { handlers.playerLeft.push(cb); },
    // A peer's shell dropped (tab refresh/close, network blip) but they're held in the lobby for the
    // reconnect grace window — and onPlayerConnected when they return within it. The player stays in
    // `players` the whole time; these are just signals so a game can show "reconnecting…". If the
    // window elapses without a reconnect, onPlayerLeft fires instead.
    onPlayerDisconnected(cb) { handlers.playerDisconnected.push(cb); },
    onPlayerConnected(cb) { handlers.playerConnected.push(cb); },
    // The lobby owner changed (a server-authority module called kb.setOwner — e.g. promoting a
    // successor when the previous owner left). ownerId/isOwner are updated before this fires.
    onOwnerChanged(cb) { handlers.ownerChanged.push(cb); },

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

    // Record a Play Log entry: an arbitrary { key: value } bag of match metadata (e.g.
    // { placement: '1', playerCount: '4' }). The server stamps the game, a UTC timestamp, and
    // whether you were host, then routes it to your home page's Play Log. Values are coerced to
    // strings. Like log.*, this is best-effort and queued (drop-oldest) until the socket attaches —
    // it never blocks game state.
    logPlay(metadata) {
      const bag = {};
      if (metadata && typeof metadata === 'object') {
        for (const key in metadata) {
          if (!Object.prototype.hasOwnProperty.call(metadata, key)) continue;
          const value = metadata[key];
          if (value === null || value === undefined) continue; // skip nullish — don't send "null"/"undefined"
          bag[key] = String(value);
        }
      }
      sendLog({ type: 'PlayLog', metadata: bag });
    },

    // Register a blob with a logical ID. Uploads to blob storage if not already present,
    // and registers with the server under the session ticket. Returns the accessible URL.
    async registerBlob(logicalId, blob) {
      if (typeof logicalId !== 'string' || !logicalId.trim()) {
        throw new TypeError('logicalId must be a non-empty string');
      }
      if (!blob || typeof blob.arrayBuffer !== 'function') {
        throw new TypeError('blob must be a Blob or File');
      }
      if (!ticket) {
        throw new Error('KnockBox is not authenticated (missing ticket)');
      }

      const sha256 = await sha256Hex(blob);

      const upload = async () => {
        const putRes = await fetch(`${httpBase}/blob/${sha256}`, {
          method: 'PUT',
          headers: {
            'X-KnockBox-Ticket': ticket,
            'Content-Type': blob.type || 'application/octet-stream',
          },
          body: blob,
        });
        if (!putRes.ok) {
          const err = await responseError(putRes);
          throw new Error(`Failed to upload blob: ${err}`);
        }
      };

      // 1. Probe HEAD ${httpBase}/blob/${sha256}
      const headRes = await fetch(`${httpBase}/blob/${sha256}`, {
        method: 'HEAD',
        headers: { 'X-KnockBox-Ticket': ticket },
      });

      if (headRes.status === 404) {
        // 2. Upload PUT ${httpBase}/blob/${sha256}
        await upload();
      } else if (!headRes.ok) {
        throw new Error(`Failed to probe blob: ${headRes.status} ${headRes.statusText}`);
      }

      // 3. Register POST ${httpBase}/blob/register
      const postRegister = () => fetch(`${httpBase}/blob/register`, {
        method: 'POST',
        headers: {
          'X-KnockBox-Ticket': ticket,
          'Content-Type': 'application/json',
        },
        body: JSON.stringify({
          logicalId,
          sha256,
          contentType: blob.type || null,
        }),
      });

      let regRes = await postRegister();

      // If evicted between probe and register (409 Conflict / UnknownHash), upload and retry once.
      if (regRes.status === 409) {
        await upload();
        regRes = await postRegister();
      }

      if (!regRes.ok) {
        const err = await responseError(regRes);
        throw new Error(`Failed to register blob: ${err}`);
      }

      const data = await regRes.json();
      if (!data || !data.ok || typeof data.url !== 'string') {
        throw new Error('Invalid response from blob registration');
      }

      blobUrls.set(logicalId, data.url);
      return data.url;
    },

    // Unregister a previously registered blob by logical ID.
    async unregisterBlob(logicalId) {
      if (typeof logicalId !== 'string' || !logicalId.trim()) {
        throw new TypeError('logicalId must be a non-empty string');
      }
      blobUrls.delete(logicalId);
      if (!ticket) return;

      const res = await fetch(`${httpBase}/blob/register/${encodeURIComponent(logicalId)}`, {
        method: 'DELETE',
        headers: { 'X-KnockBox-Ticket': ticket },
      });
      if (!res.ok) {
        const err = await responseError(res);
        throw new Error(`Failed to unregister blob: ${err}`);
      }
    },

    // Get the accessible URL for a registered blob, or null if not registered.
    blobUrl(logicalId) {
      return blobUrls.get(logicalId) || null;
    },
  };

  function snapshot() {
    return {
      playerId: KnockBox.playerId,
      players: KnockBox.players,
      isHost: KnockBox.isHost,
      authority: KnockBox.authority,
      ownerId: KnockBox.ownerId,
      isOwner: KnockBox.isOwner,
    };
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
      // `attached` means "Attach SENT", not "Attach accepted" — we flush optimistically. If the
      // ticket is rejected the server simply discards these frames and closes 1008, so flushing
      // before validation is harmless (and avoids gating every send on a round-trip).
      attached = true;
      flushPendingLogs();
    };
    ws.onmessage = (e) => {
      // A malformed frame must not throw uncaught out of the event handler (and the server is the
      // only sender, so this is belt-and-suspenders): log and drop it, keeping the socket alive.
      try { handle(JSON.parse(e.data)); }
      catch (err) { console.error('[KnockBox] discarding unparseable frame:', err); }
    };
    ws.onerror = () => { /* a failed connect surfaces as a close; reconnect is handled there */ };
    ws.onclose = (e) => {
      attached = false;
      if (isTerminalClose(e.code)) {
        // The ticket is invalid or our lobby membership ended — retrying is pointless.
        stopped = true;
        pendingLogs.length = 0; // give up — these logs will never send
        blobUrls.clear();
        console.warn('[KnockBox] data socket closed permanently:', e.reason || e.code);
        return;
      }
      scheduleReconnect();
    };
  }

  // Invoke each registered game callback in isolation: a throwing handler is the GAME's bug, and it
  // must not break sibling handlers or the SDK's own dispatch (roster updates happen before fire()).
  function fire(list, arg) {
    // Iterate a snapshot: a handler may register another during dispatch (onReady → onMessage),
    // and that newcomer must not be invoked in this same pass.
    for (const cb of [...list]) {
      try { cb(arg); }
      catch (err) { console.error('[KnockBox] error in game handler:', err); }
    }
  }

  function handle(msg) {
    switch (msg.type) {
      case 'Ready': {
        const info = normalizeReady(msg);
        KnockBox.playerId = info.playerId;
        KnockBox.players = info.players;
        KnockBox.isHost = info.isHost;
        KnockBox.authority = info.authority;
        KnockBox.ownerId = info.ownerId;
        KnockBox.isOwner = info.isOwner;
        ready = true;
        attempt = 0; // healthy connection — reset backoff
        fire(handlers.ready, snapshot());
        break;
      }
      case 'Game':
        fire(handlers.message, { from: msg.from, payload: msg.payload });
        break;
      case 'GamePlayerJoined':
        KnockBox.players = rosterAdd(KnockBox.players, msg.player);
        fire(handlers.playerJoined, msg.player);
        break;
      case 'GamePlayerLeft':
        KnockBox.players = rosterRemove(KnockBox.players, msg.playerId);
        fire(handlers.playerLeft, msg.playerId);
        break;
      // Transient presence: the peer is still a member (kept in `players` for the grace window),
      // so don't touch the roster — just signal the state change.
      case 'GamePlayerDisconnected':
        fire(handlers.playerDisconnected, msg.playerId);
        break;
      case 'GamePlayerConnected':
        fire(handlers.playerConnected, msg.playerId);
        break;
      case 'GameOwnerChanged':
        KnockBox.ownerId = msg.ownerId;
        KnockBox.isOwner = msg.ownerId === KnockBox.playerId;
        fire(handlers.ownerChanged, msg.ownerId);
        break;
    }
  }

  window.KnockBox = KnockBox;
  connect();
})();
