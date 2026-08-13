// @vitest-environment jsdom
//
// The Marketplace tab: the split poll rate that keeps a network-backed catalog off a 3-second timer, the
// version control that serves both targeting and rollback, the XHR upload path, and the lifecycle badges
// the games tab grew.
//
// Same shape as admin.test.js — reset modules, inject the REAL admin/index.html so element ids can't
// drift from production markup, stub fetch, import fresh. Split into its own file only because that one
// had grown past the point where a reader could find anything.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { installFakeFetch, installFakeXhr, loadAdminDom, makeKbgFile, tick } from './helpers.js';

const el = (id) => document.getElementById(id);
const card = (id) => document.querySelector(`.mkt-card[data-id="${id}"]`);

let admin;
let fake;

async function importAdmin() {
  admin = await import('../admin/admin.js');
  return admin;
}

const STATUS = {
  uptime: '0d 1h', activeLobbies: 0, registeredGames: 2, workingSetMb: 61, managedHeapMb: 3,
  hostTime: '2026-08-12T20:00:00.000Z', connectedPlayers: 0, gameSockets: 0, authorityLobbies: 0,
  maintenanceMode: false, maintenanceMessage: null, cpuPercentLifetime: 0.4, cpuSecondsTotal: 12,
  processorCount: 8, gen0Collections: 1, gen1Collections: 0, gen2Collections: 0,
  scanError: null, settingsError: null, diagnostics: [],
};

const GAMES = {
  gamesRoot: '/srv/games', packagesRoot: '/app/games-unpacked', scanError: null,
  diskMeasuredAt: '2026-08-12T20:00:00.000Z', compressedCacheBytes: 0, logsBytes: 0,
  managedRoot: '/app/games-managed', managedRootBytes: 0,
  games: [{
    id: 'word-rush', name: 'Word Rush', version: '1.2.0', availability: 'available', maxPlayers: 8,
    serverAuthority: false, directory: '/app/games-unpacked/word-rush', root: 'packages',
    packageBacked: true, packageRoot: 'managed', diskBytes: 1000, directoryBytes: 800,
    compressedBytes: 100, packageBytes: 100, backupBytes: 0, activeLobbies: 0, activePlayers: 0,
    deletable: true, deleteBlockedReason: null, lifecycle: 'ready', updatePolicy: 'manual',
    pendingJobId: null,
  }],
};

const CATALOG = {
  enabled: true, appVersion: '1.4.0', fetchedAt: '2026-08-12T20:00:00.000Z', maxSources: 8,
  backupRetention: 1, maxUploadBytes: 536_870_912, canInstall: true, installBlockedReason: null,
  managedRoot: '/app/games-managed', jobsLastSequence: 0, jobs: [],
  sources: [{
    id: 'official', name: 'Official KnockBox marketplace', catalogUrl: 'https://example/CATALOG.json',
    downloadBaseUrl: 'https://github.com', enabled: true, builtIn: true, entries: 2, error: null,
  }],
  entries: [
    {
      id: 'word-rush', name: 'Word Rush', description: 'Fast word game', author: 'Someone',
      tags: ['party'], availableVersion: '1.3.0', installedVersion: '1.2.0', status: 'updateAvailable',
      reason: null, sizeBytes: 2_000_000, publishedAt: null, minAppVersion: null, maxAppVersion: null,
      sourceId: 'official', sourceName: 'Official KnockBox marketplace', shadowedBy: null,
      managed: true, installed: true, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
      backups: [{ version: '1.1.0', bytes: 900_000, retainedAt: '2026-07-01T00:00:00.000Z' }],
    },
    {
      id: 'alpha-chain', name: 'Alpha Chain', description: 'A chain game', author: null, tags: [],
      availableVersion: '2.0.0', installedVersion: null, status: 'notInstalled', reason: null,
      sizeBytes: 1_000_000, publishedAt: null, minAppVersion: null, maxAppVersion: null,
      sourceId: 'official', sourceName: 'Official KnockBox marketplace', shadowedBy: null,
      managed: false, installed: false, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
      backups: [],
    },
  ],
};

const NO_JOBS = { jobs: [], lastSequence: 0, active: 0, retained: 0 };

const runningJob = {
  jobId: 'j9', sequence: 2, kind: 'install', source: 'marketplace', gameId: 'alpha-chain',
  gameName: 'Alpha Chain', fromVersion: null, toVersion: '2.0.0', status: 'downloading',
  phase: 'Downloading from owner/repo.', bytesDone: 500_000, bytesTotal: 1_000_000, mode: 'drain',
  startedAt: '2026-08-12T20:00:00.000Z', endedAt: null, error: null, warning: null,
  lobbiesWaiting: 0, cancellable: true, terminal: false,
};

function routes(overrides = {}) {
  return {
    'GET /admin/api/auth/status': { body: { configured: true, authenticated: true } },
    'GET /admin/api/system/status': { body: STATUS },
    'GET /admin/api/metrics': { body: { games: [], controlSockets: 0, gameSockets: 0, framesSent: 0, bytesSent: 0, framesDropped: 0, trackedRateLimitIps: 0 } },
    'GET /admin/api/games': { body: GAMES },
    'GET /admin/api/marketplace/catalog': { body: CATALOG },
    'GET /admin/api/packages/jobs': { body: NO_JOBS },
    '* /admin/api/marketplace/install/alpha-chain': { status: 202, body: { success: true, jobId: 'j1', detail: 'Downloading.' } },
    '* /admin/api/packages/word-rush/rollback': { status: 202, body: { success: true, jobId: 'j2', detail: 'Rolling back.' } },
    '* /admin/api/packages/word-rush/uninstall': { status: 202, body: { success: true, jobId: 'j3', detail: 'Uninstalling.' } },
    '* /admin/api/packages/word-rush/update-policy': { body: { success: true, detail: 'Set.' } },
    '* /admin/api/packages/jobs/j9/cancel': { body: { success: true, detail: 'Cancelling.' } },
    ...overrides,
  };
}

async function openMarketplace(overrides) {
  fake = installFakeFetch(routes(overrides));
  await importAdmin();
  admin.bootstrap();
  await tick();
  await tick();
  admin.selectTab('marketplace');
  await tick();
  await tick();
  await tick();
  return fake;
}

beforeEach(() => {
  vi.resetModules();
  loadAdminDom();
  // history.replaceState, NOT `location.hash = ''`: jsdom reuses one window across the file, so every
  // previously-imported copy of admin.js still has its hashchange listener attached. Assigning the hash
  // fires that event, each stale module answers it by re-rendering into the fresh DOM and re-fetching,
  // and a test counting requests then sees several it never made. replaceState changes the fragment
  // without dispatching anything.
  history.replaceState(null, '', window.location.pathname);
});

afterEach(() => {
  // Each case imports a fresh copy of admin.js, so the previous one's poll interval would otherwise
  // keep firing against THIS case's fetch stub — recording requests it never made.
  admin?.stopPolling();
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('marketplace catalog', () => {
  it('renders a card per entry, showing installed against available', async () => {
    await openMarketplace();

    expect(document.querySelectorAll('.mkt-card')).toHaveLength(2);
    const wordRush = card('word-rush');
    expect(wordRush.textContent).toContain('Word Rush');
    expect(wordRush.textContent).toContain('Update available');
    expect(wordRush.textContent).toContain('v1.2.0');
    expect(wordRush.textContent).toContain('v1.3.0');
    expect(card('alpha-chain').textContent).toContain('Not installed');
  });

  it('reads the catalog once on entry, then polls only the job feed', async () => {
    // This test IS the design. The catalog can reach the network with a 30-second timeout, so it must
    // never be what a 3-second interval hits.
    vi.useFakeTimers();
    fake = installFakeFetch(routes());
    await importAdmin();
    admin.bootstrap();
    await vi.advanceTimersByTimeAsync(1);
    admin.selectTab('marketplace');
    await vi.advanceTimersByTimeAsync(1);

    const catalogCalls = () => fake.calls.filter((c) => c.path === '/admin/api/marketplace/catalog').length;
    const jobCalls = () => fake.calls.filter((c) => c.path === '/admin/api/packages/jobs').length;
    expect(catalogCalls()).toBe(1);

    await vi.advanceTimersByTimeAsync(9000);

    expect(jobCalls()).toBeGreaterThanOrEqual(3);
    expect(catalogCalls()).toBe(1);
  });

  it('re-reads the catalog and toasts once when a job finishes', async () => {
    vi.useFakeTimers();
    let polls = 0;
    fake = installFakeFetch(routes({
      'GET /admin/api/packages/jobs': () => {
        polls += 1;
        return polls === 1 ? { body: NO_JOBS } : {
          body: {
            jobs: [{
              ...runningJob, jobId: 'j5', sequence: 5, gameId: 'word-rush', gameName: 'Word Rush',
              status: 'succeeded', phase: 'Updated to 1.3.0.', terminal: true, cancellable: false,
              endedAt: '2026-08-12T20:01:00.000Z',
            }],
            lastSequence: 5, active: 0, retained: 1,
          },
        };
      },
    }));
    await importAdmin();
    admin.bootstrap();
    await vi.advanceTimersByTimeAsync(1);
    admin.selectTab('marketplace');
    await vi.advanceTimersByTimeAsync(1);

    const catalogCalls = () => fake.calls.filter((c) => c.path === '/admin/api/marketplace/catalog').length;
    expect(catalogCalls()).toBe(1);

    await vi.advanceTimersByTimeAsync(3500);
    expect(catalogCalls()).toBe(2);
    expect(el('toast-host').querySelectorAll('.toast')).toHaveLength(1);
    expect(el('toast-host').textContent).toContain('Updated to 1.3.0');

    // The same finished job keeps arriving on every subsequent poll, and must be announced exactly
    // once — and must not re-read the catalog again either.
    await vi.advanceTimersByTimeAsync(9000);
    expect(catalogCalls()).toBe(2);
  });

  it('filters client-side, with no round trip', async () => {
    fake = await openMarketplace();
    const before = fake.calls.length;

    el('mkt-filter-q').value = 'alpha';
    el('mkt-filter-q').dispatchEvent(new Event('input'));

    expect(document.querySelectorAll('.mkt-card')).toHaveLength(1);
    expect(card('alpha-chain')).not.toBeNull();
    expect(fake.calls.length).toBe(before);
  });

  it('explains itself and still offers upload when the marketplace is switched off', async () => {
    await openMarketplace({
      'GET /admin/api/marketplace/catalog': { body: { ...CATALOG, enabled: false, entries: [], sources: [] } },
    });

    expect(el('mkt-disabled').classList.contains('hidden')).toBe(false);
    // Upload, rollback and uninstall all work on an air-gapped host, so that button stays live.
    expect(el('mkt-upload-btn').disabled).toBe(false);
  });
});

describe('marketplace actions', () => {
  it('installs, naming the source the entry came from', async () => {
    fake = await openMarketplace();

    card('alpha-chain').querySelector('.mkt-action').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.path === '/admin/api/marketplace/install/alpha-chain');
    expect(post).toBeTruthy();
    expect(post.body.sourceId).toBe('official');
  });

  it('turns the action into a rollback when an older retained version is selected', async () => {
    await openMarketplace();
    const wordRush = card('word-rush');
    const version = wordRush.querySelector('.mkt-version');
    const action = wordRush.querySelector('.mkt-action');

    expect(action.textContent).toBe('Update');

    version.value = '1.1.0';
    version.dispatchEvent(new Event('change'));

    // One control serves both version targeting and rollback, because rolling back IS targeting an
    // older version you already hold.
    expect(action.textContent).toBe('Roll back');
    expect(action.classList.contains('btn-danger')).toBe(true);
  });

  it('confirms a rollback by naming both versions, then POSTs it', async () => {
    fake = await openMarketplace();
    const wordRush = card('word-rush');
    const version = wordRush.querySelector('.mkt-version');
    version.value = '1.1.0';
    version.dispatchEvent(new Event('change'));

    wordRush.querySelector('.mkt-action').click();
    await tick();

    expect(el('confirm-body').textContent).toContain('v1.2.0');
    expect(el('confirm-body').textContent).toContain('v1.1.0');

    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.path === '/admin/api/packages/word-rush/rollback');
    expect(post.body.version).toBe('1.1.0');
  });

  it('uninstalls through the package route, not the games delete route', async () => {
    fake = await openMarketplace();

    card('word-rush').querySelector('.mkt-uninstall').click();
    await tick();
    el('confirm-ok').click();
    await tick();
    await tick();

    expect(fake.calls.some((c) => c.path === '/admin/api/packages/word-rush/uninstall')).toBe(true);
    expect(fake.calls.some((c) => c.path === '/admin/api/games/word-rush/delete')).toBe(false);
  });

  it('offers no uninstall for a game it does not manage', async () => {
    await openMarketplace();

    expect(card('alpha-chain').querySelector('.mkt-uninstall')).toBeNull();
  });

  it('sets an update policy', async () => {
    fake = await openMarketplace();
    const policy = card('word-rush').querySelector('.mkt-policy');

    policy.value = 'drain';
    policy.dispatchEvent(new Event('change'));
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.path === '/admin/api/packages/word-rush/update-policy');
    expect(post.body.policy).toBe('drain');
  });

  it('disables the mode chooser rather than hiding it when nothing is running', async () => {
    // A control that appears and disappears between polls is worse than one that is visibly inert.
    await openMarketplace();
    const mode = card('word-rush').querySelector('.mkt-mode');

    expect(mode).not.toBeNull();
    expect(mode.disabled).toBe(true);
    expect(mode.title).toMatch(/nobody is playing/i);
  });

  it('surfaces the server refusal for a bad source rather than re-checking the rule here', async () => {
    fake = await openMarketplace({
      'POST /admin/api/marketplace/sources': {
        status: 400,
        body: { success: false, error: 'The catalog URL must be an absolute https URL (http is allowed only on loopback).' },
      },
    });

    el('mkt-settings-btn').click();
    el('mkt-source-id').value = 'staging';
    el('mkt-source-name').value = 'Staging';
    el('mkt-source-url').value = 'http://example.com/CATALOG.json';
    el('mkt-source-add').click();
    await tick();
    await tick();

    // The URL rule lives in MarketplaceClient. A second copy in JS is exactly the drift this avoids.
    expect(fake.calls.some((c) => c.path === '/admin/api/marketplace/sources')).toBe(true);
    expect(el('toast-host').textContent).toContain('absolute https URL');
  });
});

describe('operations list', () => {
  it('renders a running job with determinate progress and a cancel button', async () => {
    fake = await openMarketplace({
      'GET /admin/api/packages/jobs': { body: { jobs: [runningJob], lastSequence: 2, active: 1, retained: 1 } },
    });

    const row = document.querySelector('.job-row[data-job="j9"]');
    expect(row.textContent).toContain('Downloading from owner/repo.');
    expect(row.querySelector('.job-bar-fill').style.width).toBe('50%');

    row.querySelector('.job-cancel').click();
    await tick();
    await tick();

    expect(fake.calls.some((c) => c.path === '/admin/api/packages/jobs/j9/cancel')).toBe(true);
  });

  it('renders indeterminate progress rather than a confident zero when the total is unknown', async () => {
    await openMarketplace({
      'GET /admin/api/packages/jobs': {
        body: {
          jobs: [{ ...runningJob, jobId: 'j8', status: 'verifying', bytesDone: 0, bytesTotal: 0 }],
          lastSequence: 1, active: 1, retained: 1,
        },
      },
    });

    const fill = document.querySelector('.job-row[data-job="j8"] .job-bar-fill');
    expect(fill.classList.contains('job-bar-indeterminate')).toBe(true);
    expect(fill.style.width).toBe('');
  });

  it('keeps a failed job visible with its error', async () => {
    await openMarketplace({
      'GET /admin/api/packages/jobs': {
        body: {
          jobs: [{
            ...runningJob, jobId: 'j7', status: 'failed', terminal: true, cancellable: false,
            phase: 'Failed.', error: 'the release was modified after it was catalogued',
          }],
          lastSequence: 3, active: 0, retained: 1,
        },
      },
    });

    const row = document.querySelector('.job-row[data-job="j7"]');
    expect(row.classList.contains('job-failed')).toBe(true);
    expect(row.textContent).toContain('modified after it was catalogued');
    expect(row.querySelector('.job-cancel')).toBeNull();
  });

  it('resets the job cursor on tab entry so a returning operator sees what they missed', async () => {
    fake = await openMarketplace();

    admin.selectTab('logs');
    await tick();
    admin.selectTab('marketplace');
    await tick();
    await tick();

    const last = fake.calls.filter((c) => c.path === '/admin/api/packages/jobs').at(-1);
    expect(last.url).toContain('after=0');
  });
});

describe('package upload', () => {
  async function openUploadModal(overrides) {
    fake = await openMarketplace(overrides);
    el('mkt-upload-btn').click();
    return fake;
  }

  function drop(file) {
    el('upload-drop').dispatchEvent(Object.assign(new Event('drop'), { dataTransfer: { files: [file] } }));
  }

  it('refuses a non-.kbg inline, with zero requests', async () => {
    fake = await openUploadModal();
    const xhr = installFakeXhr();
    const before = fake.calls.length;

    drop(makeKbgFile('game.zip', 1000));
    el('upload-submit').click();

    // Inline, not a toast: the operator is looking at the modal and has to change the input.
    expect(el('upload-error').classList.contains('hidden')).toBe(false);
    expect(el('upload-error').textContent).toMatch(/knockbox-pack/);
    expect(xhr.instances).toHaveLength(0);
    expect(fake.calls.length).toBe(before);
  });

  it('sends the raw bytes as octet-stream, reports progress, and closes on acceptance', async () => {
    await openUploadModal();
    const xhr = installFakeXhr();

    const kbg = makeKbgFile('word-rush.kbg', 2048);
    drop(kbg);
    el('upload-submit').click();

    const sent = xhr.instances[0];
    expect(sent.method).toBe('POST');
    expect(sent.url).toContain('/admin/api/packages/upload');
    expect(sent.headers['Content-Type']).toBe('application/octet-stream');
    expect(sent.body).toBe(kbg);

    // The whole reason this one path is XHR and not fetch.
    sent._progress(1024, 2048);
    expect(el('upload-progress-fill').style.width).toBe('50%');

    sent._respond(202, { success: true, jobId: 'j1', detail: 'Installing.' });
    await tick();

    expect(el('upload-backdrop').classList.contains('hidden')).toBe(true);
    expect(el('toast-host').textContent).toContain('Installing.');
  });

  it('shows the server refusal inline and keeps the modal open', async () => {
    await openUploadModal();
    const xhr = installFakeXhr();

    drop(makeKbgFile('demo.kbg', 2048));
    el('upload-submit').click();
    xhr.instances[0]._respond(413, { success: false, error: 'The package exceeds the limit.' });
    await tick();

    expect(el('upload-backdrop').classList.contains('hidden')).toBe(false);
    expect(el('upload-error').textContent).toContain('exceeds the limit');
  });

  it('returns to the login view when the session expired mid-upload', async () => {
    // The 401 funnel request() owns. The XHR path has to call it too — forgetting would leave an
    // operator staring at a modal that never finishes.
    await openUploadModal({
      'GET /admin/api/auth/status': { body: { configured: true, authenticated: false } },
    });
    const xhr = installFakeXhr();

    drop(makeKbgFile('demo.kbg', 2048));
    el('upload-submit').click();
    xhr.instances[0]._respond(401, { success: false, error: 'Unauthorized.' });
    await tick();
    await tick();

    expect(el('login-view').classList.contains('hidden')).toBe(false);
  });

  it('reports a transport failure without closing the modal', async () => {
    await openUploadModal();
    const xhr = installFakeXhr();

    drop(makeKbgFile('demo.kbg', 2048));
    el('upload-submit').click();
    xhr.instances[0]._fail();

    expect(el('upload-backdrop').classList.contains('hidden')).toBe(false);
    expect(el('upload-error').textContent).toMatch(/could not be sent/i);
    expect(el('upload-submit').disabled).toBe(false);
  });
});

describe('games tab lifecycle', () => {
  async function openGames(overrides) {
    fake = installFakeFetch(routes(overrides));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    admin.selectTab('games');
    await tick();
    await tick();
    return fake;
  }

  const gameCard = () => document.querySelector('#games-list .game-card');

  it('badges a game the engine is mid-update on, and holds its controls', async () => {
    await openGames({
      'GET /admin/api/games': {
        body: { ...GAMES, games: [{ ...GAMES.games[0], lifecycle: 'updating' }] },
      },
    });

    const card = gameCard();
    expect(card.textContent).toContain('Updating');
    // An availability write racing a directory swap is arbitration the engine shouldn't have to do.
    expect(card.querySelector('select').disabled).toBe(true);
    expect(card.querySelector('.btn-danger').disabled).toBe(true);
  });

  it('keeps the availability control to exactly the three operator states', async () => {
    // The canary. Engine states are NOT options in a command control: offering a value the server would
    // have to refuse is worse than not offering it.
    await openGames({
      'GET /admin/api/games': {
        body: { ...GAMES, games: [{ ...GAMES.games[0], lifecycle: 'updating' }] },
      },
    });

    const options = [...gameCard().querySelector('select').options].map((o) => o.value);
    expect(options).toEqual(['available', 'disabled', 'staged']);
  });

  it('leaves an ordinary game alone', async () => {
    await openGames();

    const card = gameCard();
    expect(card.textContent).not.toContain('Updating');
    expect(card.querySelector('select').disabled).toBe(false);
  });

  it('points at the marketplace tab rather than duplicating its controls', async () => {
    await openGames({
      'GET /admin/api/games': {
        body: { ...GAMES, games: [{ ...GAMES.games[0], lifecycle: 'draining' }] },
      },
    });

    const jump = [...gameCard().querySelectorAll('button')].find((b) => b.textContent === 'View operation');
    expect(jump).toBeTruthy();

    jump.click();
    expect(el('mkt-filter-q').value).toBe('word-rush');
    expect(window.location.hash).toBe('#marketplace');
  });
});
