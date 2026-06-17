// KnockBox client core — pure, DOM/WebSocket-free helpers shared by the SDK (knockbox.js) and the
// shell (shell.js). Kept side-effect-free so it can be unit-tested under Node/Vitest.

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

// The shell picks one of these cat icons at random on each page load (ported from the legacy
// server's per-render favicon pick). Paths are relative to the shell origin root; the files live
// under web/favicons/ and are served by the shell origin's static middleware.
export const FAVICONS = [
  '/favicons/cat-orange.png',
  '/favicons/cat-brown.png',
  '/favicons/cat-cream.png',
  '/favicons/cat-gray.png',
  '/favicons/cat-sketch.png',
];

// Pure (testable) random pick. rand defaults to Math.random so tests can inject a deterministic stub.
export function pickRandomFavicon(favicons = FAVICONS, rand = Math.random) {
  if (!favicons || favicons.length === 0) return null;
  return favicons[Math.floor(rand() * favicons.length)];
}

// The shell hands the game its credentials in the URL FRAGMENT (not the query string) so they are
// never sent in a Referer header or written to server/proxy logs. Parses "#kbTicket=…&kbEndpoint=…".
export function parseLaunchParams(hash) {
  const raw = (hash || '').replace(/^#/, '');
  const params = new URLSearchParams(raw);
  return { ticket: params.get('kbTicket'), endpoint: params.get('kbEndpoint') };
}

// Reads the auto-join room code from a URL query string ("?join=ABCD") — the middle-click
// "open a test player in a new tab" entry point. Returns the trimmed, upper-cased code, or null
// when absent/blank. Pure, so it's unit-tested alongside the other protocol helpers.
export function parseJoinParam(search) {
  const code = new URLSearchParams(search || '').get('join');
  const trimmed = (code || '').trim().toUpperCase();
  return trimmed || null;
}

// Default data-socket endpoint when the shell didn't supply one: this origin's /ws.
export function defaultEndpoint(protocol, host) {
  return `${protocol === 'https:' ? 'wss' : 'ws'}://${host}/ws`;
}

// The ws(s):// endpoint for a game origin's /ws (http→ws, https→wss).
export function gameWsEndpoint(gameOrigin) {
  return gameOrigin.replace(/^http/, 'ws') + '/ws';
}

// Validates a game-origin string (server-supplied, via the Welcome message) and returns its
// normalized http(s) origin, or null if it isn't a valid web origin. The explicit scheme allowlist
// is the guard that stops a hostile/unexpected value (e.g. "javascript:…") from ever reaching an
// iframe navigation as XSS or an open redirect.
export function sanitizeGameOrigin(value) {
  try {
    const u = new URL(value);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return null;
    return u.origin;
  } catch {
    return null;
  }
}

// Builds the iframe src for an embedded game, with credentials in the fragment (see parseLaunchParams).
export function buildGameSrc(gameOrigin, gameId, entry, ticket, wsEndpoint) {
  // The origin is server-supplied; reject anything that isn't a real http(s) origin so the iframe
  // src can never become a javascript:/data: navigation or point off to an arbitrary host.
  const safeOrigin = sanitizeGameOrigin(gameOrigin);
  if (!safeOrigin) throw new Error('Invalid game origin');
  // Encode path segments: gameId/entry arrive in a server message, so they must not be able to
  // inject a scheme, path traversal, or extra path into the iframe's navigation URL. (entry may
  // legitimately contain '/', so encode each segment rather than the whole string.)
  const safeGameId = encodeURIComponent(gameId);
  const safeEntry = entry.split('/').map(encodeURIComponent).join('/');
  const base = `${safeOrigin}/games/${safeGameId}/${safeEntry}`;
  const frag = `kbTicket=${encodeURIComponent(ticket)}&kbEndpoint=${encodeURIComponent(wsEndpoint)}`;
  return `${base}#${frag}`;
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

// ── Header-theming helpers (pure color math) ──────────────────────────────────
// Shared with shell.js, which derives the in-game header tint from a game's manifest color or
// thumbnail. Kept here (DOM-free) so the math is unit-tested under Node; shell.js owns the CSSOM
// probe and canvas plumbing that feed these.

// WCAG relative luminance (0=black … 1=white) of an {r,g,b} (0–255) color — used to choose
// contrasting header text.
export function luminance({ r, g, b }) {
  const lin = (c) => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

// Pick a contrasting text color for a background: near-black on light backgrounds, white on dark.
export function pickContrastText(bg) {
  return luminance(bg) > 0.5 ? { r: 26, g: 26, b: 26 } : { r: 255, g: 255, b: 255 };
}

// Pick a representative color from raw RGBA pixel data (a canvas getImageData `data` array) by
// bucketing pixels and weighting by saturation so a game's vibrant accent wins over flat
// backgrounds. Skips transparent and the near-white/near-black extremes that are usually padding.
// Returns {r,g,b} or null when nothing usable remains. shell.js draws the thumbnail small and hands
// the pixels here, keeping the loop pure and testable.
export function dominantColorFromPixels(data) {
  const buckets = new Map();
  let best = null;
  for (let i = 0; i < data.length; i += 4) {
    if (data[i + 3] < 200) continue; // transparent
    const r = data[i], g = data[i + 1], b = data[i + 2];
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    if (max > 240 && min > 240) continue; // near-white
    if (max < 18) continue;               // near-black
    const sat = max === 0 ? 0 : (max - min) / max;
    const weight = 1 + sat * 3;
    const key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3); // 5 bits/channel
    let e = buckets.get(key);
    if (!e) { e = { r: 0, g: 0, b: 0, w: 0 }; buckets.set(key, e); }
    e.r += r * weight; e.g += g * weight; e.b += b * weight; e.w += weight;
    if (!best || e.w > best.w) best = e;
  }
  return best ? { r: Math.round(best.r / best.w), g: Math.round(best.g / best.w), b: Math.round(best.b / best.w) } : null;
}

// Parse a CSSOM-normalized color string (always "rgb(...)" / "rgba(...)") into {r,g,b}, or null.
// Non-opaque values (alpha < 1, e.g. `transparent` → rgba(0,0,0,0)) are rejected so theming falls
// back instead of painting a wrong (black/translucent) tint. shell.js feeds this getComputedStyle's
// output after validating the author value through a CSSOM probe. Alpha may arrive as a 0–1 number
// (`rgba(…, 0.5)`) or, in the modern space-separated form, as a percentage (`rgb(… / 50%)`) — both
// are normalized before the opaque check.
export function parseRgbComponents(normalized) {
  const m = (normalized || '').match(/-?\d*\.?\d+%?/g);
  if (!m || m.length < 3) return null;
  if (m.length >= 4) {
    const a = m[3].endsWith('%') ? parseFloat(m[3]) / 100 : parseFloat(m[3]);
    if (a < 1) return null; // not fully opaque — treat as unset
  }
  return { r: +m[0], g: +m[1], b: +m[2] };
}

// ── Shareable lobby link ───────────────────────────────────────────────────────
// Auto-join URL for a lobby ("<origin>/?join=CODE"): opening it lands a player straight in the
// lobby (see shell.js's "?join=" handling). Carries only the public room code — no identity token.
export function buildJoinLink(origin, code) {
  return `${origin}/?join=${encodeURIComponent(code)}`;
}

// Roster reducers (immutable): add is idempotent by id; remove drops by id.
export function rosterAdd(players, player) {
  return players.some((p) => p.id === player.id) ? players : [...players, player];
}

export function rosterRemove(players, playerId) {
  return players.filter((p) => p.id !== playerId);
}

// ── Play Log ────────────────────────────────────────────────────────────────────
// Games push play-log entries via KnockBox.logPlay(metadata); the server stamps gameId/timestamp/
// isHost and forwards them to the shell, which persists the most-recent few in the browser and
// renders them on the home page. These helpers are the pure (storage/DOM-free) part of that.

// Cap on how many play-log entries the shell keeps in the browser (most-recent-first).
export const PLAY_LOG_MAX = 50;

// Prepend `entry` to the play-log list and clamp to `max` (newest first). Immutable, like the
// roster reducers. A non-array `list` (e.g. corrupt storage) is treated as empty.
export function appendPlayLog(list, entry, max = PLAY_LOG_MAX) {
  const base = Array.isArray(list) ? list : [];
  return [entry, ...base].slice(0, Math.max(0, max));
}

// Recognized "standard library" metadata keys a game can put in its logPlay() bag. The shell shows
// these in dedicated chips (in this display order); every other key falls through to the details
// table. Grow this list as new well-known fields are introduced. (gameId/timestamp/isHost are NOT
// here — those are stamped by the server as top-level fields, not metadata.)
export const PLAY_LOG_STANDARD_KEYS = ['placement', 'playerCount', 'score', 'result'];

// Split a metadata bag into the recognized standard keys (in PLAY_LOG_STANDARD_KEYS order) and the
// leftover arbitrary pairs (in insertion order). Both are arrays of [key, value]. A missing/non-object
// bag yields empty arrays.
export function partitionPlayLogMetadata(metadata) {
  const bag = metadata && typeof metadata === 'object' ? metadata : {};
  const standard = [];
  for (const key of PLAY_LOG_STANDARD_KEYS) {
    if (Object.prototype.hasOwnProperty.call(bag, key)) standard.push([key, bag[key]]);
  }
  const extra = Object.keys(bag)
    .filter((k) => !PLAY_LOG_STANDARD_KEYS.includes(k))
    .map((k) => [k, bag[k]]);
  return { standard, extra };
}

// Format a placement number (or numeric string) as an English ordinal: 1→"1st", 2→"2nd", 3→"3rd",
// 4→"4th", 11/12/13→"11th"/"12th"/"13th". Non-numeric input is returned unchanged (String()).
export function ordinal(n) {
  const num = Number(n);
  if (!Number.isFinite(num) || !Number.isInteger(num)) return String(n);
  const abs = Math.abs(num) % 100;
  const last = abs % 10;
  const suffix = abs >= 11 && abs <= 13 ? 'th' : last === 1 ? 'st' : last === 2 ? 'nd' : last === 3 ? 'rd' : 'th';
  return `${num}${suffix}`;
}
