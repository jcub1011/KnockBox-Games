// Pure helpers for the admin portal: formatting, filtering, and the rate arithmetic the dashboard does
// on top of the server's cumulative counters. Same split as web/kb-core.js next to web/shell.js — nothing
// here touches the DOM, fetch or timers, so it is unit-tested in the plain Node environment while
// admin.js (which does all three) is tested under jsdom.

// ── Tabs ──────────────────────────────────────────────────────────────────────

/** The dashboard tabs, in sidebar order. The nav's data-tab attributes must match these. */
export const TABS = ['overview', 'lobbies', 'games', 'logs'];

/**
 * The tab a URL fragment selects, or the first tab when the fragment names nothing valid. Driving the
 * tab from location.hash means a reload, a bookmark and the back button all land where the operator was,
 * which for a page they keep open on a second monitor is the difference between useful and annoying.
 */
export function tabFromHash(hash, tabs = TABS) {
  const name = String(hash || '').replace(/^#/, '').trim().toLowerCase();
  return tabs.includes(name) ? name : tabs[0];
}

// ── Formatting ────────────────────────────────────────────────────────────────

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];

/**
 * A finite number, or null when the value is absent or nonsense.
 *
 * The explicit null/undefined/blank check matters: `Number(null)` and `Number('')` are both 0, so without
 * it a field the server didn't report would render as a confident "0 B" or "0s" — claiming we measured
 * zero when in fact we measured nothing.
 */
function toNumber(value) {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

/** Bytes as a short human string. Deliberately 1024-based, matching what `du` and Docker report. */
export function formatBytes(bytes) {
  const n = toNumber(bytes);
  if (n === null || n < 0) return '--';
  if (n === 0) return '0 B';
  let value = n;
  let unit = 0;
  while (value >= 1024 && unit < UNITS.length - 1) { value /= 1024; unit++; }
  // Whole numbers for bytes, one decimal above that — "1.4 MB" reads better than "1.43 MB" in a table,
  // and "1433 B" better than "1.4 KB" when the point is that it's tiny.
  return unit === 0 ? `${Math.round(value)} B` : `${value.toFixed(value < 10 ? 1 : 0)} ${UNITS[unit]}`;
}

/**
 * A duration in seconds as the two largest useful units ("3d 4h", "5m 12s"). Two units, not all four:
 * the point of this column is scanning for the outlier, and "3d 4h 17m 9s" makes every row the same
 * width and none of them readable.
 */
export function formatDuration(seconds) {
  const value = toNumber(seconds);
  if (value === null || value < 0) return '--';
  const total = Math.floor(value);
  if (total === 0) return '0s';
  const d = Math.floor(total / 86400);
  const h = Math.floor((total % 86400) / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (d > 0) return `${d}d ${h}h`;
  if (h > 0) return `${h}h ${m}m`;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

/** A count with thousands separators, for the frame/byte counters. */
export function formatCount(value) {
  const n = toNumber(value);
  return n === null ? '--' : n.toLocaleString('en-US');
}

/** An ISO timestamp as a local clock time, or '--' when it isn't parseable. */
export function formatClock(iso) {
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? '--' : at.toLocaleTimeString();
}

// ── Rates from cumulative counters ────────────────────────────────────────────

/**
 * A per-second rate between two samples of a monotonic counter, or null when it can't be computed.
 *
 * The server reports totals rather than rates on purpose: a rate needs two samples, and producing one
 * server-side would mean either sleeping inside a request or keeping per-viewer state. The portal polls
 * anyway, so the subtraction belongs here.
 *
 * Returns null (rather than 0) when there is no previous sample, when the clock didn't advance, or when
 * the counter went BACKWARDS — which means the server restarted, and treating that as a negative rate
 * would draw a nonsense spike at exactly the moment an operator is trying to understand a restart.
 */
export function ratePerSecond(previous, current) {
  if (!previous || !current) return null;
  const dt = (new Date(current.at).getTime() - new Date(previous.at).getTime()) / 1000;
  if (!Number.isFinite(dt) || dt <= 0) return null;
  const dv = Number(current.value) - Number(previous.value);
  if (!Number.isFinite(dv) || dv < 0) return null;
  return dv / dt;
}

/**
 * Instantaneous process CPU as a percentage of one core-equivalent, from two system-status samples.
 * `cpuPercentLifetime` in the payload is an average since boot, which stops moving once the process has
 * been up a while — useless for spotting a spike. This differences `cpuSecondsTotal` instead.
 */
export function cpuPercentBetween(previous, current, cores) {
  const rate = ratePerSecond(previous, current);
  if (rate === null) return null;
  const n = Number(cores);
  if (!Number.isFinite(n) || n <= 0) return null;
  return (rate / n) * 100;
}

// ── Filtering ─────────────────────────────────────────────────────────────────

function matches(haystack, needle) {
  return String(haystack ?? '').toLowerCase().includes(needle);
}

/**
 * Lobbies matching the directory's filters. Done client-side because the counts are small and it keeps
 * the endpoint dumb — and because typing in a filter box should not cost a round trip.
 */
export function filterLobbies(lobbies, { game = '', code = '', status = '' } = {}) {
  const gameNeedle = game.trim().toLowerCase();
  const codeNeedle = code.trim().toLowerCase();
  const wanted = status.trim().toLowerCase();
  return (lobbies || []).filter((l) => {
    if (wanted && String(l.status).toLowerCase() !== wanted) return false;
    if (codeNeedle && !matches(l.code, codeNeedle)) return false;
    // A game filter should match what the operator can SEE, which is the title — but ids are what
    // appear in logs and URLs, so match either.
    if (gameNeedle && !matches(l.gameName, gameNeedle) && !matches(l.gameId, gameNeedle)) return false;
    return true;
  });
}

/** Games matching the catalog view's filters. */
export function filterGames(games, { q = '', availability = '' } = {}) {
  const needle = q.trim().toLowerCase();
  const wanted = availability.trim().toLowerCase();
  return (games || []).filter((g) => {
    if (wanted && String(g.availability).toLowerCase() !== wanted) return false;
    if (needle && !matches(g.name, needle) && !matches(g.id, needle)) return false;
    return true;
  });
}

// ── Logs ──────────────────────────────────────────────────────────────────────

/** Serilog levels, least to most severe — the order the level filter offers them in. */
export const LOG_LEVELS = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'];

/** A CSS class per level, so the stream is scannable by colour rather than by reading every line. */
export function logLevelClass(level) {
  switch (String(level || '').toLowerCase()) {
    case 'fatal': return 'log-fatal';
    case 'error': return 'log-error';
    case 'warning': return 'log-warning';
    case 'debug':
    case 'verbose': return 'log-quiet';
    default: return 'log-info';
  }
}

/** Level as the three-letter tag the log file uses ("INF", "WRN"), so the two read alike. */
export function logLevelTag(level) {
  const text = String(level || '').toUpperCase();
  return text.slice(0, 3) || '---';
}

/**
 * Appends new entries to the visible stream, keeping it bounded and in sequence order.
 *
 * De-duplicates by sequence because a filter change re-reads from cursor 0 while a poll may already have
 * delivered some of the same entries — without this, changing the filter mid-stream duplicates every
 * line still in the buffer.
 */
export function appendLogEntries(existing, incoming, limit = 500) {
  const seen = new Set((existing || []).map((e) => e.seq));
  const merged = [...(existing || [])];
  for (const entry of incoming || []) {
    if (seen.has(entry.seq)) continue;
    seen.add(entry.seq);
    merged.push(entry);
  }
  merged.sort((a, b) => a.seq - b.seq);
  return merged.length > limit ? merged.slice(merged.length - limit) : merged;
}

// ── Availability ──────────────────────────────────────────────────────────────

/** The three operator-settable states, with the label and explanation the portal shows for each. */
export const AVAILABILITY = [
  { value: 'available', label: 'Available', hint: 'Listed for players and startable.' },
  { value: 'disabled', label: 'Disabled', hint: 'Hidden, and new lobbies are refused. Running lobbies continue.' },
  { value: 'staged', label: 'Staged', hint: 'Hidden, but still startable via its direct link. Visibility only — not access control.' },
];

export function availabilityLabel(value) {
  return AVAILABILITY.find((a) => a.value === String(value).toLowerCase())?.label ?? String(value ?? '--');
}
