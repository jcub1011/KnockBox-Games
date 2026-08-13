// Pure helpers for the admin portal: formatting, filtering, and the rate arithmetic the dashboard does
// on top of the server's cumulative counters. Same split as web/kb-core.js next to web/shell.js — nothing
// here touches the DOM, fetch or timers, so it is unit-tested in the plain Node environment while
// admin.js (which does all three) is tested under jsdom.

// ── Tabs ──────────────────────────────────────────────────────────────────────

/** The dashboard tabs, in sidebar order. The nav's data-tab attributes must match these. */
export const TABS = ['overview', 'lobbies', 'games', 'marketplace', 'logs'];

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

// ── Lifecycle ─────────────────────────────────────────────────────────────────

/**
 * What the install engine is doing to a game right now.
 *
 * Deliberately NOT part of AVAILABILITY. That select is a command control — choosing an option POSTs it —
 * so offering a value the server would have to refuse is worse than not offering it at all. These are
 * engine state, never operator policy, and they render as a badge instead.
 */
export const LIFECYCLE = [
  // Empty label: the overwhelmingly common state renders nothing rather than a badge saying "fine".
  { value: 'ready', label: '', hint: '' },
  { value: 'draining', label: 'Draining', hint: 'Waiting for running lobbies to finish before updating. New lobbies are refused.' },
  { value: 'updating', label: 'Updating', hint: 'Files are being swapped. New lobbies are refused.' },
];

export function lifecycleLabel(value) {
  const entry = LIFECYCLE.find((l) => l.value === String(value ?? '').toLowerCase());
  return entry ? entry.label : String(value ?? '');
}

export function lifecycleClass(value) {
  switch (String(value ?? '').toLowerCase()) {
    case 'updating': return 'badge-warning';
    case 'draining': return 'badge-muted';
    default: return '';
  }
}

/** True when the engine is mid-swap, so availability and delete must be held. */
export function isBusyLifecycle(value) {
  const name = String(value ?? 'ready').toLowerCase();
  return name !== 'ready' && name !== '';
}

// ── Marketplace ───────────────────────────────────────────────────────────────

/**
 * Every status a catalog row can carry: the seven PluginUpdateStatus values, plus `installedOnly`,
 * which is not one of them — it marks a managed game no enabled source offers (an upload, or an entry
 * that was withdrawn).
 *
 * Pinned as a list so a server-side enum addition fails loudly in a test here rather than rendering as
 * a bare camelCase string in the UI.
 */
export const PLUGIN_STATUS = [
  { value: 'notInstalled', label: 'Not installed', badge: 'badge-muted', hint: 'Offered by a marketplace but not installed here.' },
  { value: 'upToDate', label: 'Up to date', badge: 'badge-ok', hint: 'The installed version matches what the marketplace offers.' },
  { value: 'updateAvailable', label: 'Update available', badge: 'badge-warning', hint: 'A newer version is published.' },
  { value: 'installedAhead', label: 'Ahead of catalog', badge: 'badge-muted', hint: 'The installed version is newer than the one offered — usually a hand-built package.' },
  { value: 'installedVersionUnknown', label: 'Version unknown', badge: 'badge-muted', hint: 'This game declares no version, so there is nothing to compare. Common for hand-made games.' },
  { value: 'incompatible', label: 'Incompatible', badge: 'badge-danger', hint: 'The offered version does not run on this server version. Never offered as an update.' },
  { value: 'unusable', label: 'Unusable', badge: 'badge-danger', hint: 'The catalog entry is malformed and cannot be acted on.' },
  { value: 'installedOnly', label: 'Installed', badge: 'badge-ok', hint: 'Installed here, but no registered marketplace offers it.' },
];

export function pluginStatusLabel(value) {
  // Unknown values pass through rather than becoming "--", the same contract availabilityLabel has: a
  // server that grew a status should read oddly, not disappear.
  return PLUGIN_STATUS.find((s) => s.value === value)?.label ?? String(value ?? '--');
}

export function pluginStatusClass(value) {
  return PLUGIN_STATUS.find((s) => s.value === value)?.badge ?? 'badge-muted';
}

export function pluginStatusHint(value) {
  return PLUGIN_STATUS.find((s) => s.value === value)?.hint ?? '';
}

const INSTALLED_STATUSES = new Set([
  'upToDate', 'updateAvailable', 'installedAhead', 'installedVersionUnknown', 'installedOnly',
]);
const PROBLEM_STATUSES = new Set(['incompatible', 'unusable']);

/** Catalog rows matching the marketplace view's filters. Client-side, like the other two filters. */
export function filterCatalog(entries, { q = '', status = '', source = '' } = {}) {
  const needle = q.trim().toLowerCase();
  const wantedStatus = status.trim();
  const wantedSource = source.trim().toLowerCase();
  return (entries || []).filter((e) => {
    if (wantedSource && String(e.sourceId ?? '').toLowerCase() !== wantedSource) return false;
    if (wantedStatus === 'installed' && !INSTALLED_STATUSES.has(e.status)) return false;
    else if (wantedStatus === 'problem' && !PROBLEM_STATUSES.has(e.status)) return false;
    else if (wantedStatus && wantedStatus !== 'installed' && wantedStatus !== 'problem'
             && e.status !== wantedStatus) return false;
    if (needle
        && !matches(e.name, needle)
        && !matches(e.id, needle)
        && !(e.tags || []).some((t) => matches(t, needle))) return false;
    return true;
  });
}

// ── Jobs ──────────────────────────────────────────────────────────────────────

/** Statuses a job stays in. Reaching one is what triggers a toast and a catalog re-read. */
export const TERMINAL_JOB_STATUSES = ['succeeded', 'failed', 'cancelled'];

export function isTerminalJob(status) {
  return TERMINAL_JOB_STATUSES.includes(String(status ?? '').toLowerCase());
}

/**
 * Merges a poll's jobs into the visible list.
 *
 * The one behavioural difference from appendLogEntries: this replaces BY jobId rather than appending.
 * A job is a single thing that changes, not a stream of events, so a running job's row has to update in
 * place instead of stacking a new row per poll.
 *
 * A lower sequence for a job we already hold is ignored — two polls can overlap, and letting a stale
 * reply overwrite a newer one would make a finished job flicker back to "downloading".
 */
export function mergeJobs(existing, incoming, limit = 50) {
  const byId = new Map();
  for (const job of existing || []) {
    if (job && job.jobId) byId.set(job.jobId, job);
  }
  for (const job of incoming || []) {
    if (!job || !job.jobId) continue;
    const current = byId.get(job.jobId);
    if (current && Number(current.sequence) > Number(job.sequence)) continue;
    byId.set(job.jobId, job);
  }
  const merged = [...byId.values()].sort((a, b) => Number(b.sequence) - Number(a.sequence));
  return merged.length > limit ? merged.slice(0, limit) : merged;
}

/**
 * A job's progress as a percentage and a label.
 *
 * `percent` is null — never 0 — when the total is unknown, so the bar renders indeterminate. A confident
 * "0%" on a transfer that is actually moving is a claim we cannot make, and it reads as a stall.
 */
export function jobProgress(job) {
  const done = Number(job?.bytesDone);
  const total = Number(job?.bytesTotal);
  if (!Number.isFinite(total) || total <= 0 || !Number.isFinite(done) || done < 0) {
    return { percent: null, label: done > 0 ? formatBytes(done) : '' };
  }
  const percent = Math.max(0, Math.min(100, (done / total) * 100));
  return { percent, label: `${formatBytes(done)} / ${formatBytes(total)}` };
}

// ── Version targeting ─────────────────────────────────────────────────────────

/**
 * The versions a catalog row can be taken to, newest-intent first: what the marketplace offers, what is
 * running now, then each retained backup.
 *
 * One control serves both version targeting and rollback, because rolling back IS targeting an older
 * version you already hold. Two separate controls would be two ways to say the same thing.
 */
export function versionOptions(entry) {
  const options = [];
  const seen = new Set();
  const add = (version, kind) => {
    const key = `${version ?? ''}|${kind}`;
    if (seen.has(key)) return;
    seen.add(key);
    options.push({ version: version ?? null, kind });
  };

  if (entry?.availableVersion) add(entry.availableVersion, 'available');
  if (entry?.installed) add(entry.installedVersion ?? null, 'installed');
  for (const backup of entry?.backups || []) add(backup.version ?? null, 'backup');
  return options;
}

/**
 * What the action button does for the currently selected version.
 *
 * `blockedReason` is set when the row cannot be acted on at all; the caller disables the button and uses
 * the reason as its tooltip, the same pattern the games tab already uses for a non-deletable game.
 */
export function versionAction(entry, selected) {
  if (!entry) return { kind: 'none', label: 'Install', danger: false, blockedReason: 'Nothing selected.' };

  if (entry.status === 'incompatible' || entry.status === 'unusable') {
    return {
      kind: 'none',
      label: entry.status === 'incompatible' ? 'Incompatible' : 'Unusable',
      danger: false,
      blockedReason: entry.reason || pluginStatusHint(entry.status),
    };
  }
  if (entry.installBlockedReason) {
    return { kind: 'none', label: 'Install', danger: false, blockedReason: entry.installBlockedReason };
  }

  const options = versionOptions(entry);
  const target = options.find((o) => (o.version ?? '') === String(selected ?? ''))
    ?? options[0]
    ?? { version: entry.availableVersion ?? null, kind: 'available' };

  if (!entry.installed) {
    return { kind: 'install', label: 'Install', danger: false, blockedReason: null };
  }
  if (target.kind === 'backup') {
    // Danger styling because it replaces what is running with older bytes — the one action here an
    // operator can regret.
    return { kind: 'rollback', label: 'Roll back', danger: true, blockedReason: null };
  }
  if (target.kind === 'installed' || (target.version ?? '') === (entry.installedVersion ?? '')) {
    return { kind: 'reinstall', label: 'Reinstall', danger: false, blockedReason: null };
  }
  return { kind: 'update', label: 'Update', danger: false, blockedReason: null };
}

/** How an update is allowed to treat lobbies that are running right now. */
export const UPDATE_MODES = [
  { value: 'drain', label: 'When games finish', hint: 'Stops new lobbies starting, then updates once the running ones end on their own.' },
  { value: 'auto', label: 'Only if idle', hint: 'Updates only if nobody is playing right now; otherwise does nothing.' },
  { value: 'force', label: 'Now (closes games)', hint: 'Closes every lobby running this game, then updates immediately.' },
];

/** What the scheduled check may do to a game unattended. `manual` means "never, ask me". */
export const UPDATE_POLICIES = [
  { value: 'manual', label: 'Manual', hint: 'Never updated on its own. The portal reports what is available.' },
  { value: 'auto', label: 'Automatic when idle', hint: 'Updates itself whenever the game has no lobbies running.' },
  { value: 'drain', label: 'Automatic, draining', hint: 'Stops new lobbies when an update is found, then updates once the running ones end.' },
  { value: 'force', label: 'Automatic, immediate', hint: 'Closes running lobbies and updates as soon as an update is found.' },
];

/** A version as `v1.2.3`, or a dash when there is none to show. */
export function formatVersion(version) {
  const text = String(version ?? '').trim();
  return text ? `v${text.replace(/^v/i, '')}` : '--';
}

// ── Upload ────────────────────────────────────────────────────────────────────

/**
 * Whether a picked file is worth uploading. ADVISORY ONLY — the server enforces the real cap while
 * streaming, and re-validates the archive. This exists so a ten-minute upload doesn't end in a 413.
 *
 * `maxBytes` comes from the server (KnockBox:MaxPackageBytes); hard-coding it here would drift from the
 * only place that actually decides.
 */
export function uploadGuard(file, { maxBytes = 0 } = {}) {
  if (!file) return { ok: false, error: 'Choose a .kbg package to upload.' };
  if (!/\.kbg$/i.test(file.name || '')) {
    return {
      ok: false,
      error: 'Only .kbg packages can be uploaded. Run knockbox-pack on the game folder to produce one.',
    };
  }
  if (!Number(file.size)) return { ok: false, error: 'That file is empty.' };
  if (maxBytes > 0 && Number(file.size) > maxBytes) {
    return { ok: false, error: `That package is ${formatBytes(file.size)}, over the ${formatBytes(maxBytes)} limit.` };
  }
  return { ok: true, error: null };
}
