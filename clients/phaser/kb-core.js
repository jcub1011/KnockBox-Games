// KnockBox client core — pure, DOM/WebSocket-free helpers shared by the Phaser plugin
// (knockbox-plugin.js) and the authority helper (kb-authority.js). Kept side-effect-free so it can
// be unit-tested under Node, and so the exact same protocol behavior (backoff, terminal-close
// handling, roster reducers, fragment parsing) stays identical to the vanilla-JS and Godot clients.
//
// This is a direct port of KnockBox-Games/web/kb-core.js. Shipped as a UMD module so it works as a
// browser global (window.KnockBoxCore), under CommonJS (require), or AMD — no build step required.
(function (root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else if (typeof define === 'function' && define.amd) {
    define([], factory);
  } else {
    root.KnockBoxCore = factory();
  }
})(typeof globalThis !== 'undefined' ? globalThis : (typeof self !== 'undefined' ? self : this), function () {
  'use strict';

  // Wire-protocol version this client speaks, declared in the first frame (Attach). The server
  // accepts anything up to its own version and terminally rejects anything newer, so a copied-out
  // client that outpaces an old server fails loudly instead of being silently misrouted.
  // Mirrors KnockBoxProtocol.Version in KnockBox.Contracts.
  var PROTOCOL_VERSION = 1;

  // Server close code used for terminal rejections (WebSocketCloseStatus.PolicyViolation): an
  // invalid ticket or expired lobby membership. There is no point reconnecting — the credential
  // won't work.
  var TERMINAL_CLOSE_CODE = 1008;

  function isTerminalClose(code) {
    return code === TERMINAL_CLOSE_CODE;
  }

  // Capped exponential backoff for transient drops. attempt is 0-based: 1s, 2s, 4s, … up to `max`.
  function reconnectDelay(attempt, base, max) {
    base = base || 1000;
    max = max || 30000;
    var n = Math.max(0, attempt | 0);
    return Math.min(max, base * Math.pow(2, n));
  }

  // The shell hands the game its credentials in the URL FRAGMENT (not the query string) so they are
  // never sent in a Referer header or written to server/proxy logs. Parses "#kbTicket=…&kbEndpoint=…".
  function parseLaunchParams(hash) {
    var raw = (hash || '').replace(/^#/, '');
    var params = new URLSearchParams(raw);
    return { ticket: params.get('kbTicket'), endpoint: params.get('kbEndpoint') };
  }

  // Default data-socket endpoint when the shell didn't supply one: this origin's /ws.
  function defaultEndpoint(protocol, host) {
    return (protocol === 'https:' ? 'wss' : 'ws') + '://' + host + '/ws';
  }

  // Game → server logging. Maps the friendly, console-like method names the client exposes to the
  // Microsoft.Extensions.Logging.LogLevel NAMES the server's LogMessage expects on the wire (the
  // server parses them case-insensitively). info→Information and warn→Warning match console habits.
  var LOG_LEVELS = {
    trace: 'Trace',
    debug: 'Debug',
    info: 'Information',
    warn: 'Warning',
    error: 'Error',
    critical: 'Critical',
  };

  // Builds a console-like logger object ({ trace, debug, info, warn, error, critical }) whose methods
  // each hand a { type:'Log', level, message } frame to the supplied transport. `sendFrame` is the
  // only client-specific bit, so this stays pure and every client emits identical frames.
  function makeLogger(sendFrame) {
    var api = {};
    Object.keys(LOG_LEVELS).forEach(function (method) {
      var level = LOG_LEVELS[method];
      api[method] = function (message) {
        sendFrame({ type: 'Log', level: level, message: String(message) });
      };
    });
    return api;
  }

  // Roster reducers (immutable): add is idempotent by id; remove drops by id.
  function rosterAdd(players, player) {
    return players.some(function (p) { return p.id === player.id; })
      ? players
      : players.concat([player]);
  }

  function rosterRemove(players, playerId) {
    return players.filter(function (p) { return p.id !== playerId; });
  }

  return {
    PROTOCOL_VERSION: PROTOCOL_VERSION,
    TERMINAL_CLOSE_CODE: TERMINAL_CLOSE_CODE,
    isTerminalClose: isTerminalClose,
    reconnectDelay: reconnectDelay,
    parseLaunchParams: parseLaunchParams,
    defaultEndpoint: defaultEndpoint,
    LOG_LEVELS: LOG_LEVELS,
    makeLogger: makeLogger,
    rosterAdd: rosterAdd,
    rosterRemove: rosterRemove,
  };
});
