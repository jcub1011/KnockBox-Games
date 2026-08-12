// Pure logic behind the admin portal: formatting, filtering, tab routing, and the rate arithmetic the
// dashboard does on top of the server's cumulative counters. Node environment (no DOM) — same split as
// kb-core.test.js, and the reason admin-core.js exists as a module of its own.
import { describe, it, expect } from 'vitest';
import {
  AVAILABILITY, TABS, appendLogEntries, availabilityLabel, cpuPercentBetween, filterGames, filterLobbies,
  formatBytes, formatClock, formatCount, formatDuration, logLevelClass, logLevelTag, ratePerSecond,
  tabFromHash,
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
