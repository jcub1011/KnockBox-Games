// Pure logic behind the admin portal: formatting, filtering, tab routing, and the rate arithmetic the
// dashboard does on top of the server's cumulative counters. Node environment (no DOM) — same split as
// kb-core.test.js, and the reason admin-core.js exists as a module of its own.
import { describe, it, expect } from 'vitest';
import {
  AVAILABILITY, LIFECYCLE, LIMIT_FIELDS, PLUGIN_STATUS, STARTUP_LIMITS, TABS, UPDATE_MODES,
  UPDATE_POLICIES, appendLogEntries, availabilityLabel, cpuPercentBetween, filterCatalog, filterGames,
  filterLobbies, formatBytes, formatClock, formatCount, formatDuration, formatVersion, isBusyLifecycle,
  isTerminalJob, jobProgress, lifecycleLabel, logLevelClass, logLevelTag, mergeJobs, noLimitOverrides,
  pluginStatusClass, pluginStatusLabel, ratePerSecond, tabFromHash, uploadGuard, validateLimits,
  versionAction, versionOptions, checkCodeEntry, blockedShare, WEBHOOK_EVENTS, webhookEventLabel,
  checkWebhook, webhookLastDelivery, mergeSamples, seriesRate, seriesValue, seriesCpuPercent,
  downsample, sparklinePath, formatDateTime, scheduleNote, hourOptionLabel,
  SIDEBAR_COLLAPSED_KEY, getStoredSidebarCollapsed, setStoredSidebarCollapsed,
} from '../admin/admin-core.js';

describe('tabFromHash', () => {
  it('selects the tab a fragment names', () => {
    expect(tabFromHash('#lobbies')).toBe('lobbies');
    expect(tabFromHash('#logs')).toBe('logs');
  });

  it('is forgiving about case and surrounding whitespace', () => {
    expect(tabFromHash('#  GAMES  ')).toBe('games');
  });

  it('falls back to the first tab for nothing, junk, or an unknown name', () => {
    // A bookmark from a future version, or a hand-typed fragment, must not blank the panel.
    for (const hash of ['', '#', undefined, null, '#nope', '#tab-lobbies']) {
      expect(tabFromHash(hash)).toBe(TABS[0]);
    }
  });
});

describe('formatBytes', () => {
  it('reports whole bytes below a kilobyte', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(999)).toBe('999 B');
  });

  it('uses 1024-based units, matching what du and Docker report', () => {
    expect(formatBytes(1024)).toBe('1.0 KB');
    expect(formatBytes(1024 * 1024)).toBe('1.0 MB');
    expect(formatBytes(1536 * 1024)).toBe('1.5 MB');
  });

  it('drops the decimal once the number is large enough not to need it', () => {
    expect(formatBytes(15 * 1024 * 1024)).toBe('15 MB');
  });

  it('returns a placeholder for values that are not a size', () => {
    for (const bad of [undefined, null, NaN, -1, 'abc']) expect(formatBytes(bad)).toBe('--');
  });
});

describe('formatDuration', () => {
  it('shows the two largest useful units so rows stay scannable', () => {
    expect(formatDuration(0)).toBe('0s');
    expect(formatDuration(45)).toBe('45s');
    expect(formatDuration(125)).toBe('2m 5s');
    expect(formatDuration(3 * 3600 + 4 * 60 + 5)).toBe('3h 4m');
    expect(formatDuration(3 * 86400 + 4 * 3600)).toBe('3d 4h');
  });

  it('rounds down to whole seconds', () => {
    expect(formatDuration(59.9)).toBe('59s');
  });

  it('returns a placeholder for values that are not a duration', () => {
    for (const bad of [undefined, null, NaN, -5]) expect(formatDuration(bad)).toBe('--');
  });
});

describe('formatCount and formatClock', () => {
  it('groups large counts', () => {
    expect(formatCount(1234567)).toBe('1,234,567');
    expect(formatCount('nope')).toBe('--');
  });

  it('renders a parseable timestamp and rejects junk', () => {
    expect(formatClock('2026-08-12T20:19:37.000Z')).not.toBe('--');
    expect(formatClock('not a date')).toBe('--');
  });

  it('renders a date as well as a time for something days away', () => {
    expect(formatDateTime('2026-08-16T03:00:00.000Z')).not.toBe('--');
    expect(formatDateTime('not a date')).toBe('--');
  });
});

describe('scheduleNote', () => {
  const base = {
    summary: 'weekly, Sundays at 03:00 UTC',
    nextRunUtc: '2026-08-16T03:00:00.000Z',
    enrolled: 2,
  };

  it('states the schedule, the next run and the enrolment', () => {
    const note = scheduleNote(base);

    expect(note).toContain('weekly, Sundays at 03:00 UTC');
    expect(note).toContain('(your time)');
    expect(note).toContain('2 game(s) enrolled');
  });

  it('says nothing is scheduled when checks are off', () => {
    expect(scheduleNote({ ...base, summary: 'never (scheduled checks are off)', nextRunUtc: null }))
      .toContain('No check is scheduled.');
  });

  it('warns when a schedule has nothing to act on', () => {
    // A pass with an empty enrolment makes no request at all, so the schedule alone does nothing —
    // an operator who set one and saw no activity would reasonably think it was broken.
    expect(scheduleNote({ ...base, enrolled: 0 })).toContain('No game is enrolled');
  });

  it('renders nothing at all when the marketplace is off', () => {
    expect(scheduleNote(null)).toBe('');
  });
});

describe('hourOptionLabel', () => {
  // Pinned against a fixed reference date so the assertions don't move with the calendar. The LOCAL half
  // is whatever zone the test host is in, so it is asserted structurally rather than by value.
  const reference = new Date(Date.UTC(2026, 7, 13, 12));

  it('always states the UTC hour it stores', () => {
    expect(hourOptionLabel(3, reference)).toContain('03:00 UTC');
    expect(hourOptionLabel(14, reference)).toContain('14:00 UTC');
    expect(hourOptionLabel(0, reference)).toContain('00:00 UTC');
  });

  it('adds the reader’s own clock beside it', () => {
    // The stored value is UTC and never moves; the label exists so nobody has to do the arithmetic.
    expect(hourOptionLabel(9, reference)).toMatch(/09:00 UTC \(.+ local(, (prev\.|next) day)?\)/);
  });

  it('marks an hour that lands on a different local day', () => {
    // Across all 24 hours, any zone but UTC pushes at least one of them onto a neighbouring date, and
    // "03:00 UTC → 10:00 PM" without that marker is quietly the wrong evening.
    const labels = Array.from({ length: 24 }, (_, h) => hourOptionLabel(h, reference));
    const offsetMinutes = reference.getTimezoneOffset();
    const shifted = labels.filter((l) => /prev\. day|next day/.test(l));
    if (offsetMinutes === 0) expect(shifted).toHaveLength(0);
    else expect(shifted.length).toBeGreaterThan(0);
  });

  it('does not report a day shift across a month boundary', () => {
    // 31 → 1 is one day, not minus thirty: comparing day-of-month naively inverts the marker on the
    // last day of a month, which is the one day nobody would test by hand.
    const label = hourOptionLabel(23, new Date(Date.UTC(2026, 7, 31, 12)));
    expect(label).not.toMatch(/prev\. day/);
  });
});

describe('ratePerSecond', () => {
  const at = (seconds) => new Date(Date.UTC(2026, 0, 1, 0, 0, seconds)).toISOString();

  it('divides the counter delta by the elapsed time', () => {
    expect(ratePerSecond({ value: 100, at: at(0) }, { value: 400, at: at(10) })).toBe(30);
  });

  it('has no answer without a previous sample', () => {
    // The first poll can only establish a baseline — reporting 0 would draw a false trough.
    expect(ratePerSecond(null, { value: 10, at: at(0) })).toBeNull();
    expect(ratePerSecond(undefined, undefined)).toBeNull();
  });

  it('has no answer when the clock did not advance', () => {
    expect(ratePerSecond({ value: 1, at: at(5) }, { value: 9, at: at(5) })).toBeNull();
  });

  it('has no answer when the counter went backwards', () => {
    // A counter that decreases means the server restarted. Treating it as a negative rate would draw a
    // nonsense spike at exactly the moment an operator is trying to understand the restart.
    expect(ratePerSecond({ value: 500, at: at(0) }, { value: 5, at: at(10) })).toBeNull();
  });
});

describe('cpuPercentBetween', () => {
  const at = (seconds) => new Date(Date.UTC(2026, 0, 1, 0, 0, seconds)).toISOString();

  it('expresses CPU seconds per wall second as a percentage of one core', () => {
    // 4 CPU-seconds over 10 wall seconds on 2 cores = 20% of the machine.
    expect(cpuPercentBetween({ value: 0, at: at(0) }, { value: 4, at: at(10) }, 2)).toBeCloseTo(20);
  });

  it('has no answer without two samples or a sane core count', () => {
    expect(cpuPercentBetween(null, { value: 4, at: at(10) }, 2)).toBeNull();
    expect(cpuPercentBetween({ value: 0, at: at(0) }, { value: 4, at: at(10) }, 0)).toBeNull();
    expect(cpuPercentBetween({ value: 0, at: at(0) }, { value: 4, at: at(10) }, undefined)).toBeNull();
  });
});

describe('filterLobbies', () => {
  const lobbies = [
    { code: 'AB12', gameId: 'tictactoe', gameName: 'Tic-Tac-Toe', status: 'waiting' },
    { code: 'CD34', gameId: 'word-rush', gameName: 'Word Rush', status: 'in-game' },
    { code: 'EF56', gameId: 'tictactoe', gameName: 'Tic-Tac-Toe', status: 'stale' },
  ];

  it('returns everything with no filters', () => {
    expect(filterLobbies(lobbies)).toHaveLength(3);
    expect(filterLobbies(lobbies, {})).toHaveLength(3);
  });

  it('matches a game by title or by id', () => {
    // The operator sees the title but ids are what appear in logs and URLs, so both work.
    expect(filterLobbies(lobbies, { game: 'tic-tac' }).map((l) => l.code)).toEqual(['AB12', 'EF56']);
    expect(filterLobbies(lobbies, { game: 'word-rush' }).map((l) => l.code)).toEqual(['CD34']);
  });

  it('matches a room code case-insensitively and partially', () => {
    expect(filterLobbies(lobbies, { code: 'cd' }).map((l) => l.code)).toEqual(['CD34']);
  });

  it('matches status exactly, so "in-game" never catches "stale"', () => {
    expect(filterLobbies(lobbies, { status: 'stale' }).map((l) => l.code)).toEqual(['EF56']);
    expect(filterLobbies(lobbies, { status: 'in-game' }).map((l) => l.code)).toEqual(['CD34']);
  });

  it('combines filters', () => {
    expect(filterLobbies(lobbies, { game: 'tictactoe', status: 'stale' }).map((l) => l.code)).toEqual(['EF56']);
  });

  it('survives a missing list', () => {
    expect(filterLobbies(undefined, { game: 'x' })).toEqual([]);
  });
});

describe('filterGames', () => {
  const games = [
    { id: 'tictactoe', name: 'Tic-Tac-Toe', availability: 'available' },
    { id: 'word-rush', name: 'Word Rush', availability: 'disabled' },
    { id: 'alpha-chain', name: 'Alpha Chain', availability: 'staged' },
  ];

  it('matches on name or id', () => {
    expect(filterGames(games, { q: 'alpha' }).map((g) => g.id)).toEqual(['alpha-chain']);
    expect(filterGames(games, { q: 'RUSH' }).map((g) => g.id)).toEqual(['word-rush']);
  });

  it('filters by availability', () => {
    expect(filterGames(games, { availability: 'staged' }).map((g) => g.id)).toEqual(['alpha-chain']);
  });

  it('returns everything with no filters', () => {
    expect(filterGames(games)).toHaveLength(3);
  });
});

describe('log helpers', () => {
  it('maps each level to a class, defaulting to info', () => {
    expect(logLevelClass('Fatal')).toBe('log-fatal');
    expect(logLevelClass('error')).toBe('log-error');
    expect(logLevelClass('Warning')).toBe('log-warning');
    expect(logLevelClass('Debug')).toBe('log-quiet');
    expect(logLevelClass('Verbose')).toBe('log-quiet');
    expect(logLevelClass('Information')).toBe('log-info');
    expect(logLevelClass(undefined)).toBe('log-info');
  });

  it('abbreviates the level to the three-letter tag the log file uses', () => {
    expect(logLevelTag('Information')).toBe('INF');
    expect(logLevelTag('Warning')).toBe('WAR');
    expect(logLevelTag(undefined)).toBe('---');
  });
});

describe('appendLogEntries', () => {
  const entry = (seq) => ({ seq, message: `event ${seq}` });

  it('appends new entries in sequence order', () => {
    expect(appendLogEntries([entry(1)], [entry(3), entry(2)]).map((e) => e.seq)).toEqual([1, 2, 3]);
  });

  it('de-duplicates by sequence', () => {
    // A filter change re-reads from cursor 0 while a poll may already have delivered the same entries;
    // without this, changing the filter duplicates every line still in the buffer.
    expect(appendLogEntries([entry(1), entry(2)], [entry(2), entry(3)]).map((e) => e.seq)).toEqual([1, 2, 3]);
  });

  it('keeps the stream bounded by dropping the oldest', () => {
    const existing = [1, 2, 3, 4].map(entry);
    expect(appendLogEntries(existing, [entry(5)], 3).map((e) => e.seq)).toEqual([3, 4, 5]);
  });

  it('survives missing arguments', () => {
    expect(appendLogEntries(undefined, undefined)).toEqual([]);
    expect(appendLogEntries(null, [entry(1)]).map((e) => e.seq)).toEqual([1]);
  });
});

describe('availability metadata', () => {
  it('offers exactly the three server-side states', () => {
    expect(AVAILABILITY.map((a) => a.value)).toEqual(['available', 'disabled', 'staged']);
  });

  it('explains what each state does, since the difference is not obvious', () => {
    for (const option of AVAILABILITY) expect(option.hint.length).toBeGreaterThan(10);
    // The one that most needs saying out loud: staged is visibility, not access control.
    expect(AVAILABILITY.find((a) => a.value === 'staged').hint).toMatch(/not access control/i);
  });

  it('labels a known state and passes through an unknown one', () => {
    expect(availabilityLabel('disabled')).toBe('Disabled');
    expect(availabilityLabel('STAGED')).toBe('Staged');
    expect(availabilityLabel('something-new')).toBe('something-new');
  });
});

describe('lifecycle', () => {
  it('renders nothing for the ordinary state', () => {
    // 'ready' is what almost every game is almost all the time; a badge saying "fine" is noise.
    expect(lifecycleLabel('ready')).toBe('');
    expect(isBusyLifecycle('ready')).toBe(false);
    expect(isBusyLifecycle(undefined)).toBe(false);
  });

  it('labels the two engine states and treats them as busy', () => {
    expect(lifecycleLabel('draining')).toBe('Draining');
    expect(lifecycleLabel('updating')).toBe('Updating');
    expect(isBusyLifecycle('draining')).toBe(true);
    expect(isBusyLifecycle('updating')).toBe(true);
  });

  it('is deliberately NOT part of the availability control', () => {
    // The availability select is a command — choosing an option POSTs it. Offering a value the server
    // would have to refuse is worse than not offering it, so these live in their own list.
    const availabilityValues = AVAILABILITY.map((a) => a.value);
    for (const state of LIFECYCLE.map((l) => l.value)) {
      expect(availabilityValues).not.toContain(state);
    }
  });
});

describe('plugin status metadata', () => {
  it('covers every server-side status plus the one the projection synthesizes', () => {
    // Pinned so a server-side enum addition fails loudly here rather than rendering as a bare camelCase
    // string in the UI.
    expect(PLUGIN_STATUS.map((s) => s.value)).toEqual([
      'notInstalled', 'upToDate', 'updateAvailable', 'installedAhead', 'installedVersionUnknown',
      'incompatible', 'unusable', 'installedOnly',
    ]);
  });

  it('explains each one, since several are near-identical at a glance', () => {
    for (const status of PLUGIN_STATUS) expect(status.hint.length).toBeGreaterThan(10);
  });

  it('labels a known status and passes an unknown one through', () => {
    expect(pluginStatusLabel('updateAvailable')).toBe('Update available');
    expect(pluginStatusLabel('somethingNew')).toBe('somethingNew');
    expect(pluginStatusClass('incompatible')).toBe('badge-danger');
    expect(pluginStatusClass('somethingNew')).toBe('badge-muted');
  });
});

describe('filterCatalog', () => {
  const entries = [
    { id: 'alpha', name: 'Alpha Chain', status: 'updateAvailable', sourceId: 'official', tags: ['party'] },
    { id: 'beta', name: 'Beta Blast', status: 'notInstalled', sourceId: 'community', tags: ['puzzle'] },
    { id: 'gamma', name: 'Gamma Quest', status: 'upToDate', sourceId: 'official', tags: [] },
    { id: 'delta', name: 'Delta Force', status: 'incompatible', sourceId: 'official', tags: [] },
    { id: 'epsilon', name: 'Epsilon', status: 'installedOnly', sourceId: '', tags: [] },
  ];

  it('matches on name, id or tag', () => {
    expect(filterCatalog(entries, { q: 'chain' }).map((e) => e.id)).toEqual(['alpha']);
    expect(filterCatalog(entries, { q: 'beta' }).map((e) => e.id)).toEqual(['beta']);
    // Tags are why an operator can find "that party game" without knowing its name.
    expect(filterCatalog(entries, { q: 'puzzle' }).map((e) => e.id)).toEqual(['beta']);
  });

  it('groups the several installed statuses under one filter', () => {
    expect(filterCatalog(entries, { status: 'installed' }).map((e) => e.id))
      .toEqual(['alpha', 'gamma', 'epsilon']);
  });

  it('groups incompatible and unusable under "problem"', () => {
    expect(filterCatalog(entries, { status: 'problem' }).map((e) => e.id)).toEqual(['delta']);
  });

  it('filters by an exact status when one is named', () => {
    expect(filterCatalog(entries, { status: 'notInstalled' }).map((e) => e.id)).toEqual(['beta']);
  });

  it('filters by source and combines with the others', () => {
    expect(filterCatalog(entries, { source: 'community' }).map((e) => e.id)).toEqual(['beta']);
    expect(filterCatalog(entries, { source: 'official', status: 'installed' }).map((e) => e.id))
      .toEqual(['alpha', 'gamma']);
  });

  it('survives a missing list', () => {
    expect(filterCatalog(undefined, { q: 'x' })).toEqual([]);
  });
});

describe('mergeJobs', () => {
  const job = (id, sequence, extra = {}) => ({ jobId: id, sequence, ...extra });

  it('replaces by jobId rather than appending', () => {
    // The one behavioural difference from appendLogEntries: a job is a single thing that changes, so its
    // row must update in place instead of stacking a new row per poll.
    const merged = mergeJobs([job('a', 1, { status: 'downloading' })], [job('a', 2, { status: 'applying' })]);

    expect(merged).toHaveLength(1);
    expect(merged[0].status).toBe('applying');
  });

  it('ignores a stale lower sequence for a job already held', () => {
    // Two polls can overlap. Letting an older reply win would make a finished job flicker back to
    // "downloading".
    const merged = mergeJobs([job('a', 5, { status: 'succeeded' })], [job('a', 2, { status: 'downloading' })]);

    expect(merged[0].status).toBe('succeeded');
  });

  it('orders newest change first', () => {
    expect(mergeJobs([job('a', 1)], [job('b', 3), job('c', 2)]).map((j) => j.jobId))
      .toEqual(['b', 'c', 'a']);
  });

  it('is bounded', () => {
    const many = Array.from({ length: 80 }, (_, i) => job(`j${i}`, i));
    expect(mergeJobs([], many, 50)).toHaveLength(50);
  });

  it('survives missing arguments and rows with no id', () => {
    expect(mergeJobs(undefined, undefined)).toEqual([]);
    expect(mergeJobs(null, [job('a', 1), { sequence: 2 }, null])).toHaveLength(1);
  });
});

describe('jobProgress', () => {
  it('computes a percentage when the total is known', () => {
    expect(jobProgress({ bytesDone: 50, bytesTotal: 200 }).percent).toBe(25);
    expect(jobProgress({ bytesDone: 50, bytesTotal: 200 }).label).toBe('50 B / 200 B');
  });

  it('returns null, never zero, when the total is unknown', () => {
    // A confident "0%" on a transfer that is actually moving is a claim we cannot make, and it reads as
    // a stall. Null tells the caller to render indeterminate instead.
    expect(jobProgress({ bytesDone: 0, bytesTotal: 0 }).percent).toBeNull();
    expect(jobProgress({}).percent).toBeNull();
    expect(jobProgress(null).percent).toBeNull();
  });

  it('clamps a total the server under-reported', () => {
    expect(jobProgress({ bytesDone: 300, bytesTotal: 200 }).percent).toBe(100);
  });
});

describe('isTerminalJob', () => {
  it('recognises the three states a job stops in', () => {
    for (const status of ['succeeded', 'failed', 'cancelled']) {
      expect(isTerminalJob(status)).toBe(true);
    }
  });

  it('treats everything still moving as not terminal', () => {
    for (const status of ['queued', 'downloading', 'verifying', 'waitingForLobbies', 'applying']) {
      expect(isTerminalJob(status)).toBe(false);
    }
    expect(isTerminalJob(undefined)).toBe(false);
  });
});

describe('versionOptions', () => {
  it('offers available, then installed, then each backup', () => {
    const options = versionOptions({
      installed: true,
      installedVersion: '1.2.0',
      availableVersion: '1.3.0',
      backups: [{ version: '1.1.0' }, { version: '1.0.0' }],
    });

    expect(options).toEqual([
      { version: '1.3.0', kind: 'available' },
      { version: '1.2.0', kind: 'installed' },
      { version: '1.1.0', kind: 'backup' },
      { version: '1.0.0', kind: 'backup' },
    ]);
  });

  it('does not repeat a version that is both offered and installed', () => {
    const options = versionOptions({
      installed: true, installedVersion: '1.0.0', availableVersion: '1.0.0', backups: [],
    });

    expect(options.map((o) => o.version)).toEqual(['1.0.0', '1.0.0']);
    // Distinct kinds, so "reinstall" and "update" stay distinguishable — but the same version is never
    // listed twice under one kind.
    expect(options.map((o) => o.kind)).toEqual(['available', 'installed']);
  });

  it('handles a game with nothing to offer', () => {
    expect(versionOptions({ installed: false, backups: [] })).toEqual([]);
    expect(versionOptions(null)).toEqual([]);
  });
});

describe('versionAction', () => {
  const installed = {
    installed: true, installedVersion: '1.2.0', availableVersion: '1.3.0',
    backups: [{ version: '1.1.0' }], status: 'updateAvailable',
  };

  it('installs a game that is not installed', () => {
    const action = versionAction({ installed: false, availableVersion: '1.0.0', backups: [] }, '1.0.0');
    expect(action).toMatchObject({ kind: 'install', label: 'Install', danger: false });
  });

  it('updates when a newer version is selected', () => {
    expect(versionAction(installed, '1.3.0')).toMatchObject({ kind: 'update', label: 'Update' });
  });

  it('reinstalls when the running version is selected', () => {
    expect(versionAction(installed, '1.2.0')).toMatchObject({ kind: 'reinstall', label: 'Reinstall' });
  });

  it('rolls back to a retained version, and says so dangerously', () => {
    const action = versionAction(installed, '1.1.0');
    expect(action.kind).toBe('rollback');
    expect(action.label).toBe('Roll back');
    // The one action here an operator can regret: it replaces what is running with older bytes.
    expect(action.danger).toBe(true);
  });

  it('refuses an incompatible entry and explains why', () => {
    const action = versionAction(
      { ...installed, status: 'incompatible', reason: 'needs server 2.0.0' }, '1.3.0');

    expect(action.kind).toBe('none');
    expect(action.blockedReason).toBe('needs server 2.0.0');
  });

  it('refuses when the deployment cannot install at all', () => {
    // Passed IN, not read off the entry: the block reason is a property of the response (the managed root
    // is unwritable for every game or none of them), and no such field is ever sent on an entry. Reading
    // it from the entry made this branch unreachable in production while a test like this still passed.
    const action = versionAction(installed, '1.3.0', 'the managed folder is not writable');

    expect(action.kind).toBe('none');
    expect(action.blockedReason).toMatch(/not writable/);
  });

  it('ignores a block reason on the entry, which the server never sends there', () => {
    const action = versionAction(
      { ...installed, installBlockedReason: 'the managed folder is not writable' }, '1.3.0');

    expect(action.kind).toBe('update');
    expect(action.blockedReason).toBe(null);
  });

  it('refuses to reinstall a game no source offers, instead of posting a request that must 404', () => {
    // An upload, or a withdrawn catalog entry: the install route resolves the id out of the fetched
    // catalogs, so "Reinstall" here could only ever fail.
    const action = versionAction(
      { ...installed, status: 'installedOnly', availableVersion: null }, '1.2.0');

    expect(action.kind).toBe('none');
    expect(action.blockedReason).toMatch(/upload/i);
  });
});

describe('update modes and policies', () => {
  it('offers exactly the three apply modes, and warns that force closes games', () => {
    expect(UPDATE_MODES.map((m) => m.value)).toEqual(['drain', 'auto', 'force']);
    expect(UPDATE_MODES.find((m) => m.value === 'force').hint).toMatch(/close/i);
  });

  it('defaults the list to the least disruptive mode that still happens', () => {
    // Drain first, because auto silently does nothing when a game is busy and force kills sessions.
    expect(UPDATE_MODES[0].value).toBe('drain');
  });

  it('offers exactly the four update policies, starting at manual', () => {
    expect(UPDATE_POLICIES.map((p) => p.value)).toEqual(['manual', 'auto', 'drain', 'force']);
    expect(UPDATE_POLICIES[0].hint).toMatch(/never/i);
  });
});

describe('formatVersion', () => {
  it('prefixes a bare version and leaves an existing v alone', () => {
    expect(formatVersion('1.2.3')).toBe('v1.2.3');
    expect(formatVersion('v1.2.3')).toBe('v1.2.3');
  });

  it('dashes when there is no version, which is normal for a hand-made game', () => {
    expect(formatVersion(null)).toBe('--');
    expect(formatVersion('')).toBe('--');
  });
});

describe('uploadGuard', () => {
  const file = (name, size) => ({ name, size });

  it('accepts a .kbg regardless of case', () => {
    expect(uploadGuard(file('demo.kbg', 1000), { maxBytes: 5000 }).ok).toBe(true);
    expect(uploadGuard(file('DEMO.KBG', 1000), { maxBytes: 5000 }).ok).toBe(true);
  });

  it('rejects a plain zip and points at the packer', () => {
    const result = uploadGuard(file('game.zip', 1000), { maxBytes: 5000 });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/knockbox-pack/);
  });

  it('rejects an empty file and a missing one', () => {
    expect(uploadGuard(file('demo.kbg', 0)).ok).toBe(false);
    expect(uploadGuard(null).ok).toBe(false);
  });

  it('rejects one over the server-reported cap', () => {
    // Advisory only — the server enforces while streaming. This exists so a ten-minute upload does not
    // end in a 413, which is why the limit comes FROM the server rather than being hard-coded here.
    const result = uploadGuard(file('demo.kbg', 9000), { maxBytes: 5000 });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/over the/);
  });

  it('does not enforce a cap it was not given', () => {
    expect(uploadGuard(file('demo.kbg', 9_000_000_000)).ok).toBe(true);
  });
});

describe('platform limit fields', () => {
  it('covers exactly the eight limits the server lets an operator change', () => {
    // Pinned against AdminLimitValues / OperatorLimits. A knob added on one side only shows up here,
    // rather than as a field that silently never saves.
    expect(LIMIT_FIELDS.map((f) => f.key).sort()).toEqual([
      'controlMessagesBurst', 'controlMessagesPerSecond', 'gameMessagesBurst', 'gameMessagesPerSecond',
      'lobbyCreatesPerMinute', 'maxConnectionsPerIp', 'maxLobbies', 'maxLobbiesPerGame',
    ]);
    for (const field of LIMIT_FIELDS) {
      expect(field.label).toBeTruthy();
      expect(field.hint).toBeTruthy();
    }
  });

  it('lists the startup-only limits, which are deliberately NOT editable', () => {
    // Two are startup-derived (the reaper's interval comes from the grace window); two bound PBKDF2 CPU
    // for an unauthenticated caller, and a lock that opens from inside the room is not a lock.
    expect(STARTUP_LIMITS.map((f) => f.key)).toEqual([
      'handshakeTimeoutSeconds', 'disconnectGraceSeconds',
      'adminLoginAttemptsPerMinute', 'adminLoginAttemptsPerMinuteGlobal',
    ]);
    // And none of them is also editable — that would be two answers to one question.
    const editable = new Set(LIMIT_FIELDS.map((f) => f.key));
    for (const field of STARTUP_LIMITS) expect(editable.has(field.key)).toBe(false);
  });
});

describe('validateLimits', () => {
  const blank = () => Object.fromEntries(LIMIT_FIELDS.map((f) => [f.key, '']));

  it('reads a blank field as null, which is how an override is cleared', () => {
    const result = validateLimits(blank());
    expect(result.ok).toBe(true);
    for (const field of LIMIT_FIELDS) expect(result.values[field.key]).toBeNull();
    expect(noLimitOverrides(result.values)).toBe(true);
  });

  it('keeps zero as zero, because zero disables a limit', () => {
    const result = validateLimits({ ...blank(), maxLobbies: '0' });
    expect(result.values.maxLobbies).toBe(0);
    // Zero is an override — "no limit at all" is a decision, not the absence of one.
    expect(noLimitOverrides(result.values)).toBe(false);
  });

  it('accepts a fractional rate but not a fractional count', () => {
    expect(validateLimits({ ...blank(), gameMessagesPerSecond: '2.5' }).ok).toBe(true);
    const result = validateLimits({ ...blank(), maxConnectionsPerIp: '2.5' });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/whole number/);
  });

  it('rejects negatives and junk, naming the field', () => {
    expect(validateLimits({ ...blank(), maxLobbies: '-1' }).error).toMatch(/Max lobbies \(platform\)/);
    expect(validateLimits({ ...blank(), maxLobbies: 'lots' }).ok).toBe(false);
  });

  it('rejects a burst below one against a live rate — a lockout, not a limit', () => {
    const result = validateLimits({ ...blank(), controlMessagesPerSecond: '5', controlMessagesBurst: '0' });
    expect(result.ok).toBe(false);
    expect(result.error).toMatch(/at least 1/);

    // Turning the limit off entirely stays legitimate.
    expect(validateLimits({ ...blank(), controlMessagesPerSecond: '0', controlMessagesBurst: '0' }).ok).toBe(true);
  });

  it('leaves a burst-only edit to the server, which knows the configured rate', () => {
    // Only the server can pair this with the configured rate, so the client must not guess either way.
    expect(validateLimits({ ...blank(), controlMessagesBurst: '0' }).ok).toBe(true);
  });
});

describe('checkCodeEntry', () => {
  it('normalises to upper case, because the join side does too', () => {
    expect(checkCodeEntry('xq').value).toBe('XQ');
    expect(checkCodeEntry('  q7* ', { pattern: true }).value).toBe('Q7*');
  });

  it('allows wildcards only in a pattern', () => {
    expect(checkCodeEntry('Q7*', { pattern: true }).ok).toBe(true);
    const word = checkCodeEntry('Q7*');
    expect(word.ok).toBe(false);
    expect(word.error).toMatch(/pattern field/);
  });

  it('rejects anything longer than could match a code', () => {
    expect(checkCodeEntry('ABCD').ok).toBe(true);
    expect(checkCodeEntry('ABCDE').ok).toBe(false);
    // A pattern may be longer than the code, because its wildcards are characters too.
    expect(checkCodeEntry('A*B*C?', { pattern: true }).ok).toBe(true);
  });

  it('rejects blanks and punctuation', () => {
    for (const bad of ['', '   ', null, undefined, 'A-B', 'A B']) expect(checkCodeEntry(bad).ok).toBe(false);
  });

  it('accepts but flags an entry the alphabet can never produce', () => {
    // O/0/I/1 are left out of the code alphabet as too easily misread aloud. Blocking a word containing
    // one is legal and pointless, so it is reported rather than refused.
    const unreachable = checkCodeEntry('XO');
    expect(unreachable.ok).toBe(true);
    expect(unreachable.unreachable).toBe(true);
    expect(checkCodeEntry('XQ').unreachable).toBe(false);
    expect(checkCodeEntry('A*', { pattern: true }).unreachable).toBe(false);
  });
});

describe('blockedShare', () => {
  it('expresses blocked codes as a percentage of the space', () => {
    expect(blockedShare(1024, 1_048_576)).toBeCloseTo(0.0977, 3);
    expect(blockedShare(524_288, 1_048_576)).toBe(50);
  });

  it('has no answer without both numbers', () => {
    expect(blockedShare(null, 1000)).toBeNull();
    expect(blockedShare(10, 0)).toBeNull();
    expect(blockedShare(10, undefined)).toBeNull();
  });
});

describe('webhook helpers', () => {
  it('covers exactly the events the server defines', () => {
    // Pinned against WebhookEvent. A new event kind added server-side shows up here as a failing test
    // rather than as a checkbox nobody added.
    expect(WEBHOOK_EVENTS.map((e) => e.value)).toEqual([
      'logError', 'updateApplied', 'updateFailed', 'maintenanceChanged', 'resourceThreshold',
    ]);
    for (const event of WEBHOOK_EVENTS) expect(event.hint).toBeTruthy();
    expect(webhookEventLabel('logError')).toBe('Errors');
    expect(webhookEventLabel('somethingNew')).toBe('somethingNew'); // unknown passes through
  });

  it('accepts https anywhere and http only on loopback', () => {
    expect(checkWebhook({ id: 'ops', url: 'https://hooks.slack.com/services/x' }).ok).toBe(true);
    expect(checkWebhook({ id: 'ops', url: 'http://127.0.0.1:9099/hook' }).ok).toBe(true);
    expect(checkWebhook({ id: 'ops', url: 'http://localhost/hook' }).ok).toBe(true);
    // Mirrors the server's IsAllowedUrl — a URL that passes here must be one the sender will accept.
    expect(checkWebhook({ id: 'ops', url: 'http://example.com/hook' }).ok).toBe(false);
    expect(checkWebhook({ id: 'ops', url: 'ftp://example.com/hook' }).ok).toBe(false);
    expect(checkWebhook({ id: 'ops', url: 'not a url' }).ok).toBe(false);
  });

  it('rejects an id that could not be a route value', () => {
    expect(checkWebhook({ id: 'my-ops_2', url: 'https://e.com/h' }).ok).toBe(true);
    for (const bad of ['', '   ', 'has space', 'slash/es', 'x'.repeat(33)]) {
      expect(checkWebhook({ id: bad, url: 'https://e.com/h' }).ok).toBe(false);
    }
  });

  it('trims what it hands back, so the request carries the cleaned values', () => {
    const result = checkWebhook({ id: '  ops  ', url: '  https://e.com/h  ' });
    expect(result.id).toBe('ops');
    expect(result.url).toBe('https://e.com/h');
  });

  it('distinguishes a bad status from never getting one', () => {
    expect(webhookLastDelivery({ lastAt: 'x', lastOk: true, lastStatus: 204 })).toBe('OK (204)');
    expect(webhookLastDelivery({ lastAt: 'x', lastOk: false, lastStatus: 404 })).toBe('Failed (404)');
    // No status means DNS/TLS/timeout, which reads very differently from a 404.
    expect(webhookLastDelivery({ lastAt: 'x', lastOk: false, lastStatus: null, lastError: 'No such host.' }))
      .toContain('No response');
    expect(webhookLastDelivery({})).toBeNull();
  });
});

describe('metric history helpers', () => {
  const at = (seconds) => new Date(Date.UTC(2026, 7, 13, 12, 0, seconds)).toISOString();
  const sample = (seq, seconds, fields = {}) => ({ sequence: seq, at: at(seconds), ...fields });

  it('merges cursor-polled samples by sequence, dropping duplicates', () => {
    const held = [sample(1, 0), sample(2, 15)];
    const merged = mergeSamples(held, [sample(2, 15), sample(3, 30)]);
    expect(merged.map((s) => s.sequence)).toEqual([1, 2, 3]);
  });

  it('keeps only the newest `limit` samples', () => {
    const many = Array.from({ length: 300 }, (_, i) => sample(i + 1, i * 15));
    const merged = mergeSamples([], many, 240);
    expect(merged.length).toBe(240);
    expect(merged[0].sequence).toBe(61);
  });

  it('turns a cumulative counter into per-second rates', () => {
    const samples = [
      sample(1, 0, { framesOut: 0 }),
      sample(2, 10, { framesOut: 100 }),
      sample(3, 20, { framesOut: 300 }),
    ];
    expect(seriesRate(samples, 'framesOut').map((p) => p.value)).toEqual([10, 20]);
  });

  it('omits a pair whose counter went backwards instead of plotting a trough', () => {
    // A counter that decreases means the server restarted. A false zero at exactly that moment is worse
    // than a gap, because a gap looks like what it is.
    const samples = [
      sample(1, 0, { framesOut: 500 }),
      sample(2, 10, { framesOut: 5 }),
      sample(3, 20, { framesOut: 25 }),
    ];
    expect(seriesRate(samples, 'framesOut').map((p) => p.value)).toEqual([2]);
  });

  it('reads a gauge series straight through, skipping junk', () => {
    const samples = [sample(1, 0, { players: 3 }), sample(2, 15, {}), sample(3, 30, { players: 7 })];
    expect(seriesValue(samples, 'players').map((p) => p.value)).toEqual([3, 7]);
  });

  it('expresses CPU as a percentage of one core-equivalent', () => {
    const samples = [sample(1, 0, { cpuSeconds: 0 }), sample(2, 10, { cpuSeconds: 4 })];
    // 4 CPU-seconds over 10 wall seconds on 2 cores = 20%, matching cpuPercentBetween's convention.
    expect(seriesCpuPercent(samples, 2)[0].value).toBeCloseTo(20);
  });

  it('downsamples by averaging buckets, so a spike survives', () => {
    const points = Array.from({ length: 100 }, (_, i) => ({ at: at(i), value: i === 50 ? 1000 : 1 }));
    const reduced = downsample(points, 10);
    expect(reduced.length).toBe(10);
    // Taking every Nth point would let sampling luck decide whether the spike appears at all.
    expect(Math.max(...reduced.map((p) => p.value))).toBeGreaterThan(50);
  });

  it('leaves a short series alone', () => {
    const points = [{ at: at(0), value: 1 }, { at: at(1), value: 2 }];
    expect(downsample(points, 10)).toBe(points);
  });

  it('builds a path with one point per sample, and refuses to draw a single point', () => {
    const path = sparklinePath([{ at: at(0), value: 1 }, { at: at(1), value: 3 }, { at: at(2), value: 2 }],
      { width: 100, height: 20 });
    expect(path.path.startsWith('M')).toBe(true);
    expect(path.path.match(/L/g).length).toBe(2);
    expect(path.max).toBe(3);
    expect(path.last).toBe(2);

    // Fewer than two points is a real state early on — the caller shows "collecting…" rather than an empty
    // box that reads as broken.
    expect(sparklinePath([{ at: at(0), value: 5 }]).path).toBeNull();
    expect(sparklinePath([]).path).toBeNull();
  });

  it('does not divide by zero for a flat series', () => {
    const flat = Array.from({ length: 5 }, (_, i) => ({ at: at(i), value: 7 }));
    const result = sparklinePath(flat, { width: 100, height: 20 });
    expect(result.path).toContain('M');
    expect(result.path).not.toContain('NaN');
  });
});

describe('sidebar collapsed storage', () => {
  it('reads false when storage is empty or null', () => {
    const fakeStorage = { getItem: () => null, setItem: () => {}, removeItem: () => {} };
    expect(getStoredSidebarCollapsed(fakeStorage)).toBe(false);
    expect(getStoredSidebarCollapsed(null)).toBe(false);
  });

  it('reads true only when the key is strictly "true"', () => {
    const map = new Map();
    const fakeStorage = {
      getItem: (k) => map.get(k) ?? null,
      setItem: (k, v) => map.set(k, String(v)),
      removeItem: (k) => map.delete(k),
    };

    map.set(SIDEBAR_COLLAPSED_KEY, 'true');
    expect(getStoredSidebarCollapsed(fakeStorage)).toBe(true);

    map.set(SIDEBAR_COLLAPSED_KEY, 'false');
    expect(getStoredSidebarCollapsed(fakeStorage)).toBe(false);

    map.set(SIDEBAR_COLLAPSED_KEY, '1');
    expect(getStoredSidebarCollapsed(fakeStorage)).toBe(false);
  });

  it('writes "true" to storage when collapsed is true', () => {
    const map = new Map();
    const fakeStorage = {
      getItem: (k) => map.get(k) ?? null,
      setItem: (k, v) => map.set(k, String(v)),
      removeItem: (k) => map.delete(k),
    };

    setStoredSidebarCollapsed(true, fakeStorage);
    expect(map.get(SIDEBAR_COLLAPSED_KEY)).toBe('true');
  });

  it('removes the key from storage when collapsed is false', () => {
    const map = new Map([[SIDEBAR_COLLAPSED_KEY, 'true']]);
    const fakeStorage = {
      getItem: (k) => map.get(k) ?? null,
      setItem: (k, v) => map.set(k, String(v)),
      removeItem: (k) => map.delete(k),
    };

    setStoredSidebarCollapsed(false, fakeStorage);
    expect(map.has(SIDEBAR_COLLAPSED_KEY)).toBe(false);
  });

  it('handles throwing storage gracefully without uncaught errors', () => {
    const brokenStorage = {
      getItem: () => { throw new Error('SecurityError'); },
      setItem: () => { throw new Error('QuotaExceededError'); },
      removeItem: () => { throw new Error('SecurityError'); },
    };

    expect(getStoredSidebarCollapsed(brokenStorage)).toBe(false);
    expect(() => setStoredSidebarCollapsed(true, brokenStorage)).not.toThrow();
    expect(() => setStoredSidebarCollapsed(false, brokenStorage)).not.toThrow();
  });
});

