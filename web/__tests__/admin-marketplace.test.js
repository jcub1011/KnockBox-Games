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
      license: 'MIT', contentRating: 'everyone', minPlayers: 2, maxPlayers: 8,
      homepage: 'https://example.com/word-rush', bugs: 'https://example.com/word-rush/issues',
      reason: null, sizeBytes: 2_000_000, publishedAt: null, minAppVersion: null, maxAppVersion: null,
      sourceId: 'official', sourceName: 'Official KnockBox marketplace', shadowedBy: null,
      managed: true, installed: true, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
      backups: [{ version: '1.1.0', bytes: 900_000, retainedAt: '2026-07-01T00:00:00.000Z' }],
    },
    {
      id: 'alpha-chain', name: 'Alpha Chain', description: 'A chain game', author: null, tags: [],
      // Deliberately none: this is every entry published before catalog schema 1.1.0.
      license: null, contentRating: null, minPlayers: null, maxPlayers: null, homepage: null, bugs: null,
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
  // keep firing against THIS case's fetch stub — recording requests it never made. The scroll-settle
  // timeout is the same trap one tick further out.
  admin?.stopPolling();
  admin?.stopScrollSettle();
  admin?.resetPluginStateForTests?.();
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

  it('shows the listing metadata a 1.1 catalog carries', async () => {
    await openMarketplace();

    const wordRush = card('word-rush');
    expect(wordRush.textContent).toContain('everyone');
    expect(wordRush.textContent).toContain('2–8');
    expect(wordRush.textContent).toContain('MIT');

    const links = [...wordRush.querySelectorAll('.mkt-links a')];
    expect(links.map((a) => a.textContent)).toEqual(['Homepage', 'Report a problem']);
    expect(links.map((a) => a.getAttribute('href')))
      .toEqual(['https://example.com/word-rush', 'https://example.com/word-rush/issues']);
    // The destination is chosen by the game's author, so it gets neither a window handle nor a referrer.
    for (const link of links) expect(link.getAttribute('rel')).toBe('noopener noreferrer');
  });

  it('renders nothing for the listing metadata an entry omits', async () => {
    // Every entry published before catalog 1.1.0 has none of these. Absent has to render as absent
    // rather than as "1–1 players" or an empty link.
    await openMarketplace();

    const alpha = card('alpha-chain');
    expect(alpha.textContent).not.toContain('Players');
    expect(alpha.textContent).not.toContain('License');
    expect(alpha.querySelector('.mkt-links')).toBeNull();
  });

  it('refuses to link a catalog url that is not https', async () => {
    // The catalog comes from a repository this server does not control, and the schema's https-only rule
    // is enforced where an entry is PUBLISHED. If a hand-edited or compromised catalog gets a
    // `javascript:` url this far, the portal must not turn it into a link on an authenticated page.
    await openMarketplace({
      'GET /admin/api/marketplace/catalog': {
        body: {
          ...CATALOG,
          entries: [{
            ...CATALOG.entries[0],
            homepage: 'javascript:alert(document.cookie)',
            bugs: 'http://insecure.example.com/issues',
          }],
        },
      },
    });

    const wordRush = card('word-rush');
    expect(wordRush.querySelector('.mkt-links')).toBeNull();
    expect(wordRush.innerHTML).not.toContain('javascript:');
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
    const gameCalls = () => fake.calls.filter((c) => c.path === '/admin/api/games').length;
    expect(catalogCalls()).toBe(1);
    expect(gameCalls()).toBe(1);

    await vi.advanceTimersByTimeAsync(3500);
    expect(catalogCalls()).toBe(2);
    expect(gameCalls()).toBeGreaterThan(1);
    expect(el('toast-host').querySelectorAll('.toast')).toHaveLength(1);
    expect(el('toast-host').textContent).toContain('Updated to 1.3.0');

    // The same finished job keeps arriving on every subsequent poll, and must be announced exactly
    // once — and must not re-read the catalog again either. /admin/api/games is a different matter:
    // it IS on the poll path, because this panel shows availability, lifecycle and live lobby counts.
    await vi.advanceTimersByTimeAsync(9000);
    expect(catalogCalls()).toBe(2);
    expect(gameCalls()).toBeGreaterThan(2);
  });

  it('filters client-side, with no round trip', async () => {
    fake = await openMarketplace();
    const before = fake.calls.length;

    const filterInput = el('plugins-filter-q') || el('mkt-filter-q');
    filterInput.value = 'alpha';
    filterInput.dispatchEvent(new Event('input'));

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

  it('renders an Export button on installed marketplace cards', async () => {
    await openMarketplace();
    const exportBtn = card('word-rush').querySelector('.mkt-export');
    expect(exportBtn).not.toBeNull();
    expect(exportBtn.textContent).toBe('Export');

    const appendChildSpy = vi.spyOn(document.body, 'appendChild');
    exportBtn.click();

    const anchor = appendChildSpy.mock.calls.find(([node]) => node.tagName === 'A')?.[0];
    expect(anchor).toBeDefined();
    expect(anchor.href).toContain('/admin/api/games/word-rush/export');
  });

  it('renders Uninstall button for unmanaged / folder-installed games', async () => {
    await openMarketplace({
      'GET /admin/api/marketplace/catalog': {
        body: {
          ...CATALOG,
          entries: [
            ...CATALOG.entries,
            {
              id: 'folder-game', name: 'Folder Game', description: null, author: null,
              tags: [], availableVersion: null, installedVersion: '1.0.0', status: 'installedOnly',
              license: null, contentRating: null, minPlayers: 1, maxPlayers: 4,
              homepage: null, bugs: null, reason: 'No registered marketplace offers this game.',
              sizeBytes: null, publishedAt: null, minAppVersion: null, maxAppVersion: null,
              sourceId: '', sourceName: null, shadowedBy: null,
              managed: false, installed: true, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
              backups: [],
            },
          ],
        },
      },
    });

    const folderCard = card('folder-game');
    expect(folderCard).not.toBeNull();
    const uninstallBtn = folderCard.querySelector('.mkt-uninstall');
    expect(uninstallBtn).not.toBeNull();
    expect(uninstallBtn.textContent).toBe('Uninstall');
  });

  it('warns that nothing can re-supply an unoffered plugin, and offers an export, before uninstalling', async () => {
    await openMarketplace({
      'GET /admin/api/marketplace/catalog': {
        body: {
          ...CATALOG,
          entries: [
            ...CATALOG.entries,
            {
              id: 'folder-game', name: 'Folder Game', description: null, author: null,
              tags: [], availableVersion: null, installedVersion: '1.0.0', status: 'installedOnly',
              license: null, contentRating: null, minPlayers: 1, maxPlayers: 4,
              homepage: null, bugs: null, reason: 'No registered marketplace offers this game.',
              sizeBytes: null, publishedAt: null, minAppVersion: null, maxAppVersion: null,
              sourceId: '', sourceName: null, shadowedBy: null,
              managed: false, installed: true, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
              backups: [],
            },
          ],
        },
      },
    });

    card('folder-game').querySelector('.mkt-uninstall').click();

    expect(el('confirm-warning').classList.contains('hidden')).toBe(false);
    expect(el('confirm-warning').textContent).toBe(
      'No marketplace source offers this plugin, so it cannot be re-downloaded — export a copy first.');
    expect(el('confirm-export').classList.contains('hidden')).toBe(false);
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

  it('offers "Install Anyways" for incompatible games with a confirmation warning that it is unsupported', async () => {
    fake = await openMarketplace({
      'GET /admin/api/marketplace/catalog': {
        body: {
          ...CATALOG,
          entries: [
            {
              id: 'future-game', name: 'Future Game', description: 'Requires newer server',
              author: 'FutureDev', tags: ['arcade'], availableVersion: '3.0.0',
              installedVersion: null, status: 'incompatible', reason: 'needs server 3.0.0',
              sizeBytes: 1_000_000, publishedAt: null, minAppVersion: '3.0.0', maxAppVersion: null,
              sourceId: 'official', sourceName: 'Official KnockBox marketplace', shadowedBy: null,
              managed: false, installed: false, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
              backups: [],
            },
          ],
        },
      },
      '* /admin/api/marketplace/install/future-game': { status: 202, body: { success: true, jobId: 'j10', detail: 'Downloading.' } },
    });

    const futureGame = card('future-game');
    const action = futureGame.querySelector('.mkt-action');
    expect(action.textContent).toBe('Install Anyways');
    expect(action.classList.contains('btn-danger')).toBe(true);
    expect(action.disabled).toBe(false);

    action.click();
    await tick();

    expect(el('confirm-backdrop').classList.contains('hidden')).toBe(false);
    expect(el('confirm-body').textContent).toContain('Future Game');
    expect(el('confirm-body').textContent).toContain('unsupported');
    expect(el('confirm-body').textContent).toContain('may not work');
    expect(el('confirm-body').textContent).toContain('needs server 3.0.0');
    expect(el('confirm-ok').textContent).toBe('Install Anyways');

    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.path === '/admin/api/marketplace/install/future-game');
    expect(post).toBeTruthy();
    expect(post.body.sourceId).toBe('official');
  });

  it('cancels "Install Anyways" without posting a request', async () => {
    fake = await openMarketplace({
      'GET /admin/api/marketplace/catalog': {
        body: {
          ...CATALOG,
          entries: [
            {
              id: 'future-game', name: 'Future Game', description: 'Requires newer server',
              author: 'FutureDev', tags: ['arcade'], availableVersion: '3.0.0',
              installedVersion: null, status: 'incompatible', reason: 'needs server 3.0.0',
              sizeBytes: 1_000_000, publishedAt: null, minAppVersion: '3.0.0', maxAppVersion: null,
              sourceId: 'official', sourceName: 'Official KnockBox marketplace', shadowedBy: null,
              managed: false, installed: false, activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
              backups: [],
            },
          ],
        },
      },
    });

    const futureGame = card('future-game');
    futureGame.querySelector('.mkt-action').click();
    await tick();

    el('confirm-cancel').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.path === '/admin/api/marketplace/install/future-game');
    expect(post).toBeUndefined();
  });

  it('turns the action into a rollback when an older retained version is selected', async () => {
    await openMarketplace();
    const wordRush = card('word-rush');
    const version = wordRush.querySelector('.mkt-version');
    const action = wordRush.querySelector('.mkt-action');

    expect(action.textContent).toBe('Update');

    version.value = 'backup:1.1.0';
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
    version.value = 'backup:1.1.0';
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

  it('loads older versions dynamically when load:more is selected', async () => {
    fake = await openMarketplace({
      'GET /admin/api/marketplace/plugins/word-rush/versions': {
        body: {
          id: 'word-rush',
          name: 'Word Rush',
          repo: 'owner/word-rush',
          currentVersion: '1.3.0',
          versions: [
            { version: '1.3.0', tag: 'v1.3.0', sizeBytes: 2_000_000, publishedAt: null, isCurrent: true },
            { version: '1.0.0', tag: 'v1.0.0', sizeBytes: 1_800_000, publishedAt: null, isCurrent: false },
          ],
        },
      },
    });

    const wordRush = card('word-rush');
    const version = wordRush.querySelector('.mkt-version');
    expect(Array.from(version.options).some((o) => o.value === 'load:more')).toBe(true);

    version.value = 'load:more';
    version.dispatchEvent(new Event('change'));
    await tick();
    await tick();

    // Versions dropdown now has available:1.0.0
    expect(Array.from(version.options).some((o) => o.value === 'available:1.0.0')).toBe(true);
    // And action button turns into Downgrade
    const action = wordRush.querySelector('.mkt-action');
    expect(action.textContent).toBe('Downgrade');
    expect(action.classList.contains('btn-danger')).toBe(true);
  });

  it('confirms a downgrade and POSTs with the target version', async () => {
    fake = await openMarketplace({
      'GET /admin/api/marketplace/plugins/word-rush/versions': {
        body: {
          id: 'word-rush',
          name: 'Word Rush',
          repo: 'owner/word-rush',
          currentVersion: '1.3.0',
          versions: [
            { version: '1.3.0', tag: 'v1.3.0', sizeBytes: 2_000_000, publishedAt: null, isCurrent: true },
            { version: '1.0.0', tag: 'v1.0.0', sizeBytes: 1_800_000, publishedAt: null, isCurrent: false },
          ],
        },
      },
      '* /admin/api/marketplace/install/word-rush': { status: 202, body: { success: true, jobId: 'j2', detail: 'Downloading.' } },
    });

    const wordRush = card('word-rush');
    const version = wordRush.querySelector('.mkt-version');
    version.value = 'load:more';
    version.dispatchEvent(new Event('change'));
    await tick();
    await tick();

    wordRush.querySelector('.mkt-action').click();
    await tick();

    expect(el('confirm-backdrop').classList.contains('hidden')).toBe(false);
    expect(el('confirm-body').textContent).toContain('v1.2.0');
    expect(el('confirm-body').textContent).toContain('v1.0.0');
    expect(el('confirm-body').textContent).toContain('replaces');

    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.path === '/admin/api/marketplace/install/word-rush');
    expect(post).toBeTruthy();
    expect(post.body.version).toBe('1.0.0');
    expect(post.body.sourceId).toBe('official');
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
    // Inline beside the form, not as a toast: the operator has to read it while correcting the field it
    // is about, and a toast fades. (The element was previously only ever HIDDEN on failure, so the reason
    // reached nobody at all.)
    const error = el('mkt-settings-error');
    expect(error.textContent).toContain('absolute https URL');
    expect(error.classList.contains('hidden')).toBe(false);
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

  const gameCard = () => document.querySelector('#plugins-list .game-card') || document.querySelector('#games-list .game-card');

  it('badges a game the engine is mid-update on, and holds its controls', async () => {
    await openGames({
      'GET /admin/api/games': {
        body: { ...GAMES, games: [{ ...GAMES.games[0], lifecycle: 'updating' }] },
      },
    });

    const card = gameCard();
    expect(card.textContent).toContain('Updating');
    // An availability write racing a directory swap is arbitration the engine shouldn't have to do.
    expect(card.querySelector('.plugin-availability').disabled).toBe(true);
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

    const options = [...gameCard().querySelector('.plugin-availability').options].map((o) => o.value);
    expect(options).toEqual(['available', 'disabled', 'staged']);
  });

  it('leaves an ordinary game alone', async () => {
    await openGames();

    const card = gameCard();
    expect(card.textContent).not.toContain('Updating');
    expect(card.querySelector('.plugin-availability').disabled).toBe(false);
  });

  it('indicates when a package operation is in progress', async () => {
    await openGames({
      'GET /admin/api/games': {
        body: { ...GAMES, games: [{ ...GAMES.games[0], lifecycle: 'draining' }] },
      },
    });

    const card = gameCard();
    expect(card.textContent).toContain('Draining');
    expect(card.textContent).toContain('package operation in progress');
  });
});

describe('scrolling is not arriving', () => {
  it('does not fetch the catalog just because a scroll passed the marketplace section', async () => {
    // updateScrollspy is bound to three scroll sources and used to run the tab-change side effects
    // inline, so one sidebar click — which smooth-scrolls through every section on the way — wiped the
    // log buffer and called refreshCatalog for each tab it crossed. The catalog read reaches the
    // network with a 30-second timeout and is documented as never being on the poll path.
    fake = installFakeFetch(routes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    const catalogCalls = () => fake.calls.filter((c) => c.path === '/admin/api/marketplace/catalog').length;
    const before = catalogCalls();

    // Only the marketplace / plugins card is above the activation line, which is what "scrolled onto it" looks
    // like to updateScrollspy. Every other card is pushed below.
    for (const cardEl of document.querySelectorAll('.setting-card')) {
      const onScreen = cardEl.id === 'setting-games' || cardEl.id === 'setting-marketplace';
      cardEl.getBoundingClientRect = () => ({ top: onScreen ? 10 : 9000, bottom: 9100, height: 100 });
    }
    window.dispatchEvent(new Event('scroll'));
    await tick();

    // The cheap half still happened — the sidebar and the panel title follow the scroll immediately.
    expect(el('panel-title').textContent).toBe('Plugins & Games');
    // The expensive half did not.
    expect(catalogCalls()).toBe(before);
  });
});

describe('the job feed survives a server restart', () => {
  it('drops stale rows when the sequence goes backwards', async () => {
    // The registry is in-memory, so a restart begins again at 1. Holding the old rows meant every real
    // job that followed sorted below them and was sliced away at the view limit, while the cursor —
    // only ever clamped upward — asked for everything after a sequence the new process would not
    // reach for a long time. The log feed already handled this; the job feed did not.
    // Driven through the POLL, not a tab re-entry: entering a tab resets the feed anyway, so a test
    // that switches tabs would pass with the bug still in place.
    vi.useFakeTimers();
    const oldJob = {
      ...runningJob, jobId: 'old', sequence: 900, status: 'succeeded', terminal: true,
      gameId: 'word-rush', gameName: 'Word Rush', phase: 'Updated to 1.3.0.', cancellable: false,
    };
    fake = installFakeFetch(routes({
      'GET /admin/api/packages/jobs': { body: { jobs: [oldJob], lastSequence: 900, active: 0, retained: 1 } },
    }));
    await importAdmin();
    admin.bootstrap();
    await vi.advanceTimersByTimeAsync(1);
    admin.selectTab('marketplace');
    await vi.advanceTimersByTimeAsync(1);

    expect(el('mkt-jobs').textContent).toContain('Word Rush');

    // The restart: the same route now answers from sequence 1 again.
    fake.routes['GET /admin/api/packages/jobs'] =
      { body: { jobs: [{ ...runningJob, jobId: 'fresh', sequence: 1 }], lastSequence: 1, active: 1, retained: 1 } };
    await vi.advanceTimersByTimeAsync(3500);

    expect(el('mkt-jobs').textContent).toContain('Alpha Chain');
    expect(el('mkt-jobs').textContent).not.toContain('Word Rush');
  });
});

describe('combined plugins tile and metadata dialog', () => {
  it('displays installed plugins on top and uninstalled plugins below with required facts', async () => {
    await openMarketplace();

    const cards = [...document.querySelectorAll('#plugins-list .game-card')];
    expect(cards.length).toBeGreaterThanOrEqual(2);

    const wordRush = cards.find((c) => c.dataset.id === 'word-rush');
    const alphaChain = cards.find((c) => c.dataset.id === 'alpha-chain');

    expect(wordRush).toBeTruthy();
    expect(alphaChain).toBeTruthy();

    // Word Rush is installed, so it should be before Alpha Chain (not installed)
    expect(cards.indexOf(wordRush)).toBeLessThan(cards.indexOf(alphaChain));

    // Word Rush card displays all required fields: name, tags, description, status, version, size (disk size), player range, game-id, author
    expect(wordRush.textContent).toContain('Word Rush');
    expect(wordRush.textContent).toContain('party');
    expect(wordRush.textContent).toContain('Fast word game');
    expect(wordRush.textContent).toContain('Update available');
    expect(wordRush.textContent).toContain('v1.2.0');
    expect(wordRush.textContent).toContain('Disk');
    expect(wordRush.textContent).toContain('1000 B');
    expect(wordRush.textContent).toContain('2–8');
    expect(wordRush.textContent).toContain('word-rush');
    expect(wordRush.textContent).toContain('Someone');

    // Controls on installed card: version select, status select, primary button, export button, delete button, 3-dots button
    expect(wordRush.querySelector('.plugin-version')).not.toBeNull();
    expect(wordRush.querySelector('.plugin-availability')).not.toBeNull();
    expect(wordRush.querySelector('.plugin-action')).not.toBeNull();
    expect(wordRush.querySelector('.plugin-export')).not.toBeNull();
    expect(wordRush.querySelector('.plugin-delete')).not.toBeNull();
    expect(wordRush.querySelector('.plugin-details-btn')).not.toBeNull();

    // Controls on not-installed card (Alpha Chain): version select, primary install button, 3-dots button
    // Hidden / omitted: status select, export button, delete button
    expect(alphaChain.querySelector('.plugin-version')).not.toBeNull();
    expect(alphaChain.querySelector('.plugin-action')).not.toBeNull();
    expect(alphaChain.querySelector('.plugin-details-btn')).not.toBeNull();
    expect(alphaChain.querySelector('.plugin-availability')).toBeNull();
    expect(alphaChain.querySelector('.plugin-export')).toBeNull();
    expect(alphaChain.querySelector('.plugin-delete')).toBeNull();
  });

  it('filters by source, status, and search query using unified filter controls', async () => {
    await openMarketplace({
      'GET /admin/api/games': {
        body: {
          ...GAMES,
          games: [
            ...GAMES.games,
            {
              id: 'tictactoe', name: 'Tic-Tac-Toe', root: 'games', version: '1.0.0',
              availability: 'available', diskBytes: 12000, directoryBytes: 8000, compressedBytes: 4000,
              packageBytes: 0, activeLobbies: 1, activePlayers: 2, deletable: true,
            },
          ],
        },
      },
    });

    const qInput = el('plugins-filter-q');
    const sourceSelect = el('plugins-filter-source');
    const statusSelect = el('plugins-filter-status');

    expect(sourceSelect).not.toBeNull();
    expect(statusSelect).not.toBeNull();

    // 1. Filter by source: Games Folder
    sourceSelect.value = 'games';
    sourceSelect.dispatchEvent(new Event('change'));
    let visible = [...document.querySelectorAll('#plugins-list .game-card')];
    expect(visible.map((c) => c.dataset.id)).toEqual(['tictactoe']);

    // 2. Filter by source: Official Marketplace
    sourceSelect.value = 'official';
    sourceSelect.dispatchEvent(new Event('change'));
    visible = [...document.querySelectorAll('#plugins-list .game-card')];
    expect(visible.map((c) => c.dataset.id)).toEqual(['word-rush', 'alpha-chain']);

    // Reset source
    sourceSelect.value = '';
    sourceSelect.dispatchEvent(new Event('change'));

    // 3. Filter by status: Not Installed
    statusSelect.value = 'notInstalled';
    statusSelect.dispatchEvent(new Event('change'));
    visible = [...document.querySelectorAll('#plugins-list .game-card')];
    expect(visible.map((c) => c.dataset.id)).toEqual(['alpha-chain']);

    // 4. Filter by status: Installed
    statusSelect.value = 'installed';
    statusSelect.dispatchEvent(new Event('change'));
    visible = [...document.querySelectorAll('#plugins-list .game-card')];
    expect(visible.map((c) => c.dataset.id)).toEqual(['tictactoe', 'word-rush']);

    // Reset status
    statusSelect.value = '';
    statusSelect.dispatchEvent(new Event('change'));

    // 5. Search query
    qInput.value = 'Tic';
    qInput.dispatchEvent(new Event('input'));
    visible = [...document.querySelectorAll('#plugins-list .game-card')];
    expect(visible.map((c) => c.dataset.id)).toEqual(['tictactoe']);
  });

  it('opens 3-dots full metadata popup dialog and renders details and footer actions', async () => {
    await openMarketplace();

    const wordRush = card('word-rush');
    const dotsBtn = wordRush.querySelector('.plugin-details-btn');
    expect(dotsBtn).not.toBeNull();

    const modal = el('plugin-details-backdrop');
    expect(modal.classList.contains('hidden')).toBe(true);

    dotsBtn.click();
    expect(modal.classList.contains('hidden')).toBe(false);

    // Title
    expect(el('plugin-details-title').textContent).toContain('Word Rush');

    // Body content sections
    const body = el('plugin-details-body');
    expect(body.textContent).toContain('Overview & Identity');
    expect(body.textContent).toContain('Description & Tags');
    expect(body.textContent).toContain('Gameplay & Runtime');
    expect(body.textContent).toContain('Storage & Disk Breakdown');
    expect(body.textContent).toContain('Retained Version Backups');

    // Metadata specifics
    expect(body.textContent).toContain('word-rush');
    expect(body.textContent).toContain('Someone');
    expect(body.textContent).toContain('MIT');
    expect(body.textContent).toContain('everyone');
    expect(body.textContent).toContain('2–8');
    expect(body.textContent).toContain('Fast word game');
    expect(body.textContent).toContain('party');

    // Action buttons in modal footer
    const actions = el('plugin-details-actions');
    expect(actions.querySelector('.mkt-version')).not.toBeNull();
    expect(actions.querySelector('.plugin-availability')).not.toBeNull();
    expect(actions.querySelector('.mkt-action')).not.toBeNull();
    expect(actions.querySelector('.mkt-export')).not.toBeNull();
    expect(actions.querySelector('.mkt-uninstall')).not.toBeNull();
    expect(el('plugin-details-close')).not.toBeNull();

    // Close button dismisses dialog
    el('plugin-details-close').click();
    expect(modal.classList.contains('hidden')).toBe(true);

    // Reopen and test close with Escape key
    dotsBtn.click();
    expect(modal.classList.contains('hidden')).toBe(false);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(modal.classList.contains('hidden')).toBe(true);

    // Reopen and test close with X button
    dotsBtn.click();
    expect(modal.classList.contains('hidden')).toBe(false);
    el('plugin-details-close-x').click();
    expect(modal.classList.contains('hidden')).toBe(true);
  });

  it('fetches and renders repository releases in the details dialog', async () => {
    fake = await openMarketplace({
      'GET /admin/api/marketplace/plugins/word-rush/versions': {
        body: {
          id: 'word-rush',
          name: 'Word Rush',
          repo: 'owner/word-rush',
          currentVersion: '1.3.0',
          versions: [
            { version: '1.3.0', tag: 'v1.3.0', sizeBytes: 2_000_000, publishedAt: '2026-09-02T15:00:00Z', isCurrent: true },
            { version: '1.0.0', tag: 'v1.0.0', sizeBytes: 1_800_000, publishedAt: '2026-08-01T10:00:00Z', isCurrent: false },
          ],
        },
      },
    });

    const wordRush = card('word-rush');
    const dotsBtn = wordRush.querySelector('.plugin-details-btn');
    dotsBtn.click();
    await tick();
    await tick();

    const body = el('plugin-details-body');
    expect(body.textContent).toContain('Repository Releases');
    expect(body.textContent).toContain('v1.3.0');
    expect(body.textContent).toContain('v1.0.0');
    expect(body.textContent).toContain('Installed');
    expect(body.textContent).toContain('Downgrade');

    el('plugin-details-close').click();
  });
});

describe('plugin stability & dropdown state preservation', () => {
  it('does not destroy a focused plugin card or close its select on subsequent renderPlugins', async () => {
    await openMarketplace();

    const wordRush = card('word-rush');
    const versionSelect = wordRush.querySelector('.mkt-version');
    versionSelect.focus();
    expect(document.activeElement).toBe(versionSelect);

    // Trigger renderPlugins while the dropdown is focused
    admin.renderPlugins();

    // The focused card is preserved in-place; the select element is not destroyed or blurred
    const currentWordRush = card('word-rush');
    expect(currentWordRush).toBe(wordRush);
    expect(document.activeElement).toBe(versionSelect);
  });

  it('remembers chosen version and discovered releases across renderPlugins', async () => {
    fake = await openMarketplace({
      'GET /admin/api/marketplace/plugins/word-rush/versions': {
        body: {
          id: 'word-rush',
          name: 'Word Rush',
          repo: 'owner/word-rush',
          currentVersion: '1.3.0',
          versions: [
            { version: '1.3.0', tag: 'v1.3.0', sizeBytes: 2_000_000, publishedAt: null, isCurrent: true },
            { version: '1.0.0', tag: 'v1.0.0', sizeBytes: 1_800_000, publishedAt: null, isCurrent: false },
          ],
        },
      },
    });

    const wordRush = card('word-rush');
    const versionSelect = wordRush.querySelector('.mkt-version');
    versionSelect.value = 'load:more';
    versionSelect.dispatchEvent(new Event('change'));
    await tick();
    await tick();

    // Dropdown now has available:1.0.0 selected (first older release)
    expect(versionSelect.value).toBe('available:1.0.0');

    // Manually trigger renderPlugins (as would happen on search filter input, tab refresh, etc.)
    admin.renderPlugins();

    // The newly rendered card must still remember the discovered versions and user selection!
    const updatedCard = card('word-rush');
    const updatedSelect = updatedCard.querySelector('.mkt-version');
    expect(updatedSelect.value).toBe('available:1.0.0');
    expect(Array.from(updatedSelect.options).some((o) => o.value === 'available:1.0.0')).toBe(true);
    expect(updatedCard.querySelector('.mkt-action').textContent).toBe('Downgrade');
  });
});

