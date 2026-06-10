// KnockBox client core — pure, DOM/WebSocket-free helpers shared by the SDK (knockbox.js) and the
// shell (shell.js). Kept side-effect-free so it can be unit-tested under Node/Vitest.

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

// The ws(s):// endpoint for a game origin's /ws (http→ws, https→wss).
export function gameWsEndpoint(gameOrigin) {
  return gameOrigin.replace(/^http/, 'ws') + '/ws';
}

// Builds the iframe src for an embedded game, with credentials in the fragment (see parseLaunchParams).
export function buildGameSrc(gameOrigin, gameId, entry, ticket, wsEndpoint) {
  const base = `${gameOrigin}/games/${gameId}/${entry}`;
  const frag = `kbTicket=${encodeURIComponent(ticket)}&kbEndpoint=${encodeURIComponent(wsEndpoint)}`;
  return `${base}#${frag}`;
}

// Roster reducers (immutable): add is idempotent by id; remove drops by id.
export function rosterAdd(players, player) {
  return players.some((p) => p.id === player.id) ? players : [...players, player];
}

export function rosterRemove(players, playerId) {
  return players.filter((p) => p.id !== playerId);
}
