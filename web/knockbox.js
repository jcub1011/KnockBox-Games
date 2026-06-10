// KnockBox Networking API — loaded *inside* a game's iframe.
//
// A game never touches the WebSocket, identity, or lobby. It talks to the platform shell
// (the parent window) over postMessage; the shell wraps these calls in WebSocket relay
// envelopes. Same-origin, so messages are addressed to location.origin.
//
//   KnockBox.onReady(({ playerId, players, isHost, lobbyId }) => { ... })
//   KnockBox.onMessage(({ from, payload }) => { ... })   // a relayed game message arrived
//   KnockBox.onPlayerJoined(player => ...) / onPlayerLeft(playerId => ...)
//   KnockBox.sendToHost(payload)   // guest -> authoritative host (intent)
//   KnockBox.sendToAll(payload)    // host  -> everyone (state)
//   KnockBox.sendTo(playerId, payload)
//
// After onReady fires, KnockBox.playerId / players / isHost / lobbyId are populated.
(function () {
  const handlers = { ready: [], message: [], playerJoined: [], playerLeft: [] };
  let ready = false;

  const KnockBox = {
    playerId: null,
    players: [],
    isHost: false,
    lobbyId: null,

    onReady(cb) {
      handlers.ready.push(cb);
      if (ready) cb(snapshot()); // late subscriber still gets the init
    },
    onMessage(cb) { handlers.message.push(cb); },
    onPlayerJoined(cb) { handlers.playerJoined.push(cb); },
    onPlayerLeft(cb) { handlers.playerLeft.push(cb); },

    sendToHost(payload) { relay('host', payload); },
    sendToAll(payload) { relay('all', payload); },
    sendTo(playerId, payload) { relay(playerId, payload); },
  };

  function snapshot() {
    return {
      playerId: KnockBox.playerId,
      players: KnockBox.players,
      isHost: KnockBox.isHost,
      lobbyId: KnockBox.lobbyId,
    };
  }

  function relay(to, payload) {
    parent.postMessage({ kb: true, kind: 'relay', to, payload }, location.origin);
  }

  window.addEventListener('message', (e) => {
    if (e.origin !== location.origin) return;
    const msg = e.data;
    if (!msg || msg.kb !== true) return;

    switch (msg.kind) {
      case 'init':
        KnockBox.playerId = msg.playerId;
        KnockBox.players = msg.players || [];
        KnockBox.isHost = !!msg.isHost;
        KnockBox.lobbyId = msg.lobbyId;
        ready = true;
        handlers.ready.forEach((cb) => cb(snapshot()));
        break;
      case 'relay':
        handlers.message.forEach((cb) => cb({ from: msg.from, payload: msg.payload }));
        break;
      case 'playerJoined':
        KnockBox.players = msg.players || KnockBox.players;
        handlers.playerJoined.forEach((cb) => cb(msg.player));
        break;
      case 'playerLeft':
        KnockBox.players = msg.players || KnockBox.players;
        handlers.playerLeft.forEach((cb) => cb(msg.playerId));
        break;
    }
  });

  // Tell the shell we're loaded and ready to receive init.
  parent.postMessage({ kb: true, kind: 'gameLoaded' }, location.origin);

  window.KnockBox = KnockBox;
})();
