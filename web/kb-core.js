// KnockBox client core — pure, DOM/WebSocket-free helpers shared by the SDK (knockbox.js) and the
// shell (shell.js). Kept side-effect-free so it can be unit-tested under Node/Vitest.
//
// The GAME-facing half now lives in kb-protocol.js and is RE-EXPORTED below, so every existing
// importer of kb-core.js keeps working unchanged. The split exists because this module is the
// SHELL's: favicons, colour math, the launch-overlay geometry, the play log and announcements all
// live here, and `/knockbox.js` used to drag all ~21 KB of it into every game to reach 9 symbols.
// Add a game-facing helper to kb-protocol.js; add a shell-only helper here.
export {
  PROTOCOL_VERSION,
  TERMINAL_CLOSE_CODE,
  isTerminalClose,
  reconnectDelay,
  parseLaunchParams,
  defaultEndpoint,
  LOG_LEVELS,
  makeLogger,
  normalizeReady,
  rosterAdd,
  rosterRemove,
} from './kb-protocol.js';

// Trailing-edge debounce: returns a wrapper that defers `fn` until `ms` have elapsed since the LAST
// call, collapsing a burst into one invocation. Used to keep high-frequency input (e.g. typing a
// name) from sending a control frame per keystroke and tripping the server rate limit. The returned
// function carries a .cancel() so callers that send immediately on their own (create/join) can drop
// a still-pending trailing send instead of letting it fire a redundant duplicate.
export function debounce(fn, ms) {
  let timer = null;
  const debounced = (...args) => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => { timer = null; fn(...args); }, ms);
  };
  debounced.cancel = () => { if (timer) { clearTimeout(timer); timer = null; } };
  return debounced;
}

// The shell picks one of these cat icons at random on each page load (ported from the legacy
// server's per-render favicon pick). Paths are relative to the shell origin root; the files live
// under web/favicons/ and are served by the shell origin's static middleware. The sketch variant
// is reserved exclusively for the admin portal.
export const FAVICONS = [
  '/favicons/cat-orange.png',
  '/favicons/cat-brown.png',
  '/favicons/cat-cream.png',
  '/favicons/cat-gray.png',
];

export const ADMIN_FAVICON = '/favicons/cat-sketch.png';

// Pure (testable) random pick. rand defaults to Math.random so tests can inject a deterministic stub.
export function pickRandomFavicon(favicons = FAVICONS, rand = Math.random) {
  if (!favicons || favicons.length === 0) return null;
  return favicons[Math.floor(rand() * favicons.length)];
}

// Reads the auto-join room code from a URL query string ("?join=ABCD") — the middle-click
// "open a test player in a new tab" entry point. Returns the trimmed, upper-cased code, or null
// when absent/blank. Pure, so it's unit-tested alongside the other protocol helpers.
export function parseJoinParam(search) {
  const code = new URLSearchParams(search || '').get('join');
  const trimmed = (code || '').trim().toUpperCase();
  return trimmed || null;
}

// A staged game's direct-launch link carries "?game=<id>". An operator marks a game "staged" to keep it
// off the public grid, so the tile that would normally start it isn't rendered — this link is the way
// in. It is VISIBILITY only, not access control: KnockBox has no player accounts, so there is nothing to
// authorize against and anyone holding the link can use it.
//
// The id is shape-checked before it goes anywhere near a request or an iframe URL. Game ids are folder
// names, so this is the same conservative alphabet the server accepts — a link can't smuggle a path
// separator or a scheme through it.
export function parseGameParam(search) {
  const id = (new URLSearchParams(search || '').get('game') || '').trim();
  return /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(id) ? id : null;
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

// ── Game launch overlay ─────────────────────────────────────────────────────────
// How long a launch may run before we admit it's slow (escalate the copy and offer a way out), and
// the hard ceiling after which the shell drops the overlay regardless — a missed iframe `load` must
// never leave a game that actually started hidden behind it.
export const LAUNCH_SLOW_MS = 8000;
export const LAUNCH_MAX_MS = 45000;

// How the launch ends. Nothing holds either exit back — making a game that has finished loading wait
// out an animation reads as clunky. Both MIRROR durations in home.css; change them together.
//
// MORPH: the good ending. The tile is replaced by the game in the very rect it occupied, which then
//   expands to fill the screen like a video going fullscreen. The overlay is gone from the first
//   frame of it, so nothing of the launch is ever drawn over a running game.
// EXIT:  the fallback fade, for an ending with no tile to hand over from (join-by-code before the
//   game is named, a rejoin) or no game to hand over to (an error, a deliberate bail-out).
export const LAUNCH_MORPH_MS = 300;
export const LAUNCH_EXIT_MS = 220;

// The morph's curve. Eased IN: the tile flight's ease-out suits a small object arriving somewhere, but
// on a full-screen expand it spends most of its travel in the first few frames, which lands as a jolt.
// This is at 4% / 10% / 36% by 40 / 60 / 100ms — a gentle start that still decelerates into the finish
// rather than slamming against the viewport edge at peak speed. (A stronger ease-in overshoots the
// other way: so little early movement that it reads as a hitch.)
export const LAUNCH_MORPH_EASING = 'cubic-bezier(0.45, 0, 0.25, 1)';

// "Starting Tic Tac Toe…". The join-by-code path doesn't learn which game it is until EnterGame
// arrives, so fall back to a generic label rather than rendering "Starting …" with a hole in it.
export function launchMessage(gameName) {
  const name = typeof gameName === 'string' ? gameName.trim() : '';
  return name ? `Starting ${name}…` : 'Starting game…';
}

// FLIP: the transform that puts `dest` (the centred launch tile, at its final size) back on top of
// `src` (the clicked grid tile). Applying it, then removing it, animates the one into the other.
// Rects are {left, top, width, height} — DOMRects, or plain objects in tests.
//
// Returns null when either rect is degenerate: a tile scrolled out of view, a display:none ancestor,
// or jsdom, where getBoundingClientRect is all zeros. That's a normal outcome, not an error — the
// caller falls back to a centre pop-in, which is also the join-by-code and rejoin path.
export function launchFlipFrom(src, dest) {
  if (!src || !dest || !src.width || !src.height || !dest.width) return null;
  return {
    dx: (src.left + src.width / 2) - (dest.left + dest.width / 2),
    dy: (src.top + src.height / 2) - (dest.top + dest.height / 2),
    scale: src.width / dest.width,
  };
}

// Degrees of rotation in a CSSOM transform value ("none", "matrix(a, b, c, d, e, f)", or the 3d
// form). The grid tiles each carry a ±1deg nth-child rotation; the flying tile starts at its source
// tile's angle and settles to square, so picking a game up off the table straightens it. Anything
// unparseable yields 0 — a missing flourish, never a broken transform.
export function rotationFromMatrix(transform) {
  const m = (transform || '').match(/matrix(3d)?\(([^)]+)\)/);
  if (!m) return 0;
  const n = m[2].split(',').map((v) => parseFloat(v));
  // matrix(a,b,…) and matrix3d(a,b,…) both start with the first column of the 2D sub-matrix.
  if (!Number.isFinite(n[0]) || !Number.isFinite(n[1])) return 0;
  return Math.atan2(n[1], n[0]) * 180 / Math.PI;
}

// 1D spring-damper step using semi-implicit Euler integration for smooth, stable physics simulation.
// Used for dragging follow-through and rubber-band return to center.
export function stepSpring1D(pos, target, vel, { stiffness = 220, damping = 20, mass = 1 } = {}, dt = 1 / 60) {
  const force = -stiffness * (pos - target) - damping * vel;
  const acc = force / (mass || 1);
  const nextVel = vel + acc * dt;
  const nextPos = pos + nextVel * dt;
  return { pos: nextPos, vel: nextVel };
}

// Dynamic tilt (in degrees) when dragging the hero tile: moving it sideways tilts the card with
// velocity and offset, giving it a playful, physical feel.
export function calculateDragTilt(velX = 0, posX = 0, maxTilt = 15) {
  const tilt = velX * 0.035 + posX * 0.015;
  return Math.max(-maxTilt, Math.min(maxTilt, tilt));
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

// ── Platform announcements (operator banner, §4.1) ────────────────────────────

// The severities the banner knows how to draw. Anything else is treated as 'info' rather than being
// used: the value ends up in a CSS class name, and a server (or a hand-edited settings file) is not a
// reason to stop validating what goes into the DOM.
export const ANNOUNCEMENT_SEVERITIES = ['info', 'warning'];

export function announcementSeverity(value) {
  return ANNOUNCEMENT_SEVERITIES.includes(String(value ?? '')) ? String(value) : 'info';
}

// Whether the banner should be shown, given the announcement and the id the player last dismissed.
// Dismissal is per-announcement, not per-session: an operator who edits a notice gets a NEW id, so
// everyone sees the new wording — which is the whole reason the id exists rather than a boolean.
export function shouldShowAnnouncement(announcement, dismissedId) {
  if (!announcement || !String(announcement.text ?? '').trim()) return false;
  return String(announcement.id ?? '') !== String(dismissedId ?? '');
}

// The text to render. A game-scoped announcement is prefixed with that game's title, because on the
// home page the notice is otherwise indistinguishable from a platform-wide one — "retiring on the
// 15th" needs to say what is retiring. `gameName` is looked up by the caller; an unknown id falls back
// to no prefix rather than showing a raw id to a player who has never seen one.
export function announcementText(announcement, gameName) {
  const text = String(announcement?.text ?? '').trim();
  const name = String(gameName ?? '').trim();
  return name ? `${name}: ${text}` : text;
}

// ── Games list search, player filter & sorting ───────────────────────────────

// Checks if a game matches a text search query across its name, id, tags, and description.
export function matchesGameSearch(game, query) {
  if (!query || typeof query !== 'string') return true;
  const q = query.trim().toLowerCase();
  if (!q) return true;

  if (game?.name && game.name.toLowerCase().includes(q)) return true;
  if (game?.id && game.id.toLowerCase().includes(q)) return true;
  if (game?.description && game.description.toLowerCase().includes(q)) return true;
  if (normalizeTags(game?.tags).some((t) => t.toLowerCase().includes(q))) return true;
  return false;
}

// Checks if a game matches the selected player count filter.
// Handles exact player counts ("1", "2", "3", ...) and "9+" ranges.
export function matchesPlayerCount(game, filterValue) {
  if (!filterValue || typeof filterValue !== 'string') return true;
  const val = filterValue.trim();
  if (!val) return true;

  const min = Number.isFinite(game?.minPlayers) ? game.minPlayers : 1;
  const max = Number.isFinite(game?.maxPlayers) ? game.maxPlayers : min;

  if (val.endsWith('+')) {
    const threshold = parseInt(val, 10);
    if (!Number.isFinite(threshold)) return true;
    return max >= threshold;
  }

  const count = parseInt(val, 10);
  if (!Number.isFinite(count)) return true;
  return min <= count && max >= count;
}

// Parses an ISO timestamp into millisecond epoch for date sorting, or 0 if absent/invalid.
function parseDateEpoch(val) {
  if (!val) return 0;
  const d = new Date(val);
  const time = d.getTime();
  return Number.isFinite(time) ? time : 0;
}

// Sorts an array of games according to the sort option:
// - 'alphabetical': name A-Z (case-insensitive)
// - 'newest': createdAt descending (newest first), tie-breaking by name
// - 'updated': updatedAt descending (most recently updated first), tie-breaking by name
export function sortGames(gamesList, sortOption = 'newest') {
  const list = Array.isArray(gamesList) ? [...gamesList] : [];
  const opt = String(sortOption || '').toLowerCase();

  return list.sort((a, b) => {
    const nameA = String(a?.name || a?.id || '');
    const nameB = String(b?.name || b?.id || '');

    if (opt === 'alphabetical') {
      return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
    }

    if (opt === 'updated') {
      const timeA = parseDateEpoch(a?.updatedAt) || parseDateEpoch(a?.createdAt);
      const timeB = parseDateEpoch(b?.updatedAt) || parseDateEpoch(b?.createdAt);
      if (timeA !== timeB) return timeB - timeA;
      return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
    }

    // Default: 'newest'
    const timeA = parseDateEpoch(a?.createdAt) || parseDateEpoch(a?.updatedAt);
    const timeB = parseDateEpoch(b?.createdAt) || parseDateEpoch(b?.updatedAt);
    if (timeA !== timeB) return timeB - timeA;
    return nameA.localeCompare(nameB, undefined, { sensitivity: 'base' });
  });
}

// Formats a game's player capacity for the chin bar (e.g. "1–8", "2", "1").
export function formatPlayerCapacity(minPlayers, maxPlayers) {
  const min = Number.isFinite(minPlayers) && minPlayers > 0 ? minPlayers : 1;
  const max = Number.isFinite(maxPlayers) && maxPlayers > 0 ? maxPlayers : min;

  if (min === max) {
    return `${max}`;
  }
  return `${min}–${max}`;
}

// The one rule for what counts as a renderable tag. Nothing validates `tags` server-side, so a
// GAME.json can declare `["", null, 3]` — and every consumer (the chips, the tooltip, the search)
// must agree about it, or the grid shows zero-width chips for entries the search can never match.
export function normalizeTags(tags) {
  if (!Array.isArray(tags)) return [];
  return tags.filter(Boolean).map((t) => String(t).trim()).filter(Boolean);
}

// Formats the full list of tags for the hover tooltip.
export function formatTagsTooltip(tags) {
  return normalizeTags(tags).join(', ');
}

// Unified filtering and sorting pipeline for the games catalog.
export function filterAndSortGames(gamesList, { search = '', playerCount = '', sort = 'newest' } = {}) {
  const base = Array.isArray(gamesList) ? gamesList : [];
  const filtered = base.filter((g) => matchesGameSearch(g, search) && matchesPlayerCount(g, playerCount));
  return sortGames(filtered, sort);
}

