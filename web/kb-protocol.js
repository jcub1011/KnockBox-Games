// KnockBox game protocol core — the game-facing half of the client SDK.
//
// Pure, DOM/WebSocket-free helpers that a GAME needs: the wire version, reconnect policy, the
// launch fragment, the log-level mapping and the Ready/roster reducers. Nothing here knows about
// the shell's UI, so this file is what ships to a game — `web/knockbox.js` imports it directly,
// and it is the `web` addon's second file (see clients/addons.manifest.json).
//
// `web/kb-core.js` re-exports everything below, so the shell (shell.js, admin/) keeps importing
// from kb-core.js unchanged. Keep it that way: kb-core.js is the shell's module, this is the
// game's, and the shell-only helpers (favicons, launch-overlay math, play log, announcements,
// color math) must NOT move here — shipping them to every game is what this split undid.
//
// The export list below is the contract the other two client ports mirror:
// `clients/phaser/kb-core.js` (UMD) and `clients/godot/addons/knockbox/kb_core.gd` (GDScript).
// A parity test compares the three; adding a game-facing helper means adding it in all three.

// Wire-protocol version this SDK speaks, declared in the first frame of each role (Hello/Attach).
// The server accepts anything up to its own version and terminally rejects anything newer, so a
// copied-out SDK that outpaces an old server fails loudly instead of being silently misrouted.
// Mirrors KnockBoxProtocol.Version in KnockBox.Contracts.
export const PROTOCOL_VERSION = 1;

// Server close code used for terminal rejections (WebSocketCloseStatus.PolicyViolation): an invalid
// ticket or expired lobby membership. There is no point reconnecting — the credential won't work.
export const TERMINAL_CLOSE_CODE = 1008;

export function isTerminalClose(code) {
  return code === TERMINAL_CLOSE_CODE;
}

// Capped exponential backoff for transient drops. attempt is 0-based: 1s, 2s, 4s, … up to `max`.
export function reconnectDelay(attempt, base = 1000, max = 30000) {
  const n = Math.max(0, attempt | 0);
  return Math.min(max, base * 2 ** n);
}

// The shell hands the game its credentials in the URL FRAGMENT (not the query string) so they are
// never sent in a Referer header or written to server/proxy logs. Parses "#kbTicket=…&kbEndpoint=…".
export function parseLaunchParams(hash) {
  const raw = (hash || '').replace(/^#/, '');
  const params = new URLSearchParams(raw);
  return { ticket: params.get('kbTicket'), endpoint: params.get('kbEndpoint') };
}

// Default data-socket endpoint when the shell didn't supply one: this origin's /ws.
export function defaultEndpoint(protocol, host) {
  return `${protocol === 'https:' ? 'wss' : 'ws'}://${host}/ws`;
}

// Game → server logging. Maps the friendly, console-like method names the SDK exposes to the
// Microsoft.Extensions.Logging.LogLevel NAMES the server's LogMessage expects on the wire (the
// server parses them case-insensitively). info→Information and warn→Warning match console habits.
export const LOG_LEVELS = {
  trace: 'Trace',
  debug: 'Debug',
  info: 'Information',
  warn: 'Warning',
  error: 'Error',
  critical: 'Critical',
};

// Builds a console-like logger object ({ trace, debug, info, warn, error, critical }) whose methods
// each hand a { type:'Log', level, message } frame to the supplied transport. `sendFrame` is the
// only client-specific bit, so this stays pure and the web and Phaser SDKs emit identical frames.
export function makeLogger(sendFrame) {
  const api = {};
  for (const method in LOG_LEVELS) {
    const level = LOG_LEVELS[method];
    api[method] = (message) => sendFrame({ type: 'Log', level, message: String(message) });
  }
  return api;
}

// Normalize a Ready frame into the SDK's identity/authority fields, with old-server fallbacks.
// `authority` says who runs the game's authoritative logic: 'host' (a member's browser — the
// default) or 'server' (the game's authority module runs server-side; every client is a guest).
// `ownerId` is the member holding the lobby powers (kick, open/close) — a separate concept from
// the authority; gate owner UI on `isOwner`, never `isHost`. A pre-authority server omits both
// fields: authority defaults to 'host', and the owner is derivable only when we ARE the host.
export function normalizeReady(msg) {
  const playerId = msg.playerId;
  const isHost = !!msg.isHost;
  const authority = msg.authority ?? 'host';
  const ownerId = msg.ownerId ?? (isHost ? playerId : null);
  return {
    playerId,
    players: msg.players || [],
    isHost,
    authority,
    ownerId,
    isOwner: ownerId != null && ownerId === playerId,
  };
}

// Roster reducers (immutable): add is idempotent by id; remove drops by id.
export function rosterAdd(players, player) {
  return players.some((p) => p.id === player.id) ? players : [...players, player];
}

export function rosterRemove(players, playerId) {
  return players.filter((p) => p.id !== playerId);
}
