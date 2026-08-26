// @vitest-environment jsdom
//
// The Platform tab. Three things here are design decisions rather than incidental behaviour, and each has
// a test that says so: the tab does not poll (it is a form, and a timer would overwrite what the operator
// is typing), a blank field means "not overridden" rather than zero, and the client refuses the one
// combination that would lock every player out instead of making the server say no.
//
// Same shape as admin-marketplace.test.js — reset modules, inject the REAL admin/index.html, stub fetch,
// import fresh, and stop the poll timer afterwards.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { installFakeFetch, loadAdminDom, tick } from './helpers.js';
import { LIMIT_FIELDS } from '../admin/admin-core.js';

const el = (id) => document.getElementById(id);
const limitInput = (key) => document.querySelector(`#limits-fields input[data-limit-key="${key}"]`);

let admin;
let fake;

async function importAdmin() {
  admin = await import('../admin/admin.js');
  return admin;
}

const DEFAULTS = {
  gameMessagesPerSecond: 30, gameMessagesBurst: 60,
  controlMessagesPerSecond: 5, controlMessagesBurst: 10,
  lobbyCreatesPerMinute: 10, maxConnectionsPerIp: 32,
  maxLobbies: 0, maxLobbiesPerGame: 0,
};

function limits(overrides = {}, effective = {}) {
  return {
    defaults: DEFAULTS,
    effective: { ...DEFAULTS, ...effective },
    overridden: Object.keys(overrides),
    handshakeTimeoutSeconds: 10, disconnectGraceSeconds: 60,
    adminLoginAttemptsPerMinute: 10, adminLoginAttemptsPerMinuteGlobal: 60,
    activeLobbies: 3, connectedPlayers: 7,
  };
}

function codes({ words = [], patterns = [], unreachable = [], blocked = 0 } = {}) {
  return {
    words, patterns, unreachable, blocked, codeSpace: 1_048_576,
    maxEntries: 32, maxBlockedPercent: 50, alphabet: 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789', codeLength: 4,
  };
}

function announcement(live = null) {
  return {
    id: live?.id ?? null, text: live?.text ?? null, severity: live?.severity ?? null,
    gameId: live?.gameId ?? null, postedAt: live?.postedAt ?? null,
    connectedPlayers: 3, maxLength: 200,
    games: [{ id: 'ttt', name: 'Tic-Tac-Toe' }, { id: 'word-rush', name: 'Word Rush' }],
  };
}

function webhooks({ endpoints = [], enabled = true } = {}) {
  return {
    enabled, endpoints,
    knownEvents: ['logError', 'updateApplied', 'updateFailed', 'maintenanceChanged', 'resourceThreshold'],
    maxEndpoints: 8, delivered: 4, failed: 1, dropped: 0, suppressed: 0,
    timeoutSeconds: 10, errorsPerMinute: 6,
  };
}

function schedule(over = {}) {
  return {
    cadence: 'daily', dayOfWeek: 'sunday', hourUtc: 3, overridden: false,
    summary: 'daily at 03:00 UTC', nextRunUtc: '2026-08-14T03:00:00.0000000+00:00', enrolled: 2,
    ...over,
  };
}

function routes(overrides = {}) {
  return {
    'GET /admin/api/auth/status': { body: { configured: true, authenticated: true } },
    'GET /admin/api/limits': { body: limits() },
    'GET /admin/api/updates/schedule': { body: schedule() },
    '* /admin/api/updates/schedule': { body: { success: true, detail: 'Update checks run hourly, on the hour.' } },
    '* /admin/api/limits': { body: { success: true, detail: 'In force now.' } },
    'GET /admin/api/room-codes': { body: codes() },
    '* /admin/api/room-codes': { body: { success: true, detail: 'No codes are blocked.' } },
    'GET /admin/api/announcement': { body: announcement() },
    'GET /admin/api/webhooks': { body: webhooks() },
    '* /admin/api/webhooks': { body: { success: true, detail: 'Saved.' } },
    '* /admin/api/webhooks/ops/delete': { body: { success: true, detail: 'Removed.' } },
    '* /admin/api/webhooks/ops/test': { body: { success: true, detail: 'Delivered (204).' } },
    '* /admin/api/announcement': { body: { success: true, affected: 3, detail: 'Posted to 3 connected player(s).' } },
    '* /admin/api/announcement/delete': { body: { success: true, detail: 'Cleared for 3 connected player(s).' } },
    ...overrides,
  };
}

async function openPlatform(overrides) {
  fake = installFakeFetch(routes(overrides));
  await importAdmin();
  admin.bootstrap();
  await tick();
  await tick();
  admin.selectTab('platform');
  await tick();
  await tick();
  return fake;
}

beforeEach(() => {
  vi.resetModules();
  loadAdminDom();
  // replaceState, not `location.hash = ...`: see the note in admin-marketplace.test.js.
  history.replaceState(null, '', window.location.pathname);
});

afterEach(() => {
  admin?.stopPolling();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('limits form', () => {
  it('renders one row per editable limit, with its default as the placeholder', async () => {
    await openPlatform();

    const rows = document.querySelectorAll('#limits-fields input[data-limit-key]');
    expect(rows.length).toBe(LIMIT_FIELDS.length);
    // Empty box + default placeholder is the whole UI for "not overridden".
    expect(limitInput('maxLobbies').value).toBe('');
    expect(limitInput('controlMessagesPerSecond').placeholder).toBe('Default: 5');
    expect(el('limits-badge').hidden).toBe(true);
    expect(el('limits-reset').disabled).toBe(true);
  });

  it('shows an overridden limit as its value, badges it, and enables revert', async () => {
    await openPlatform({
      'GET /admin/api/limits': { body: limits({ maxLobbies: 40 }, { maxLobbies: 40 }) },
    });

    expect(limitInput('maxLobbies').value).toBe('40');
    expect(el('limits-badge').hidden).toBe(false);
    expect(el('limits-reset').disabled).toBe(false);
    expect(el('limits-note').textContent).toContain('1 of 8');
  });

  it('reports the startup-only limits read-only rather than hiding them', async () => {
    await openPlatform();

    // An operator who looks for "reconnect grace" and doesn't find it concludes the portal is incomplete.
    const text = el('limits-startup-body').textContent;
    expect(text).toContain('60');
    expect(text).toContain('Reconnect grace (s)');
  });

  it('does not poll: the tab is a form, not a view', async () => {
    await openPlatform();
    // Fake timers AFTER the open: tick() is a real setTimeout, so installing them first deadlocks.
    vi.useFakeTimers();
    const before = fake.calls.length;

    await vi.advanceTimersByTimeAsync(30_000);

    // Any request here would have re-rendered the form under the operator's cursor.
    expect(fake.calls.length).toBe(before);
  });

  it('sends a blank field as null, not as zero', async () => {
    await openPlatform();
    limitInput('controlMessagesPerSecond').value = '2';

    el('limits-save').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/limits');
    expect(post.body.controlMessagesPerSecond).toBe(2);
    // Zero would mean "disable this limit" — a completely different instruction from "leave it alone".
    expect(post.body.maxLobbies).toBeNull();
    expect(post.body.gameMessagesBurst).toBeNull();
  });

  it('refuses a burst below one against a live rate without asking the server', async () => {
    await openPlatform();
    limitInput('controlMessagesPerSecond').value = '5';
    limitInput('controlMessagesBurst').value = '0';

    el('limits-save').click();
    await tick();

    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
    expect(el('toast-host').textContent).toContain('at least 1');
  });

  it('refuses a fractional connection cap, and a negative anything', async () => {
    await openPlatform();
    limitInput('maxConnectionsPerIp').value = '2.5';
    el('limits-save').click();
    await tick();
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
    expect(el('toast-host').textContent).toContain('whole number');

    limitInput('maxConnectionsPerIp').value = '-1';
    el('limits-save').click();
    await tick();
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('confirms a capping change, naming what is running, and cancelling sends nothing', async () => {
    await openPlatform();
    limitInput('maxLobbies').value = '1';

    el('limits-save').click();
    await tick();
    expect(el('confirm-body').textContent).toContain('3 lobbies');

    el('confirm-cancel').click();
    await tick();
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('reverting sends an explicit null for every field', async () => {
    await openPlatform({
      'GET /admin/api/limits': { body: limits({ maxLobbies: 40 }, { maxLobbies: 40 }) },
    });

    el('limits-reset').click();
    await tick();
    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/limits');
    // A full replacement, not a patch: an omitted key and a null key would otherwise be the same bytes.
    for (const field of LIMIT_FIELDS) expect(post.body[field.key]).toBeNull();
  });

  it('surfaces a server refusal rather than re-deciding it client-side', async () => {
    await openPlatform({
      '* /admin/api/limits': { status: 400, body: { success: false, error: 'maxLobbies must be between 0 and 1000000.' } },
    });
    limitInput('gameMessagesPerSecond').value = '15';

    el('limits-save').click();
    await tick();
    await tick();

    expect(el('toast-host').textContent).toContain('maxLobbies must be between');
  });

  it('keeps a field the operator is editing when the panel re-renders', async () => {
    await openPlatform();
    const input = limitInput('maxLobbies');
    input.value = '77';
    input.focus();

    el('limits-refresh').click();
    await tick();
    await tick();

    // The server says "not overridden" for this field; the operator says 77. The cursor wins.
    expect(limitInput('maxLobbies').value).toBe('77');
  });
});

describe('banned room codes', () => {
  const chips = (id) => [...el(id).querySelectorAll('.member-chip')].map((c) => c.textContent.replace('×', ''));

  it('renders the saved blocklist as removable chips with what it costs', async () => {
    await openPlatform({
      'GET /admin/api/room-codes': { body: codes({ words: ['XQ'], patterns: ['Q7*'], blocked: 33_728 }) },
    });

    expect(chips('code-words')).toEqual(['XQ']);
    expect(chips('code-patterns')).toEqual(['Q7*']);
    // The share is the number that stops an operator adding one pattern too many.
    expect(el('codes-note').textContent).toContain('3.2%');
    expect(el('codes-badge').textContent).toBe('2 / 32');
  });

  it('adds an entry locally and only posts when saved', async () => {
    await openPlatform();
    el('code-word').value = 'xq';
    el('code-word-add').click();
    await tick();

    // Upper-cased on the way in, because the join side upper-cases too.
    expect(chips('code-words')).toEqual(['XQ']);
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
    expect(el('codes-note').textContent).toContain('Unsaved');

    el('codes-save').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/room-codes');
    expect(post.body).toEqual({ words: ['XQ'], patterns: [] });
  });

  it('refuses a wildcard in the word field and an over-long word, without a request', async () => {
    await openPlatform();

    el('code-word').value = 'Q7*';
    el('code-word-add').click();
    await tick();
    expect(chips('code-words')).toEqual([]);
    expect(el('toast-host').textContent).toContain('pattern field');

    el('code-word').value = 'TOOLONG';
    el('code-word-add').click();
    await tick();
    expect(chips('code-words')).toEqual([]);
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('accepts an entry the generator can never produce, but says so', async () => {
    await openPlatform();
    el('code-word').value = 'XO'; // the alphabet has no O — too easily misread aloud

    el('code-word-add').click();
    await tick();

    expect(chips('code-words')).toEqual(['XO']);
    expect(el('toast-host').textContent).toContain('never be generated');
    expect(el('code-words').querySelector('.chip-unreachable')).not.toBeNull();
  });

  it('removes an entry with its own x', async () => {
    await openPlatform({
      'GET /admin/api/room-codes': { body: codes({ words: ['XQ', 'K3'] }) },
    });

    el('code-words').querySelectorAll('.chip-action')[0].click();
    await tick();
    expect(chips('code-words')).toEqual(['K3']);
  });

  it('rejects a duplicate without touching the list', async () => {
    await openPlatform({ 'GET /admin/api/room-codes': { body: codes({ words: ['XQ'] }) } });
    el('code-word').value = 'xq';

    el('code-word-add').click();
    await tick();

    expect(chips('code-words')).toEqual(['XQ']);
    expect(el('toast-host').textContent).toContain('already blocked');
  });

  it('confirms clearing everything, and cancelling changes nothing', async () => {
    await openPlatform({ 'GET /admin/api/room-codes': { body: codes({ words: ['XQ'] }) } });

    el('codes-clear').click();
    await tick();
    el('confirm-cancel').click();
    await tick();

    expect(chips('code-words')).toEqual(['XQ']);
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('keeps the blocklist when the server refuses to clear it', async () => {
    // The draft used to be emptied BEFORE the POST, and saveRoomCodes only re-renders on success — so a
    // rejected clear left every chip on screen over an empty draft, and the operator's next Save deleted
    // the whole blocklist without asking. Which is the outcome they had just been told did not happen.
    await openPlatform({
      'GET /admin/api/room-codes': { body: codes({ words: ['XQ'], patterns: ['Q7*'] }) },
      '* /admin/api/room-codes': { status: 500, body: { success: false, error: 'Could not save.' } },
    });

    el('codes-clear').click();
    await tick();
    el('confirm-ok').click();
    await tick();
    await tick();

    expect(el('toast-host').textContent).toContain('Could not save.');
    expect(chips('code-words')).toEqual(['XQ']);
    expect(chips('code-patterns')).toEqual(['Q7*']);

    // The draft still holds them too, which is the half the screen could not show: saving now must send
    // the blocklist back, not an empty one.
    const postsBefore = fake.calls.filter((c) => c.method === 'POST').length;
    el('codes-save').click();
    await tick();
    const post = fake.calls.filter((c) => c.method === 'POST')[postsBefore];
    expect(post.body).toEqual({ words: ['XQ'], patterns: ['Q7*'] });
  });

  it('surfaces the server refusing a blocklist that removes too much', async () => {
    await openPlatform({
      '* /admin/api/room-codes': {
        status: 409,
        body: { success: false, error: 'That blocklist removes 60% of possible codes, over the 50% limit.' },
      },
    });
    el('code-pattern').value = '*';
    el('code-pattern-add').click();
    await tick();

    el('codes-save').click();
    await tick();
    await tick();

    // The client deliberately doesn't try to compute this — only the server walks the code space.
    expect(el('toast-host').textContent).toContain('over the 50% limit');
  });
});

describe('update schedule', () => {
  it('renders the schedule in force and when it next runs', async () => {
    await openPlatform();

    expect(el('schedule-cadence').value).toBe('daily');
    expect(el('schedule-hour').value).toBe('3');
    // The hours are built in JS rather than as 24 <option>s of markup, and each carries the reader's own
    // clock beside the UTC hour it stores — the schedule is UTC, but nobody should have to convert it.
    expect(el('schedule-hour').options.length).toBe(24);
    expect(el('schedule-hour').options[3].textContent).toContain('03:00 UTC');
    expect(el('schedule-hour').options[3].textContent).toContain('local');
    expect(el('schedule-note').textContent).toContain('daily at 03:00 UTC');
    expect(el('schedule-note').textContent).toContain('2 game(s) enrolled');
    // Not overridden: this is still the configured default.
    expect(el('schedule-badge').hidden).toBe(true);
  });

  it('badges a schedule the operator chose', async () => {
    await openPlatform({
      'GET /admin/api/updates/schedule': {
        body: schedule({ cadence: 'weekly', dayOfWeek: 'tuesday', hourUtc: 14, overridden: true }),
      },
    });

    expect(el('schedule-badge').hidden).toBe(false);
    expect(el('schedule-cadence').value).toBe('weekly');
    expect(el('schedule-day').value).toBe('tuesday');
    expect(el('schedule-hour').value).toBe('14');
  });

  it('greys out the fields the cadence does not use, without discarding them', async () => {
    // Disabled rather than hidden: a control that vanishes makes an operator wonder whether the value
    // went with it, and weekly → daily → weekly has to come back to the day they picked.
    await openPlatform({
      'GET /admin/api/updates/schedule': {
        body: schedule({ cadence: 'weekly', dayOfWeek: 'friday', overridden: true }),
      },
    });
    expect(el('schedule-day').disabled).toBe(false);
    expect(el('schedule-hour').disabled).toBe(false);

    el('schedule-cadence').value = 'daily';
    el('schedule-cadence').dispatchEvent(new Event('change'));
    expect(el('schedule-day').disabled).toBe(true);
    expect(el('schedule-hour').disabled).toBe(false);
    expect(el('schedule-day').value).toBe('friday');

    el('schedule-cadence').value = 'hourly';
    el('schedule-cadence').dispatchEvent(new Event('change'));
    expect(el('schedule-day').disabled).toBe(true);
    expect(el('schedule-hour').disabled).toBe(true);
  });

  it('posts the chosen cadence, day and hour', async () => {
    await openPlatform();

    el('schedule-cadence').value = 'weekly';
    el('schedule-cadence').dispatchEvent(new Event('change'));
    el('schedule-day').value = 'wednesday';
    el('schedule-hour').value = '21';
    el('schedule-save').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/updates/schedule');
    expect(post).toBeTruthy();
    expect(post.body).toEqual({ cadence: 'weekly', dayOfWeek: 'wednesday', hourUtc: 21 });
  });

  it('reverts to the configured default by posting nothing', async () => {
    // Same shape as clearing every limit override: absence is how "use the default" is said.
    await openPlatform();

    el('schedule-reset').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/updates/schedule');
    expect(post.body).toEqual({});
  });

  it('says so and disables the form when the marketplace is switched off', async () => {
    await openPlatform({
      'GET /admin/api/updates/schedule': { status: 409, body: { error: 'The marketplace is disabled.' } },
    });

    expect(el('schedule-cadence').disabled).toBe(true);
    expect(el('schedule-save').disabled).toBe(true);
    expect(el('schedule-note').textContent).toContain('MarketplaceEnabled=false');
  });

  it('warns when a schedule has nothing enrolled to act on', async () => {
    // A schedule with no enrolled game makes no request at all, so an operator who set one and saw
    // nothing happen would reasonably conclude it was broken.
    await openPlatform({
      'GET /admin/api/updates/schedule': { body: schedule({ enrolled: 0 }) },
    });

    expect(el('schedule-note').textContent).toContain('No game is enrolled');
  });
});

describe('player announcement', () => {
  it('offers every installed game as a scope, plus all games', async () => {
    await openPlatform();

    const options = [...el('announce-game').options].map((o) => [o.value, o.textContent]);
    // Built from what the server reported, so the form can't offer a scope the POST would 404 on.
    expect(options).toEqual([['', 'All games'], ['ttt', 'Tic-Tac-Toe'], ['word-rush', 'Word Rush']]);
    expect(el('announce-badge').textContent).toBe('None');
    expect(el('announce-clear').disabled).toBe(true);
  });

  it('shows a live announcement, badged, with clear enabled', async () => {
    await openPlatform({
      'GET /admin/api/announcement': {
        body: announcement({
          id: 'a1', text: 'Maintenance in 20 minutes.', severity: 'warning',
          gameId: 'word-rush', postedAt: '2026-08-13T10:00:00.000Z',
        }),
      },
    });

    expect(el('announce-text').value).toBe('Maintenance in 20 minutes.');
    expect(el('announce-severity').value).toBe('warning');
    expect(el('announce-game').value).toBe('word-rush');
    expect(el('announce-badge').textContent).toBe('Live');
    expect(el('announce-clear').disabled).toBe(false);
    expect(el('announce-note').textContent).toContain('3 player(s) connected');
  });

  it('posts text, severity and scope, and reports how many were reached', async () => {
    await openPlatform();
    el('announce-text').value = '  Trivia Clash retires on the 15th.  ';
    el('announce-severity').value = 'warning';
    el('announce-game').value = 'ttt';

    el('announce-post').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/announcement');
    expect(post.body).toEqual({
      text: 'Trivia Clash retires on the 15th.', severity: 'warning', gameId: 'ttt',
    });
    expect(el('toast-host').textContent).toContain('Posted to 3');
  });

  it('sends a null scope for a platform-wide notice', async () => {
    await openPlatform();
    el('announce-text').value = 'Hello everyone';

    el('announce-post').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/announcement');
    expect(post.body.gameId).toBeNull();
  });

  it('refuses an empty message without a request', async () => {
    await openPlatform();
    el('announce-text').value = '   ';

    el('announce-post').click();
    await tick();

    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
    expect(el('toast-host').textContent).toContain('Enter the message');
  });

  it('confirms clearing, because every reader loses it at once', async () => {
    await openPlatform({
      'GET /admin/api/announcement': { body: announcement({ id: 'a1', text: 'Live one' }) },
    });

    el('announce-clear').click();
    await tick();
    expect(el('confirm-body').textContent).toContain('every player');

    el('confirm-cancel').click();
    await tick();
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);

    el('announce-clear').click();
    await tick();
    el('confirm-ok').click();
    await tick();
    await tick();
    expect(fake.calls.some((c) => c.path === '/admin/api/announcement/delete')).toBe(true);
  });
});

describe('webhooks', () => {
  const hookRow = (id) => document.querySelector(`#hook-body tr[data-hook-id="${id}"]`);

  it('lists endpoints with their events and last delivery, showing only the URL origin', async () => {
    await openPlatform({
      'GET /admin/api/webhooks': {
        body: webhooks({
          endpoints: [
            {
              id: 'ops', name: 'Ops channel', url: 'https://discord.com/api/webhooks/123/secret-token',
              events: ['logError'], enabled: true, lastAt: '2026-08-13T10:00:00.000Z',
              lastOk: true, lastStatus: 204, lastError: null, lastEvent: 'logError',
            },
          ],
        }),
      },
    });

    const row = hookRow('ops');
    expect(row.textContent).toContain('Ops channel');
    expect(row.textContent).toContain('Errors');
    expect(row.textContent).toContain('OK (204)');
    // A webhook URL is a bearer credential — the token must not be on screen.
    expect(row.textContent).toContain('https://discord.com');
    expect(row.textContent).not.toContain('secret-token');
  });

  it('marks a failed delivery and distinguishes "no response" from a status', async () => {
    await openPlatform({
      'GET /admin/api/webhooks': {
        body: webhooks({
          endpoints: [
            {
              id: 'dead', name: 'Dead', url: 'https://nope.example.com/hook', events: [], enabled: true,
              lastAt: '2026-08-13T10:00:00.000Z', lastOk: false, lastStatus: null,
              lastError: 'No such host is known.', lastEvent: 'logError',
            },
          ],
        }),
      },
    });

    const row = hookRow('dead');
    expect(row.className).toContain('row-warn');
    expect(row.textContent).toContain('No response');
    expect(row.textContent).toContain('All events'); // empty subscription means everything
  });

  it('refuses a plain-http non-loopback URL and a bad id without a request', async () => {
    await openPlatform();

    el('hook-id').value = 'ops';
    el('hook-url').value = 'http://example.com/hook';
    el('hook-add').click();
    await tick();
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
    expect(el('toast-host').textContent).toContain('https');

    el('hook-id').value = 'not a valid id!';
    el('hook-url').value = 'https://example.com/hook';
    el('hook-add').click();
    await tick();
    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('accepts http on loopback, for a local monitoring agent', async () => {
    await openPlatform();
    el('hook-id').value = 'local';
    el('hook-url').value = 'http://127.0.0.1:9099/hook';

    el('hook-add').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/webhooks');
    expect(post.body.url).toBe('http://127.0.0.1:9099/hook');
  });

  it('posts the selected events, and none means all', async () => {
    await openPlatform();
    el('hook-id').value = 'ops';
    el('hook-url').value = 'https://example.com/hook';
    document.querySelector('#hook-events input[data-hook-event="updateFailed"]').checked = true;

    el('hook-add').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST' && c.path === '/admin/api/webhooks');
    expect(post.body.events).toEqual(['updateFailed']);
  });

  it('tests an endpoint through the real delivery path and surfaces an upstream failure', async () => {
    await openPlatform({
      'GET /admin/api/webhooks': {
        body: webhooks({
          endpoints: [{ id: 'ops', name: 'Ops', url: 'https://example.com/hook', events: [], enabled: true }],
        }),
      },
      '* /admin/api/webhooks/ops/test': {
        status: 502, body: { success: false, error: 'Delivery failed (404): Not Found' },
      },
    });

    hookRow('ops').querySelector('.btn-secondary').click();
    await tick();
    await tick();

    expect(el('toast-host').textContent).toContain('Delivery failed (404)');
  });

  it('confirms removal, naming that the URL is not stored elsewhere', async () => {
    await openPlatform({
      'GET /admin/api/webhooks': {
        body: webhooks({
          endpoints: [{ id: 'ops', name: 'Ops', url: 'https://example.com/hook', events: [], enabled: true }],
        }),
      },
    });

    hookRow('ops').querySelector('.btn-danger').click();
    await tick();
    expect(el('confirm-body').textContent).toContain('paste it again');

    el('confirm-ok').click();
    await tick();
    await tick();
    expect(fake.calls.some((c) => c.path === '/admin/api/webhooks/ops/delete')).toBe(true);
  });

  it('says so when the feature is switched off, without hiding the saved endpoints', async () => {
    await openPlatform({
      'GET /admin/api/webhooks': {
        body: webhooks({
          enabled: false,
          endpoints: [{ id: 'ops', name: 'Ops', url: 'https://example.com/hook', events: [], enabled: true }],
        }),
      },
    });

    expect(el('hook-note').textContent).toContain('WebhooksEnabled=false');
    expect(hookRow('ops')).not.toBeNull();
  });
});
