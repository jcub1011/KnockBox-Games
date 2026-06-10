// KnockBox Networking API — the game's "just send/receive over a websocket" client library.
//
// Loaded inside a game served from the GAME ORIGIN. It reads a one-time ticket + endpoint from its
// own URL (the shell put them there), opens its OWN websocket to the server, authenticates with the
// ticket, and exposes a tiny API. The game never sees a lobby id, the player's identity, or the
// shell — the server resolves all routing from the bound connection.
//
//   KnockBox.onReady(({ playerId, players, isHost }) => { ... })
//   KnockBox.onMessage(({ from, payload }) => { ... })
//   KnockBox.onPlayerJoined(player => ...) / onPlayerLeft(playerId => ...)
//   KnockBox.sendToHost(payload)        // -> the authoritative host (intent)
//   KnockBox.sendToAll(payload)         // -> everyone incl. self (state)
//   KnockBox.sendTo(playerId, payload)  // -> one specific player
//
// After onReady fires, KnockBox.playerId / players / isHost are populated.
//
// Engine note: this is the reference (vanilla-JS) client. A Godot addon (WebSocketPeer) or a Unity
// jslib package speaks the same JSON protocol: send {type:"Attach",ticket}; then exchange
// {type:"Game",to,payload} frames; read {type:"Ready",...} / {type:"GamePlayerJoined|Left",...}.
(function () {
  const params = new URLSearchParams(location.search);
  const ticket = params.get('kbTicket');
  // Endpoint the shell handed us; fall back to this origin's /ws.
  const endpoint = params.get('kbEndpoint') ||
    `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`;

  const handlers = { ready: [], message: [], playerJoined: [], playerLeft: [] };
  let ready = false;
  let ws = null;

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
  };

  function snapshot() {
    return { playerId: KnockBox.playerId, players: KnockBox.players, isHost: KnockBox.isHost };
  }

  function send(to, payload) {
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ type: 'Game', to, payload }));
  }

  function connect() {
    if (!ticket) { console.error('[KnockBox] missing kbTicket — cannot attach.'); return; }
    ws = new WebSocket(endpoint);

    ws.onopen = () => ws.send(JSON.stringify({ type: 'Attach', ticket }));
    ws.onmessage = (e) => handle(JSON.parse(e.data));
    // Session-scoped ticket: reconnect on drop and re-attach (the server re-validates membership).
    ws.onclose = () => setTimeout(connect, 1000);
  }

  function handle(msg) {
    switch (msg.type) {
      case 'Ready':
        KnockBox.playerId = msg.playerId;
        KnockBox.players = msg.players || [];
        KnockBox.isHost = !!msg.isHost;
        ready = true;
        handlers.ready.forEach((cb) => cb(snapshot()));
        break;
      case 'Game':
        handlers.message.forEach((cb) => cb({ from: msg.from, payload: msg.payload }));
        break;
      case 'GamePlayerJoined':
        if (!KnockBox.players.some((p) => p.id === msg.player.id)) KnockBox.players.push(msg.player);
        handlers.playerJoined.forEach((cb) => cb(msg.player));
        break;
      case 'GamePlayerLeft':
        KnockBox.players = KnockBox.players.filter((p) => p.id !== msg.playerId);
        handlers.playerLeft.forEach((cb) => cb(msg.playerId));
        break;
    }
  }

  window.KnockBox = KnockBox;
  connect();
})();
