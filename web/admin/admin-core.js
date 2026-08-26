// Pure helpers for the admin portal: formatting, filtering, and the rate arithmetic the dashboard does
// on top of the server's cumulative counters. Same split as web/kb-core.js next to web/shell.js — nothing
// here touches the DOM, fetch or timers, so it is unit-tested in the plain Node environment while
// admin.js (which does all three) is tested under jsdom.

// ── Settings Registry & Logical Groups ────────────────────────────────────────

/**
 * The declarative registry of admin setting groups and individual settings.
 * Single source of truth for the sidebar tree view, Visual Studio style search indexing,
 * deep linking, and scrollspy navigation.
 */
export const SETTINGS_GROUPS = [
  {
    id: 'monitoring',
    label: 'Monitoring & Health',
    icon: 'activity',
    settings: [
      {
        id: 'setting-overview',
        legacyTab: 'overview',
        label: 'System Overview',
        icon: 'overview',
        description: 'Server uptime, active lobbies, connected players, registered games, memory, CPU, and diagnostics.',
        keywords: ['uptime', 'metrics', 'cpu', 'memory', 'heap', 'players', 'lobbies', 'diagnostics', 'health', 'server'],
      },
      {
        id: 'setting-history',
        legacyTab: 'overview',
        label: 'Recent History',
        icon: 'history',
        description: 'Time series sparkline charts for CPU, memory, connected players, and active lobbies.',
        keywords: ['history', 'telemetry', 'graphs', 'sparklines', 'cpu', 'memory', 'samples', 'trends', 'time series'],
      },
      {
        id: 'setting-cost',
        legacyTab: 'overview',
        label: 'Per-Game Server Cost',
        icon: 'cost',
        description: 'Network relay throughput, socket frames and bytes sent, fan-out ratios, and authority CPU time.',
        keywords: ['cost', 'traffic', 'network', 'fan-out', 'frames', 'bytes', 'socket', 'relay', 'authority cpu', 'bandwidth'],
      },
      {
        id: 'setting-lobbies',
        legacyTab: 'lobbies',
        label: 'Active Lobbies',
        icon: 'lobbies',
        description: 'Live lobby directory, room inspection, member lists, kicking players, and closing or purging lobbies.',
        keywords: ['lobbies', 'rooms', 'players', 'members', 'kick', 'purge', 'stale', 'active', 'sessions'],
      },
      {
        id: 'setting-logs',
        legacyTab: 'logs',
        label: 'System Logs',
        icon: 'logs',
        description: 'Real-time server log stream, filter by severity level or subsystem, follow live, and download log files.',
        keywords: ['logs', 'stream', 'events', 'exceptions', 'errors', 'download', 'warnings', 'subsystem', 'diagnostics'],
      },
    ],
  },
  {
    id: 'games',
    label: 'Game Management',
    icon: 'games',
    settings: [
      {
        id: 'setting-games',
        legacyTab: 'games',
        label: 'Game Catalog',
        icon: 'games',
        description: 'Installed game catalog, availability controls (available, disabled, staged), disk breakdown, and rescan.',
        keywords: ['games', 'catalog', 'installed', 'availability', 'disabled', 'staged', 'delete', 'rescan', 'disk', 'package'],
      },
      {
        id: 'setting-marketplace',
        legacyTab: 'marketplace',
        label: 'Marketplace & Packages',
        icon: 'marketplace',
        description: 'Marketplace catalog, package update jobs, manual .kbg uploads, version rollback, and package sources.',
        keywords: ['marketplace', 'packages', 'kbg', 'upload', 'sources', 'updates', 'jobs', 'operations', 'rollback', 'install'],
      },
    ],
  },
  {
    id: 'platform',
    label: 'Platform Policies & Security',
    icon: 'platform',
    settings: [
      {
        id: 'setting-maintenance',
        legacyTab: 'platform',
        label: 'Maintenance Mode',
        icon: 'maintenance',
        description: 'Global maintenance mode toggle and player notification banner, blocking new lobbies across all games.',
        keywords: ['maintenance', 'drain', 'freeze', 'block lobbies', 'notice', 'offline', 'service'],
      },
      {
        id: 'setting-announcement',
        legacyTab: 'platform',
        label: 'Player Announcement',
        icon: 'announcement',
        description: 'Broadcast announcement banner displayed on the player home page with configurable severity.',
        keywords: ['announcement', 'broadcast', 'banner', 'player message', 'notice', 'alert', 'severity', 'info', 'warning'],
      },
      {
        id: 'setting-limits',
        legacyTab: 'platform',
        label: 'Limits & Capacity',
        icon: 'limits',
        description: 'Abuse protection and capacity limits: message rates, burst caps, IP connection caps, and lobby caps.',
        keywords: ['limits', 'caps', 'rate limit', 'burst', 'throttle', 'connections', 'messages per second', 'max lobbies', 'capacity'],
      },
      {
        id: 'setting-schedule',
        legacyTab: 'platform',
        label: 'Update Schedule',
        icon: 'schedule',
        description: 'Automated check schedule for marketplace game updates: cadence, day of week, and UTC hour.',
        keywords: ['schedule', 'updates', 'cron', 'cadence', 'automatic', 'utc', 'hour', 'check', 'timer'],
      },
      {
        id: 'setting-room-codes',
        legacyTab: 'platform',
        label: 'Banned Room Codes',
        icon: 'roomCodes',
        description: 'Blocklist of profanity words and wildcard pattern filters the room code generator will never issue.',
        keywords: ['room codes', 'banned', 'blocked', 'filter', 'profanity', 'words', 'patterns', 'generator', 'blocklist'],
      },
      {
        id: 'setting-webhooks',
        legacyTab: 'platform',
        label: 'Webhooks & Alerts',
        icon: 'webhooks',
        description: 'Outbound webhook endpoints for error alerts, update completions, maintenance changes, and resource thresholds.',
        keywords: ['webhooks', 'endpoints', 'events', 'discord', 'slack', 'alerts', 'notifications', 'http', 'integration'],
      },
      {
        id: 'setting-startup-config',
        legacyTab: 'platform',
        label: 'Startup Configuration',
        icon: 'startup',
        description: 'Immutable server configuration: handshake timeouts, reconnect grace period, login attempt limits, and network isolation.',
        keywords: ['startup', 'configuration', 'immutable', 'handshake', 'reconnect grace', 'login attempts', 'port isolation', 'security'],
      },
    ],
  },
];

/** Flat list of all registered settings across all groups. */
export const ALL_SETTINGS = SETTINGS_GROUPS.flatMap((g) => g.settings);

/** The dashboard tabs/settings IDs in order. */
export const TABS = ['overview', 'lobbies', 'games', 'marketplace', 'logs', 'platform'];

/**
 * Resolves the target setting ID from a URL fragment or query.
 * Accepts exact setting IDs ('setting-limits'), stripped names ('limits'),
 * legacy tab names ('platform'), or group IDs ('monitoring').
 */
export function settingFromHash(hash, groups = SETTINGS_GROUPS) {
  const clean = String(hash || '').replace(/^#/, '').trim().toLowerCase();
  if (!clean) return groups[0]?.settings[0]?.id ?? 'setting-overview';

  const allSettings = groups.flatMap((g) => g.settings);
  const exact = allSettings.find((s) => s.id.toLowerCase() === clean);
  if (exact) return exact.id;

  const withPrefix = allSettings.find((s) => s.id.toLowerCase() === `setting-${clean}`);
  if (withPrefix) return withPrefix.id;

  const byGroup = groups.find((g) => g.id.toLowerCase() === clean);
  if (byGroup && byGroup.settings[0]) return byGroup.settings[0].id;

  const byLegacy = allSettings.find((s) => s.legacyTab === clean);
  if (byLegacy) return byLegacy.id;

  return groups[0]?.settings[0]?.id ?? 'setting-overview';
}

/**
 * The tab a URL fragment selects, or the first tab when the fragment names nothing valid.
 */
export function tabFromHash(hash, tabs = TABS) {
  const clean = String(hash || '').replace(/^#/, '').trim().toLowerCase();
  if (!clean) return tabs[0];
  if (tabs.includes(clean)) return clean;
  // If it's a setting id like setting-lobbies or lobbies, map to legacy tab
  const settingId = settingFromHash(hash);
  const setting = ALL_SETTINGS.find((s) => s.id === settingId);
  if (setting && tabs.includes(setting.legacyTab)) return setting.legacyTab;
  return tabs[0];
}

/**
 * Evaluates a search query against all groups and settings (title, group label, description, keywords).
 * Returns which settings and groups should be visible in the tree view and the scrolling panel.
 */
export function filterSettings(query, groups = SETTINGS_GROUPS) {
  const needle = String(query ?? '').trim().toLowerCase();
  if (!needle) {
    const allSettingIds = new Set(groups.flatMap((g) => g.settings.map((s) => s.id)));
    const allGroupIds = new Set(groups.map((g) => g.id));
    return {
      query: '',
      isFiltering: false,
      matchingSettingIds: allSettingIds,
      matchingGroupIds: allGroupIds,
      totalMatches: allSettingIds.size,
    };
  }

  const matchingSettingIds = new Set();
  const matchingGroupIds = new Set();

  for (const group of groups) {
    const groupMatches = String(group.label || '').toLowerCase().includes(needle);
    for (const setting of group.settings) {
      const settingMatches =
        groupMatches ||
        String(setting.label || '').toLowerCase().includes(needle) ||
        String(setting.description || '').toLowerCase().includes(needle) ||
        (setting.keywords || []).some((kw) => String(kw).toLowerCase().includes(needle));

      if (settingMatches) {
        matchingSettingIds.add(setting.id);
        matchingGroupIds.add(group.id);
      }
    }
  }

  return {
    query: needle,
    isFiltering: true,
    matchingSettingIds,
    matchingGroupIds,
    totalMatches: matchingSettingIds.size,
  };
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

/** An ISO timestamp as a local date AND time, for something that may be days away. */
export function formatDateTime(iso) {
  const at = new Date(iso);
  return Number.isNaN(at.getTime()) ? '--' : at.toLocaleString();
}

// ── Update schedule ───────────────────────────────────────────────────────────

/**
 * The label for one hour in the schedule's hour picker: the UTC hour it stores, and the same instant on
 * the reader's own clock.
 *
 * The schedule IS UTC, and the stored value never moves — that is the whole point of choosing UTC. But
 * "14:00 UTC" alone leaves the operator doing the arithmetic to answer the only question they actually
 * have, which is whether the check lands somewhere quiet for them. So both are shown.
 *
 * The local half is computed against a REFERENCE DATE (today by default), because an offset is a
 * property of an instant, not of a zone: somewhere on daylight saving, an hour labelled 10:00 in
 * January reads 11:00 in July. Labelling it "as of today" is honest and useful; pretending a fixed
 * mapping exists would not be. A local time that lands on the day before or after carries that, since
 * "03:00 UTC" reading as "10:00 PM" is otherwise quietly the wrong evening.
 */
export function hourOptionLabel(hourUtc, reference = new Date()) {
  const utc = `${String(hourUtc).padStart(2, '0')}:00 UTC`;
  const at = new Date(Date.UTC(
    reference.getUTCFullYear(), reference.getUTCMonth(), reference.getUTCDate(), hourUtc));
  if (Number.isNaN(at.getTime())) return utc;

  const local = at.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  const dayShift = at.getDate() - at.getUTCDate();
  // Compare the day-of-month, correcting for a month boundary (31 → 1 is +1 day, not −30).
  const shift = dayShift > 1 ? -1 : dayShift < -1 ? 1 : dayShift;
  const suffix = shift === 0 ? '' : shift < 0 ? ', prev. day' : ', next day';
  return `${utc} (${local} local${suffix})`;
}

/**
 * The sentence under the update-schedule form.
 *
 * The next run is stated in the operator's OWN time zone even though the schedule is set in UTC: the
 * point of the line is "will this happen when I think it will", and answering it in the same zone they
 * just typed proves nothing. The enrolment count is here because a schedule with nothing enrolled makes
 * no request at all — an operator who set one and saw no activity would otherwise assume it was broken.
 */
export function scheduleNote(schedule) {
  if (!schedule) return '';
  const parts = [`Checks run ${schedule.summary || 'on no schedule'}.`];
  parts.push(schedule.nextRunUtc
    ? `Next check ${formatDateTime(schedule.nextRunUtc)} (your time).`
    : 'No check is scheduled.');
  const enrolled = toNumber(schedule.enrolled) ?? 0;
  parts.push(enrolled > 0
    ? `${formatCount(enrolled)} game(s) enrolled in automatic updates.`
    : 'No game is enrolled in automatic updates, so a check currently installs nothing — '
      + 'set a game’s update policy on the Marketplace tab.');
  return parts.join(' ');
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

/**
 * The Game Catalog's SDK badge: { label, className, title } or null when nothing should be shown.
 *
 * `unknown` renders NOTHING on purpose. Most games on a typical server carry no SDK stamp — every
 * hand-written one, and every package built before the stamp existed — so a badge for it would sit on
 * nearly every card and say only "this is normal". `current` is silent for the same reason: a badge
 * that is almost always present is not read.
 *
 * That leaves the two cases an operator can act on, which is what makes them worth a badge at all.
 */
export function sdkBadge(game, serverSdkVersion) {
  const status = String(game?.sdkStatus ?? 'unknown').toLowerCase();
  const stamped = Object.entries(game?.sdk ?? {})
    .map(([id, version]) => `${id} ${version}`)
    .sort()
    .join(', ');

  switch (status) {
    case 'behind':
      return {
        label: 'SDK outdated',
        className: 'badge badge-warning',
        title: `Built against ${stamped || 'an older SDK'}; this server ships ${serverSdkVersion}. `
          + 'Rebuild the game with `knockbox addon update` to pick up client fixes.',
      };
    case 'ahead':
      return {
        label: 'SDK newer',
        className: 'badge badge-muted',
        title: `Built against ${stamped}; this server ships ${serverSdkVersion}. `
          + 'It will still run — the wire protocol is versioned separately — but this server is the older side.',
      };
    default:
      return null;
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
  { value: 'incompatible', label: 'Incompatible', badge: 'badge-danger', hint: 'The offered version declares it does not run on this server version. It is never installed automatically, and installing it by hand stages it rather than publishing it to players.' },
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
/**
 * The value an option is identified by. A version alone is NOT unique — a retained backup of the
 * installed version has the same one — and matching on it picked whichever came first in the list,
 * which is the newest available. Selecting "1.0.0 — backup" therefore resolved to the installed entry,
 * rendered "Reinstall", and installed 2.0.0: no danger styling, no rollback confirmation, and the
 * opposite of what was asked for. Kind and version together are unique by construction, because that
 * pair is exactly what versionOptions deduplicates on.
 */
export function versionOptionValue(option) {
  return `${option?.kind ?? ''}:${option?.version ?? ''}`;
}

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
 *
 * `installBlocked` is the DEPLOYMENT-level reason nothing can be installed at all (an unwritable managed
 * root, packages switched off). It is a property of the response, not of an entry — the server reports it
 * once as `installBlockedReason` alongside `canInstall` — so the caller has to pass it in. Reading it off
 * `entry` instead, as this used to, silently never matched: no such field is ever sent on an entry, so on
 * a deployment that cannot install anything every card offered an enabled button that could only fail.
 */
export function versionAction(entry, selected, installBlocked = null) {
  if (!entry) return { kind: 'none', label: 'Install', danger: false, blockedReason: 'Nothing selected.' };

  if (entry.status === 'unusable') {
    return {
      kind: 'none',
      label: 'Unusable',
      danger: false,
      blockedReason: entry.reason || pluginStatusHint(entry.status),
    };
  }
  if (installBlocked) {
    return {
      kind: 'none',
      label: entry.status === 'incompatible' ? 'Install Anyways' : 'Install',
      danger: false,
      blockedReason: installBlocked,
    };
  }

  const options = versionOptions(entry);
  const target = options.find((o) => versionOptionValue(o) === String(selected ?? ''))
    ?? options[0]
    ?? { version: entry.availableVersion ?? null, kind: 'available' };
  // Every branch reports the version it resolved, so the caller never has to re-derive it from the
  // select — which is what made the rollback POST and the button's own decision two separate answers.
  const at = (action) => ({ ...action, version: target.version ?? null, targetKind: target.kind });

  if (!entry.installed) {
    if (entry.status === 'incompatible') {
      return at({ kind: 'install', label: 'Install Anyways', danger: true, blockedReason: null, incompatible: true });
    }
    return at({ kind: 'install', label: 'Install', danger: false, blockedReason: null });
  }
  if (target.kind === 'backup') {
    // Danger styling because it replaces what is running with older bytes — the one action here an
    // operator can regret.
    return at({ kind: 'rollback', label: 'Roll back', danger: true, blockedReason: null });
  }
  if (target.kind === 'installed' || (target.version ?? '') === (entry.installedVersion ?? '')) {
    // A reinstall re-fetches from the source that offers the game — and `installedOnly` means no enabled
    // source offers it (an upload, or an entry that was withdrawn). The install route resolves the id out
    // of the fetched catalogs, so the button could only ever produce a 404. Say why instead.
    if (entry.status === 'installedOnly') {
      return at({
        kind: 'none',
        label: 'Reinstall',
        danger: false,
        blockedReason: 'No enabled marketplace offers this game, so there is nothing to reinstall from. '
          + 'Upload the package again to replace it.',
      });
    }
    return at({ kind: 'reinstall', label: 'Reinstall', danger: false, blockedReason: null });
  }
  if (entry.status === 'incompatible') {
    return at({ kind: 'update', label: 'Install Anyways', danger: true, blockedReason: null, incompatible: true });
  }
  return at({ kind: 'update', label: 'Update', danger: false, blockedReason: null });
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

// ── Platform limits ───────────────────────────────────────────────────────────

/**
 * The runtime-editable limits, in the order the form renders them. `key` is the wire name on both
 * AdminLimitValues and AdminLimitsRequest, so a field is one entry here and nothing else client-side.
 *
 * The table lives here rather than in admin.js for the same reason AVAILABILITY does: a test can pin it
 * against the server's own record, which is what catches a knob added on one side only.
 */
export const LIMIT_FIELDS = [
  {
    key: 'controlMessagesPerSecond', label: 'Control messages / second', integer: false,
    hint: 'Lobby operations from one shell socket. Sustained spam past the burst closes the connection.',
  },
  {
    key: 'controlMessagesBurst', label: 'Control burst', integer: false,
    hint: 'How many control messages may arrive back-to-back. Must be at least 1 unless the rate is 0.',
  },
  {
    key: 'gameMessagesPerSecond', label: 'Game messages / second', integer: false,
    hint: 'Per game socket. A host broadcasting state ~20x/s sits well under the default of 30.',
  },
  {
    key: 'gameMessagesBurst', label: 'Game burst', integer: false,
    hint: 'Absorbs legitimate spikes, e.g. a host re-syncing several joiners at once.',
  },
  {
    key: 'lobbyCreatesPerMinute', label: 'Lobby creates / minute', integer: true,
    hint: 'Per player. Refuses the operation without closing the connection — codes are a shared namespace.',
  },
  {
    key: 'maxConnectionsPerIp', label: 'Connections per IP', integer: true,
    hint: 'One player legitimately holds two (shell + game) per tab. Behind a proxy this needs ForwardedHeaders.',
  },
  {
    key: 'maxLobbies', label: 'Max lobbies (platform)', integer: true,
    hint: 'Total simultaneous lobbies across every game. Existing lobbies are never closed by a cap.',
  },
  {
    key: 'maxLobbiesPerGame', label: 'Max lobbies per game', integer: true,
    hint: 'Stops one popular game consuming every remaining slot.',
  },
];

/** The startup-only limits, with why each one is not editable here. */
export const STARTUP_LIMITS = [
  { key: 'handshakeTimeoutSeconds', label: 'Handshake timeout (s)' },
  { key: 'disconnectGraceSeconds', label: 'Reconnect grace (s)' },
  { key: 'adminLoginAttemptsPerMinute', label: 'Admin login attempts / minute (per IP)' },
  { key: 'adminLoginAttemptsPerMinuteGlobal', label: 'Admin login attempts / minute (server-wide)' },
];

/**
 * Turns the form's raw strings into the override object the server takes, or reports the first field that
 * doesn't make sense.
 *
 * A blank field is `null` — "not overridden" — which is also how the operator reverts one. That is why the
 * request is a full replacement rather than a patch: with a patch, blank and absent would be the same
 * bytes and could not mean two different things.
 *
 * The server validates this again and is the authority. Doing it here too is not duplication for its own
 * sake: a round trip to be told "that's not a number" is a worse form than one that says so as you type.
 */
export function validateLimits(raw, fields = LIMIT_FIELDS) {
  const values = {};
  for (const field of fields) {
    const text = String(raw?.[field.key] ?? '').trim();
    if (text === '') { values[field.key] = null; continue; }

    const n = Number(text);
    if (!Number.isFinite(n) || n < 0) {
      return { ok: false, error: `${field.label} must be 0 or more, or empty to use the default.`, values: null };
    }
    if (field.integer && !Number.isInteger(n)) {
      return { ok: false, error: `${field.label} must be a whole number.`, values: null };
    }
    values[field.key] = n;
  }

  // The one combination that locks everyone out rather than merely limiting them, checked here so the
  // operator hears it before the round trip. Only judged when BOTH halves are on the form — a burst
  // against a configured rate is the server's call, since only it knows that rate.
  for (const [rateKey, burstKey, name] of [
    ['controlMessagesPerSecond', 'controlMessagesBurst', 'Control'],
    ['gameMessagesPerSecond', 'gameMessagesBurst', 'Game'],
  ]) {
    const rate = values[rateKey];
    const burst = values[burstKey];
    if (rate !== null && rate > 0 && burst !== null && burst < 1) {
      return { ok: false, error: `${name} burst must be at least 1 while its rate is above 0, or every message is refused.`, values: null };
    }
  }

  return { ok: true, error: null, values };
}

/** True when nothing on the form is overridden — what "revert all" produces. */
export function noLimitOverrides(values, fields = LIMIT_FIELDS) {
  return fields.every((f) => values?.[f.key] === null || values?.[f.key] === undefined);
}

// ── Room codes ────────────────────────────────────────────────────────────────

/** The code alphabet, mirrored from LobbyManager.CodeAlphabet. The server reports it too — this is the
 *  fallback so the form can validate before the first response arrives. */
export const CODE_ALPHABET = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
export const CODE_LENGTH = 4;

/**
 * Normalises and checks one blocklist entry, returning `{ ok, value, error, unreachable }`.
 *
 * `unreachable` is not an error: the entry is legal, but the generator can never produce it because the
 * alphabet omits O/0/I/1 (too easily misread aloud). Saying so is the whole point — an entry that looks
 * like it is working and isn't is worse than one that is refused.
 */
export function checkCodeEntry(raw, { pattern = false, alphabet = CODE_ALPHABET } = {}) {
  const value = String(raw ?? '').trim().toUpperCase();
  if (!value) return { ok: false, value, error: 'Enter something to block.', unreachable: false };

  const allowed = pattern ? /^[A-Z0-9?*]+$/ : /^[A-Z0-9]+$/;
  if (!allowed.test(value)) {
    return {
      ok: false,
      value,
      error: pattern
        ? 'A pattern may contain letters, digits, ? (one character) and * (any run).'
        : 'A word may contain only letters and digits. Use the pattern field for ? and *.',
      unreachable: false,
    };
  }
  // A word longer than a code can never occur inside one. A pattern may be longer than the code only
  // because its wildcards are characters too.
  const limit = pattern ? CODE_LENGTH + 2 : CODE_LENGTH;
  if (value.length > limit) {
    return {
      ok: false,
      value,
      error: `Codes are ${CODE_LENGTH} characters, so nothing longer than ${limit} can match one.`,
      unreachable: false,
    };
  }

  const unreachable = [...value].some((c) => c !== '?' && c !== '*' && !alphabet.includes(c));
  return { ok: true, value, error: null, unreachable };
}

/** Blocked codes as a share of the whole space, for the readout. Null when either number is missing. */
export function blockedShare(blocked, codeSpace) {
  const b = toNumber(blocked);
  const space = toNumber(codeSpace);
  if (b === null || space === null || space <= 0) return null;
  return (b / space) * 100;
}

// ── Metric history (§5.2) ─────────────────────────────────────────────────────

/**
 * Merges newly-polled samples into the ones already held, de-duped by `sequence` and bounded.
 *
 * Same shape as appendLogEntries and mergeJobs, and for the same reason: the endpoint is cursor-polled, so
 * a reconnect or a re-entered tab can hand back samples we already have.
 */
export function mergeSamples(existing, incoming, limit = 240) {
  const bySeq = new Map();
  for (const sample of [...(existing || []), ...(incoming || [])]) {
    if (sample && Number.isFinite(Number(sample.sequence))) bySeq.set(Number(sample.sequence), sample);
  }
  return [...bySeq.values()]
    .sort((a, b) => Number(a.sequence) - Number(b.sequence))
    .slice(-Math.max(1, limit));
}

/**
 * Turns a cumulative counter into a series of per-second rates, one per adjacent pair of samples.
 *
 * Keeps ratePerSecond's discipline: a pair that yields no answer (no elapsed time, or a counter that went
 * backwards, meaning the server restarted) is **omitted** rather than plotted as zero. A false trough at the
 * moment of a restart is worse than a gap, because a gap looks like what it is.
 */
export function seriesRate(samples, key) {
  const points = [];
  for (let i = 1; i < (samples?.length ?? 0); i++) {
    const rate = ratePerSecond(
      { value: samples[i - 1][key], at: samples[i - 1].at },
      { value: samples[i][key], at: samples[i].at });
    if (rate !== null) points.push({ at: samples[i].at, value: rate });
  }
  return points;
}

/** A gauge series (memory, players — values, not counters) as plot points. */
export function seriesValue(samples, key) {
  return (samples || [])
    .map((s) => ({ at: s.at, value: Number(s[key]) }))
    .filter((p) => Number.isFinite(p.value));
}

/**
 * CPU percent of one core-equivalent, per adjacent pair. `cores` divides the rate, so 100% means one core
 * saturated regardless of machine size — the same convention cpuPercentBetween uses for the live number.
 */
export function seriesCpuPercent(samples, cores) {
  return seriesRate(samples, 'cpuSeconds')
    .map((p) => ({ at: p.at, value: cores > 0 ? (p.value / cores) * 100 : p.value }));
}

/**
 * Reduces a series to at most `max` points by averaging equal-width buckets.
 *
 * An hour of 15-second samples is 240 points in a chart a couple of hundred pixels wide, so most of them
 * land on a pixel that already has one. Averaging rather than taking every Nth keeps a spike visible instead
 * of letting sampling luck decide whether it appears at all.
 */
export function downsample(points, max = 120) {
  const list = points || [];
  if (list.length <= max || max < 1) return list;
  const bucketSize = list.length / max;
  const out = [];
  for (let i = 0; i < max; i++) {
    const from = Math.floor(i * bucketSize);
    const to = Math.min(list.length, Math.floor((i + 1) * bucketSize));
    if (to <= from) continue;
    let sum = 0;
    for (let j = from; j < to; j++) sum += list[j].value;
    out.push({ at: list[to - 1].at, value: sum / (to - from) });
  }
  return out;
}

/**
 * An SVG path for a sparkline, plus the range it was drawn against.
 *
 * Returns `{ path, min, max, last }`, or `path: null` when there is nothing to draw — the caller shows a
 * "not enough data yet" note rather than an empty box that looks broken. The y-axis starts at 0 for a
 * counter-derived series so a rate that doubled looks like it doubled; a flat series gets a small span so it
 * draws a line through the middle instead of dividing by zero.
 */
export function sparklinePath(points, { width = 240, height = 40, padding = 2 } = {}) {
  const list = (points || []).filter((p) => Number.isFinite(Number(p.value)));
  if (list.length < 2) return { path: null, min: 0, max: 0, last: list[0]?.value ?? null };

  const values = list.map((p) => Number(p.value));
  const max = Math.max(...values);
  const min = Math.min(0, ...values);
  const span = max - min || Math.max(1, Math.abs(max)) * 0.1;

  const usableWidth = Math.max(1, width - padding * 2);
  const usableHeight = Math.max(1, height - padding * 2);
  const step = usableWidth / (list.length - 1);

  const path = values
    .map((value, i) => {
      const x = padding + i * step;
      const y = padding + usableHeight - ((value - min) / span) * usableHeight;
      return `${i === 0 ? 'M' : 'L'}${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(' ');

  return { path, min, max, last: values[values.length - 1] };
}

// ── Webhooks ──────────────────────────────────────────────────────────────────

/**
 * The events an endpoint can subscribe to, with what each one actually means. Pinned against the server's
 * WebhookEvent enum by a test, the same way AVAILABILITY is against GameAvailability.
 */
export const WEBHOOK_EVENTS = [
  { value: 'logError', label: 'Errors', hint: 'Any error-or-worse log event. Rate-limited, with a count of what was suppressed.' },
  { value: 'updateApplied', label: 'Update applied', hint: 'A game finished installing or updating.' },
  { value: 'updateFailed', label: 'Update failed', hint: 'An install, update or rollback failed or was cancelled.' },
  { value: 'maintenanceChanged', label: 'Maintenance toggled', hint: 'Global maintenance mode was turned on or off.' },
  { value: 'resourceThreshold', label: 'Resource threshold', hint: 'Memory or CPU crossed the configured threshold, or came back under it.' },
];

export function webhookEventLabel(value) {
  return WEBHOOK_EVENTS.find((e) => e.value === value)?.label ?? value;
}

/**
 * Whether a webhook endpoint is worth sending to the server, and why not if it isn't.
 *
 * The URL rule mirrors the server's (which is the downloader's `IsAllowedUrl`): https anywhere, or http on
 * loopback for a local monitoring agent. Checked here only so the operator hears it before the round trip —
 * the server is still the authority, and a refusal from it is surfaced rather than second-guessed.
 */
export function checkWebhook({ id, url } = {}) {
  const cleanId = String(id ?? '').trim();
  if (!/^[A-Za-z0-9_-]{1,32}$/.test(cleanId)) {
    return { ok: false, error: 'Id must be 1-32 characters: letters, digits, dash or underscore.' };
  }

  const cleanUrl = String(url ?? '').trim();
  let parsed;
  try { parsed = new URL(cleanUrl); } catch { parsed = null; }
  if (!parsed) return { ok: false, error: 'Enter the full URL, including https://.' };

  const loopback = parsed.hostname === 'localhost' || parsed.hostname === '127.0.0.1' || parsed.hostname === '[::1]';
  if (parsed.protocol !== 'https:' && !(parsed.protocol === 'http:' && loopback)) {
    return { ok: false, error: 'The URL must be https, or http on loopback (for a local monitoring agent).' };
  }
  return { ok: true, error: null, id: cleanId, url: cleanUrl };
}

/** How the last delivery went, as one short phrase. Null when nothing has been sent yet. */
export function webhookLastDelivery(endpoint) {
  if (!endpoint?.lastAt) return null;
  if (endpoint.lastOk) return `OK${endpoint.lastStatus ? ` (${endpoint.lastStatus})` : ''}`;
  // No status means the request never got one — DNS, TLS or a timeout — which reads very differently from
  // a 404, so the two are not collapsed into "failed".
  return endpoint.lastStatus
    ? `Failed (${endpoint.lastStatus})`
    : `No response${endpoint.lastError ? `: ${endpoint.lastError}` : ''}`;
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

// ── Sidebar State ─────────────────────────────────────────────────────────────

export const SIDEBAR_COLLAPSED_KEY = 'kb_admin_sidebar_collapsed';

/**
 * Reads whether the sidebar was stored as collapsed. Returns false when unset, invalid,
 * or when storage is unavailable.
 */
export function getStoredSidebarCollapsed(storage = (typeof localStorage !== 'undefined' ? localStorage : null)) {
  try {
    return storage?.getItem(SIDEBAR_COLLAPSED_KEY) === 'true';
  } catch {
    return false;
  }
}

/**
 * Stores the sidebar collapsed preference. Removes or clears when false.
 */
export function setStoredSidebarCollapsed(collapsed, storage = (typeof localStorage !== 'undefined' ? localStorage : null)) {
  try {
    if (!storage) return;
    if (collapsed) {
      storage.setItem(SIDEBAR_COLLAPSED_KEY, 'true');
    } else {
      storage.removeItem(SIDEBAR_COLLAPSED_KEY);
    }
  } catch {
    // Quota / security restrictions in restrictive iframe/private browsing modes
  }
}

