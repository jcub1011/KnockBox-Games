// @vitest-environment jsdom
//
// The admin portal's DOM orchestration: which view the auth state selects, the tab router the dead
// data-tab attributes were always for, per-tab polling, and the confirm-then-POST path every destructive
// action goes through.
//
// admin.js is side-effecting on import in the same way shell.js is, so each test resets modules, injects
// the REAL admin/index.html (so element ids can't drift from production markup), stubs fetch, and then
// imports the module fresh. Assertions read rendered DOM text and the recorded fetch calls.
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { installFakeFetch, loadAdminDom, tick } from './helpers.js';

const el = (id) => document.getElementById(id);

let admin;

async function importAdmin() {
  admin = await import('../admin/admin.js');
  return admin;
}

// The payloads the four endpoints return, with just enough shape to render.
const STATUS = {
  uptime: '0d 1h 2m 3s',
  activeLobbies: 2,
  registeredGames: 4,
  workingSetMb: 61,
  managedHeapMb: 3,
  hostTime: '2026-08-12T20:00:00.000Z',
  connectedPlayers: 5,
  gameSockets: 3,
  authorityLobbies: 1,
  maintenanceMode: false,
  maintenanceMessage: null,
  cpuPercentLifetime: 0.42,
  cpuSecondsTotal: 12.5,
  processorCount: 16,
  gen0Collections: 7,
  gen1Collections: 2,
  gen2Collections: 1,
  scanError: null,
  settingsError: null,
  diagnostics: [],
};

const LOBBIES = {
  staleAfterMinutes: 30,
  hostTime: '2026-08-12T20:00:00.000Z',
  lobbies: [
    {
      code: 'AB12', gameId: 'tictactoe', gameName: 'Tic-Tac-Toe', gameVersion: null,
      players: 2, maxPlayers: 2, disconnected: 0, hostId: 'p1', open: true, serverAuthority: true,
      createdAt: '2026-08-12T19:00:00.000Z', ageSeconds: 3600, idleSeconds: 12, status: 'waiting',
      members: [
        { playerId: 'p1', displayName: 'Ada', isHost: true, connected: true, disconnectedSeconds: 0 },
        { playerId: 'p2', displayName: 'Grace', isHost: false, connected: false, disconnectedSeconds: 20 },
      ],
    },
    {
      code: 'CD34', gameId: 'word-rush', gameName: 'Word Rush', gameVersion: '1.2.0',
      players: 1, maxPlayers: 8, disconnected: 0, hostId: 'p3', open: false, serverAuthority: false,
      createdAt: '2026-08-12T19:30:00.000Z', ageSeconds: 1800, idleSeconds: 4000, status: 'stale',
      members: [{ playerId: 'p3', displayName: 'Linus', isHost: true, connected: true, disconnectedSeconds: 0 }],
    },
  ],
};

const GAMES = {
  gamesRoot: '/srv/games',
  packagesRoot: '/app/games-unpacked',
  scanError: null,
  diskMeasuredAt: '2026-08-12T20:00:00.000Z',
  compressedCacheBytes: 2_800_000,
  logsBytes: 500_000,
  games: [
    {
      id: 'tictactoe', name: 'Tic-Tac-Toe', version: null, availability: 'available', maxPlayers: 2,
      serverAuthority: false, directory: '/srv/games/tictactoe', root: 'games', packageBacked: false,
      diskBytes: 12_685, directoryBytes: 7_756, compressedBytes: 4_929, packageBytes: 0,
      activeLobbies: 1, activePlayers: 2, deletable: true, deleteBlockedReason: null,
      backupBytes: 0, packageRoot: null, lifecycle: 'ready', updatePolicy: 'manual', pendingJobId: null,
    },
    {
      id: 'word-rush', name: 'Word Rush', version: '1.2.0', availability: 'disabled', maxPlayers: 8,
      serverAuthority: true, directory: '/app/games-unpacked/word-rush', root: 'packages', packageBacked: true,
      diskBytes: 1_048_576, directoryBytes: 800_000, compressedBytes: 148_576, packageBytes: 100_000,
      activeLobbies: 0, activePlayers: 0, deletable: false,
      deleteBlockedReason: "'/srv/games' is not writable by the server (read-only mount).",
      backupBytes: 0, packageRoot: 'managed', lifecycle: 'ready', updatePolicy: 'manual', pendingJobId: null,
    },
  ],
  managedRoot: '/app/games-managed',
  managedRootBytes: 100_000,
};

const CATALOG = {
  enabled: true,
  appVersion: '1.4.0',
  fetchedAt: '2026-08-12T20:00:00.000Z',
  maxSources: 8,
  backupRetention: 1,
  maxUploadBytes: 536_870_912,
  canInstall: true,
  installBlockedReason: null,
  managedRoot: '/app/games-managed',
  jobsLastSequence: 0,
  jobs: [],
  sources: [
    {
      id: 'official', name: 'Official KnockBox marketplace', catalogUrl: 'https://example/CATALOG.json',
      downloadBaseUrl: 'https://github.com', enabled: true, builtIn: true, entries: 2, error: null,
    },
  ],
  entries: [
    {
      id: 'word-rush', name: 'Word Rush', description: 'Fast word game', author: 'Someone',
      tags: ['party'], availableVersion: '1.3.0', installedVersion: '1.2.0', status: 'updateAvailable',
      reason: null, sizeBytes: 2_000_000, publishedAt: '2026-08-01T00:00:00.000Z',
      minAppVersion: null, maxAppVersion: null, sourceId: 'official',
      sourceName: 'Official KnockBox marketplace', shadowedBy: null, managed: true, installed: true,
      activeLobbies: 0, pendingJobId: null, updatePolicy: 'manual',
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

const JOBS = { jobs: [], lastSequence: 0, active: 0, retained: 0 };

const LOGS = {
  entries: [
    {
      seq: 1, time: '2026-08-12T20:00:00.000Z', level: 'Information',
      category: 'KnockBox.Server.Games.GameCatalog', message: 'Game catalog ready: 4 game(s)', exception: null,
    },
    {
      seq: 2, time: '2026-08-12T20:00:01.000Z', level: 'Warning',
      category: 'KnockBox.GameLog', message: 'Skipping game code-word', exception: null,
    },
  ],
  lastSequence: 2,
  totalWritten: 41,
  buffered: 2,
};

const METRICS = {
  games: [{
    gameId: 'tictactoe', framesIn: 100, framesOut: 200, bytesIn: 5000, bytesOut: 10_000,
    framesDropped: 0, fanOut: 2, lobbies: 1, players: 2,
    socketFramesSent: 200, socketBytesSent: 10_000, socketFramesDropped: 0,
    authorityCalls: 0, authorityCpuSeconds: 0, authorityAverageMs: 0, authorityMaxMs: 0, authorityErrors: 0,
  }],
  controlSockets: 5,
  gameSockets: 3,
  outboundFramesSent: 500,
  outboundBytesSent: 20_000,
  outboundFramesDropped: 0,
  trackedRateLimitIps: 0,
  hostTime: '2026-08-12T20:00:00.000Z',
};

function historySample(seq, seconds, fields = {}) {
  return {
    sequence: seq, at: new Date(Date.UTC(2026, 7, 12, 20, 0, seconds)).toISOString(),
    cpuSeconds: seconds * 0.2, workingSetMb: 60 + seq, managedHeapMb: 3,
    lobbies: 2, players: 5, gameSockets: 3, authorityLobbies: 0, games: [], ...fields,
  };
}

const HISTORY = {
  enabled: true,
  samples: [historySample(1, 0), historySample(2, 15), historySample(3, 30)],
  lastSequence: 3, retained: 3, capacity: 240, sampleSeconds: 15, processorCount: 8,
};

// The happy path: configured, authenticated, every read endpoint answering.
function authedRoutes(overrides = {}) {
  return {
    'GET /admin/api/auth/status': { body: { configured: true, authenticated: true } },
    'GET /admin/api/system/status': { body: STATUS },
    'GET /admin/api/metrics': { body: METRICS },
    'GET /admin/api/lobbies': { body: LOBBIES },
    'GET /admin/api/games': { body: GAMES },
    'GET /admin/api/logs': { body: LOGS },
    'GET /admin/api/logs/files': { body: { files: [{ name: 'knockbox-20260812.log', bytes: 254_000, modified: '2026-08-12T20:00:00.000Z' }], logsRoot: '/logs', error: null } },
    'GET /admin/api/marketplace/catalog': { body: CATALOG },
    'GET /admin/api/packages/jobs': { body: JOBS },
    'GET /admin/api/metrics/history': { body: HISTORY },
    ...overrides,
  };
}

let fake;

beforeEach(() => {
  vi.resetModules();
  loadAdminDom();
  // Each test starts at the default tab unless it says otherwise.
  window.location.hash = '';
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('auth state selects the view', () => {
  it('shows the setup form on an unclaimed server', async () => {
    fake = installFakeFetch({ 'GET /admin/api/auth/status': { body: { configured: false, authenticated: false } } });
    await importAdmin();
    admin.bootstrap();
    await tick();

    expect(el('setup-view').classList.contains('hidden')).toBe(false);
    expect(el('login-view').classList.contains('hidden')).toBe(true);
    expect(el('dashboard-view').classList.contains('hidden')).toBe(true);
    // Nothing to log out of yet.
    expect(el('logout-btn').classList.contains('hidden')).toBe(true);
  });

  it('shows the login form on a claimed server with no session', async () => {
    fake = installFakeFetch({ 'GET /admin/api/auth/status': { body: { configured: true, authenticated: false } } });
    await importAdmin();
    admin.bootstrap();
    await tick();

    expect(el('login-view').classList.contains('hidden')).toBe(false);
    expect(el('logout-btn').classList.contains('hidden')).toBe(true);
  });

  it('shows the dashboard once authenticated', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    expect(el('dashboard-view').classList.contains('hidden')).toBe(false);
    expect(el('logout-btn').classList.contains('hidden')).toBe(false);
  });

  it('reports an unreachable server on the status pill instead of a blank page', async () => {
    fake = installFakeFetch({ 'GET /admin/api/auth/status': { status: 500, body: {} } });
    await importAdmin();
    admin.bootstrap();
    await tick();

    expect(el('server-status-text').textContent).toMatch(/unreachable/i);
    expect(el('server-status-pill').hidden).toBe(false);
  });

  it('keeps the status pill out of the header while nothing is wrong', async () => {
    // It is a fault indicator, not a heartbeat: a pill reading "Admin Port Active" on a page the admin
    // port itself served can only ever be true, so it told an operator nothing.
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    expect(el('server-status-pill').hidden).toBe(true);
  });
});

describe('overview', () => {
  it('renders the platform counters from system status', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();

    expect(el('metric-uptime').textContent).toBe('0d 1h 2m 3s');
    expect(el('metric-lobbies').textContent).toBe('2');
    expect(el('metric-players').textContent).toBe('5');
    expect(el('metric-games').textContent).toBe('4');
    expect(el('metric-memory').textContent).toBe('61 MB');
    expect(el('metric-sockets').textContent).toContain('3');
    expect(el('metric-lobbies-sub').textContent).toContain('1 server-authority');
  });

  it('shows a dash for CPU on the first poll, because a rate needs two samples', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();

    expect(el('metric-cpu').textContent).toBe('--');
    // The lifetime average IS available immediately, so it fills the sub-line.
    expect(el('metric-cpu-sub').textContent).toContain('0.42% lifetime');
  });

  it('surfaces deployment diagnostics rather than leaving them on the player site', async () => {
    fake = installFakeFetch(authedRoutes({
      'GET /admin/api/system/status': {
        body: {
          ...STATUS,
          diagnostics: [{ title: 'Games folder is not accessible', detail: 'permission denied', blocking: true }],
        },
      },
    }));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();

    const banner = el('diagnostics-banner');
    expect(banner.classList.contains('hidden')).toBe(false);
    expect(banner.textContent).toContain('Games folder is not accessible');
    expect(banner.textContent).toContain('permission denied');
    expect(banner.querySelector('.diagnostic-blocking')).not.toBeNull();
  });

  it('folds a settings-file read failure into the same banner', async () => {
    fake = installFakeFetch(authedRoutes({
      'GET /admin/api/system/status': { body: { ...STATUS, settingsError: 'settings.json could not be read' } },
    }));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();

    // Otherwise "my disabled game came back" is the only symptom the operator ever sees.
    expect(el('diagnostics-banner').textContent).toContain('settings.json could not be read');
  });

  it('reflects maintenance mode on the toggle', async () => {
    fake = installFakeFetch(authedRoutes({
      'GET /admin/api/system/status': { body: { ...STATUS, maintenanceMode: true, maintenanceMessage: 'Back at 09:00.' } },
    }));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();

    expect(el('maintenance-badge').textContent).toBe('On');
    expect(el('maintenance-toggle').textContent).toBe('Turn Off');
    expect(el('maintenance-message').value).toBe('Back at 09:00.');
  });

  it('renders the per-game relay table', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();

    const row = el('metrics-body').querySelector('tr');
    expect(row.textContent).toContain('tictactoe');
    expect(row.textContent).toContain('2.00x'); // fan-out: one broadcast, two recipients
    expect(el('metrics-empty').classList.contains('hidden')).toBe(true);
  });
});

describe('top-bar tab navigation & cross-tab deep linking', () => {
  it('renders all 4 top-bar tabs and defaults to monitoring', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    const topTabs = document.querySelectorAll('.top-tab-btn');
    expect(topTabs.length).toBe(4);
    expect(document.querySelector('.top-tab-btn[data-tab="monitoring"]').classList.contains('active')).toBe(true);
    expect(document.querySelector('.top-tab-btn[data-tab="logs"]').classList.contains('active')).toBe(false);
    expect(document.querySelector('.top-tab-btn[data-tab="plugins"]').classList.contains('active')).toBe(false);
    expect(document.querySelector('.top-tab-btn[data-tab="settings"]').classList.contains('active')).toBe(false);

    expect(el('tab-panel-monitoring').classList.contains('hidden')).toBe(false);
    expect(el('tab-panel-logs').classList.contains('hidden')).toBe(true);
    expect(el('tab-panel-plugins').classList.contains('hidden')).toBe(true);
    expect(el('tab-panel-settings').classList.contains('hidden')).toBe(true);
  });

  it('switches tab panels when top-bar buttons are clicked', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    // Click Logs tab
    document.querySelector('.top-tab-btn[data-tab="logs"]').click();
    await tick();
    expect(document.querySelector('.top-tab-btn[data-tab="logs"]').classList.contains('active')).toBe(true);
    expect(document.querySelector('.top-tab-btn[data-tab="monitoring"]').classList.contains('active')).toBe(false);
    expect(el('tab-panel-logs').classList.contains('hidden')).toBe(false);
    expect(el('tab-panel-monitoring').classList.contains('hidden')).toBe(true);

    // Click Plugins tab
    document.querySelector('.top-tab-btn[data-tab="plugins"]').click();
    await tick();
    expect(document.querySelector('.top-tab-btn[data-tab="plugins"]').classList.contains('active')).toBe(true);
    expect(el('tab-panel-plugins').classList.contains('hidden')).toBe(false);
    expect(el('tab-panel-logs').classList.contains('hidden')).toBe(true);

    // Click Settings tab
    document.querySelector('.top-tab-btn[data-tab="settings"]').click();
    await tick();
    expect(document.querySelector('.top-tab-btn[data-tab="settings"]').classList.contains('active')).toBe(true);
    expect(el('tab-panel-settings').classList.contains('hidden')).toBe(false);
    expect(el('tab-panel-plugins').classList.contains('hidden')).toBe(true);
  });

  it('cross-tab links from Plugins to Update Schedule in Settings with pulse highlight', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    // Go to Plugins tab first
    admin.selectTopTab('plugins');
    await tick();
    expect(el('tab-panel-plugins').classList.contains('hidden')).toBe(false);
    expect(el('tab-panel-settings').classList.contains('hidden')).toBe(true);

    const scheduleCard = el('setting-schedule');
    scheduleCard.scrollIntoView = vi.fn();

    // Click "Manage Update Schedule in Settings →" cross-tab jump link
    const jumpLink = el('goto-schedule-link');
    jumpLink.click();
    await tick();

    // Should now be on the Settings top tab
    expect(document.querySelector('.top-tab-btn[data-tab="settings"]').classList.contains('active')).toBe(true);
    expect(el('tab-panel-settings').classList.contains('hidden')).toBe(false);
    expect(el('tab-panel-plugins').classList.contains('hidden')).toBe(true);

    // Schedule setting should be active and scrolled
    expect(scheduleCard.scrollIntoView).toHaveBeenCalledWith({ behavior: 'smooth', block: 'start' });
    expect(document.querySelector('.tree-item[data-setting-id="setting-schedule"]').classList.contains('active')).toBe(true);
    expect(window.location.hash).toBe('#schedule');
  });

  it('honours top tab and setting fragments on load', async () => {
    window.location.hash = '#plugins';
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    expect(document.querySelector('.top-tab-btn[data-tab="plugins"]').classList.contains('active')).toBe(true);
    expect(el('tab-panel-plugins').classList.contains('hidden')).toBe(false);

    // Deep setting fragment
    admin.selectSetting('setting-webhooks');
    await tick();
    expect(document.querySelector('.top-tab-btn[data-tab="settings"]').classList.contains('active')).toBe(true);
    expect(el('tab-panel-settings').classList.contains('hidden')).toBe(false);
    expect(document.querySelector('.tree-item[data-setting-id="setting-webhooks"]').classList.contains('active')).toBe(true);
  });
});

describe('settings sidebar tree navigation & search', () => {
  it('toggles tree groups on group header click in Settings tab', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    admin.selectTopTab('settings');
    await tick();

    const group = document.querySelector('.tree-group[data-group-id="platform"]');
    const header = group.querySelector('.tree-group-header');

    expect(header.getAttribute('aria-expanded')).toBe('true');
    expect(group.classList.contains('group-collapsed')).toBe(false);

    header.click();
    expect(header.getAttribute('aria-expanded')).toBe('false');
    expect(group.classList.contains('group-collapsed')).toBe(true);

    header.click();
    expect(header.getAttribute('aria-expanded')).toBe('true');
    expect(group.classList.contains('group-collapsed')).toBe(false);
  });

  it('filters settings and sidebar items on search input and clears with X button', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    admin.selectTopTab('settings');
    await tick();

    const searchInput = el('settings-search-input');
    const clearBtn = el('settings-search-clear');
    expect(clearBtn.classList.contains('hidden')).toBe(true);

    // Search for "limits"
    searchInput.value = 'limits';
    searchInput.dispatchEvent(new Event('input'));
    await tick();

    expect(clearBtn.classList.contains('hidden')).toBe(false);
    expect(el('setting-limits').classList.contains('search-hidden')).toBe(false);
    expect(el('setting-maintenance').classList.contains('search-hidden')).toBe(true);
    expect(document.querySelector('.tree-item[data-setting-id="setting-limits"]').classList.contains('search-hidden')).toBe(false);
    expect(document.querySelector('.tree-item[data-setting-id="setting-maintenance"]').classList.contains('search-hidden')).toBe(true);
    expect(el('settings-search-empty').classList.contains('hidden')).toBe(true);

    // Search for nonexistent term
    searchInput.value = 'nonexistent_setting_query_123';
    searchInput.dispatchEvent(new Event('input'));
    await tick();

    expect(el('settings-search-empty').classList.contains('hidden')).toBe(false);

    // Click clear button
    clearBtn.click();
    await tick();

    expect(searchInput.value).toBe('');
    expect(clearBtn.classList.contains('hidden')).toBe(true);
    expect(el('setting-maintenance').classList.contains('search-hidden')).toBe(false);
    expect(el('setting-limits').classList.contains('search-hidden')).toBe(false);
    expect(el('settings-search-empty').classList.contains('hidden')).toBe(true);
  });

  it('has setting cards for every registered setting', async () => {
    const { ALL_SETTINGS } = await import('../admin/admin-core.js');

    for (const setting of ALL_SETTINGS) {
      expect(document.querySelector(`.setting-card[data-setting-id="${setting.id}"]`)).not.toBeNull();
    }
  });

  it('updates active setting and centers sidebar item via scrollspy in settings tab', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    admin.selectTopTab('settings');
    await tick();

    const tree = el('sidebar-tree');
    tree.scrollTo = vi.fn();

    // Default all cards to be below the activation line in jsdom
    for (const card of document.querySelectorAll('#tab-panel-settings .setting-card')) {
      card.getBoundingClientRect = () => ({ top: 1000, bottom: 1400, height: 400 });
    }

    el('setting-maintenance').getBoundingClientRect = () => ({ top: -600, bottom: -200, height: 400 });
    el('setting-schedule').getBoundingClientRect = () => ({ top: 80, bottom: 480, height: 400 });

    admin.updateScrollspy();

    expect(document.querySelector('.tree-item[data-setting-id="setting-schedule"]').classList.contains('active')).toBe(true);
    expect(document.querySelector('.tree-item[data-setting-id="setting-maintenance"]').classList.contains('active')).toBe(false);
    expect(el('panel-title').textContent).toBe('Update Schedule');
    expect(tree.scrollTo).toHaveBeenCalled();
  });

  it('manages scroll up/down indicator buttons via disabled property without layout shifts', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();

    const upBtn = el('sidebar-scroll-up');
    const downBtn = el('sidebar-scroll-down');
    const tree = el('sidebar-tree');

    // In expanded mode, indicators are disabled
    admin.updateSidebarScrollIndicators();
    expect(upBtn.disabled).toBe(true);
    expect(downBtn.disabled).toBe(true);

    // Collapse sidebar
    admin.setSidebarCollapsed(true, { persist: false });

    // Mock overflow with scroll at top
    Object.defineProperty(tree, 'scrollTop', { value: 0, writable: true });
    Object.defineProperty(tree, 'clientHeight', { value: 200, writable: true });
    Object.defineProperty(tree, 'scrollHeight', { value: 600, writable: true });

    admin.updateSidebarScrollIndicators();
    expect(upBtn.disabled).toBe(true);
    expect(downBtn.disabled).toBe(false);

    // Mock scroll in middle
    tree.scrollTop = 150;
    admin.updateSidebarScrollIndicators();
    expect(upBtn.disabled).toBe(false);
    expect(downBtn.disabled).toBe(false);

    // Mock scroll to bottom
    tree.scrollTop = 400;
    admin.updateSidebarScrollIndicators();
    expect(upBtn.disabled).toBe(false);
    expect(downBtn.disabled).toBe(true);

    // When in the middle, both are enabled and clicking invokes scrollBy
    tree.scrollTop = 150;
    admin.updateSidebarScrollIndicators();
    tree.scrollBy = vi.fn();

    upBtn.click();
    expect(tree.scrollBy).toHaveBeenCalledWith({ top: -80, behavior: 'smooth' });

    downBtn.click();
    expect(tree.scrollBy).toHaveBeenCalledWith({ top: 80, behavior: 'smooth' });
  });
});

describe('lobby directory', () => {
  async function openLobbies(overrides) {
    fake = installFakeFetch(authedRoutes(overrides));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    admin.selectTab('lobbies');
    await tick();
    await tick();
  }

  it('renders a row per lobby with its members', async () => {
    await openLobbies();
    const rows = el('lobbies-body').querySelectorAll('tr');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('AB12');
    expect(rows[0].textContent).toContain('Tic-Tac-Toe');
    expect(rows[0].textContent).toContain('Ada (owner)');
    expect(rows[0].textContent).toContain('Grace');
    // A member inside the reconnect grace window is marked rather than hidden — they are still a member.
    expect(rows[0].querySelector('.member-dropped')).not.toBeNull();
  });

  it('badges a stale lobby differently from a healthy one', async () => {
    await openLobbies();
    const rows = el('lobbies-body').querySelectorAll('tr');
    expect(rows[0].querySelector('.badge-ok').textContent).toBe('waiting');
    expect(rows[1].querySelector('.badge-warning').textContent).toBe('stale');
  });

  it('filters client-side without another request', async () => {
    await openLobbies();
    const before = fake.calls.length;

    el('lobby-filter-code').value = 'cd';
    el('lobby-filter-code').dispatchEvent(new Event('input', { bubbles: true }));

    const rows = el('lobbies-body').querySelectorAll('tr');
    expect(rows).toHaveLength(1);
    expect(rows[0].textContent).toContain('CD34');
    // Typing in a filter box must not cost a round trip.
    expect(fake.calls.length).toBe(before);
  });

  it('explains an empty filter result differently from an empty server', async () => {
    await openLobbies();
    el('lobby-filter-code').value = 'zzzz';
    el('lobby-filter-code').dispatchEvent(new Event('input', { bubbles: true }));

    expect(el('lobbies-empty').classList.contains('hidden')).toBe(false);
    expect(el('lobbies-empty').textContent).toMatch(/match/i);
  });

  it('asks for confirmation before closing a lobby, and does nothing if cancelled', async () => {
    await openLobbies();
    el('lobbies-body').querySelector('tr .btn-danger').click();

    expect(el('confirm-backdrop').classList.contains('hidden')).toBe(false);
    expect(el('confirm-body').textContent).toContain('AB12');

    el('confirm-cancel').click();
    await tick();

    expect(fake.calls.some((c) => c.method === 'POST')).toBe(false);
  });

  it('POSTs the close once confirmed', async () => {
    await openLobbies({ 'POST /admin/api/lobbies/AB12/close': { body: { success: true, affected: 1 } } });
    el('lobbies-body').querySelector('tr .btn-danger').click();
    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST');
    expect(post.path).toBe('/admin/api/lobbies/AB12/close');
    // The server's mutation guard requires a JSON content type.
    expect(post.init.headers['Content-Type']).toBe('application/json');
  });

  it('kicks a single member without closing the lobby', async () => {
    await openLobbies({ 'POST /admin/api/lobbies/AB12/kick': { body: { success: true, affected: 1 } } });
    el('lobbies-body').querySelector('tr .chip-action').click();
    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST');
    expect(post.path).toBe('/admin/api/lobbies/AB12/kick');
    expect(post.body).toEqual({ playerId: 'p1' });
  });

  it('reports a failed action as an error toast', async () => {
    await openLobbies({ 'POST /admin/api/lobbies/AB12/close': { status: 404, body: { success: false, error: 'No active lobby with code AB12.' } } });
    el('lobbies-body').querySelector('tr .btn-danger').click();
    el('confirm-ok').click();
    await tick();
    await tick();

    const toast = el('toast-host').querySelector('.toast-error');
    expect(toast).not.toBeNull();
    expect(toast.textContent).toContain('No active lobby');
  });
});

describe('game catalog', () => {
  async function openGames(overrides) {
    fake = installFakeFetch(authedRoutes(overrides));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    admin.selectTab('games');
    await tick();
    await tick();
  }

  it('renders a card per game with its disk breakdown and live counts', async () => {
    await openGames();
    const cards = el('games-list').querySelectorAll('.game-card');
    expect(cards).toHaveLength(2);
    expect(cards[0].textContent).toContain('Tic-Tac-Toe');
    expect(cards[0].textContent).toContain('12 KB');   // total
    expect(cards[0].textContent).toContain('7.6 KB');  // files
    expect(cards[0].textContent).toContain('1 lobby/lobbies');
  });

  it('shows the availability each game is actually in', async () => {
    await openGames();
    const selects = el('games-list').querySelectorAll('select');
    expect(selects[0].value).toBe('available');
    expect(selects[1].value).toBe('disabled');
  });

  it('disables Delete and says why when the deployment forbids it', async () => {
    await openGames();
    const wordRush = el('games-list').querySelectorAll('.game-card')[1];
    const remove = [...wordRush.querySelectorAll('button')].find((b) => b.textContent === 'Delete');

    // Offering a button that always fails on a read-only games mount is worse than not offering it.
    expect(remove.disabled).toBe(true);
    expect(wordRush.textContent).toContain('not writable');
    expect(wordRush.textContent).toMatch(/disable the game instead/i);
  });

  it('confirms before hiding a game that has players in it, and says what survives', async () => {
    await openGames({ 'POST /admin/api/games/tictactoe/availability': { body: { success: true } } });
    const select = el('games-list').querySelector('select');   // tictactoe, 1 running lobby
    select.value = 'staged';
    select.dispatchEvent(new Event('change', { bubbles: true }));

    expect(el('confirm-backdrop').classList.contains('hidden')).toBe(false);
    // The nuance that trips operators up: hiding a game does NOT end the sessions already playing it.
    expect(el('confirm-body').textContent).toMatch(/keep playing until they finish/i);

    el('confirm-ok').click();
    await tick();
    await tick();

    const post = fake.calls.find((c) => c.method === 'POST');
    expect(post.path).toBe('/admin/api/games/tictactoe/availability');
    expect(post.body).toEqual({ state: 'staged' });
  });

  it('changes a game with nobody playing it without stopping to ask', async () => {
    await openGames({ 'POST /admin/api/games/word-rush/availability': { body: { success: true } } });
    const select = el('games-list').querySelectorAll('select')[1];  // word-rush, 0 running lobbies
    select.value = 'available';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    await tick();
    await tick();

    // Nothing is at stake, so a confirmation would just be a click to dismiss.
    expect(el('confirm-backdrop').classList.contains('hidden')).toBe(true);
    expect(fake.calls.find((c) => c.method === 'POST').path)
      .toBe('/admin/api/games/word-rush/availability');
  });

  it('warns when a policy change was applied but not persisted', async () => {
    await openGames({
      'POST /admin/api/games/tictactoe/availability': {
        body: { success: true, warning: 'The change is active now but could not be saved, so it will be lost on restart.' },
      },
    });
    const select = el('games-list').querySelector('select');
    select.value = 'disabled';
    select.dispatchEvent(new Event('change', { bubbles: true }));
    el('confirm-ok').click();   // tictactoe has a running lobby, so this asks first
    await tick();
    await tick();

    const toast = el('toast-host').querySelector('.toast-warning');
    expect(toast).not.toBeNull();
    expect(toast.textContent).toMatch(/lost on restart/i);
  });

  it('confirms a delete before sending it', async () => {
    await openGames({ 'POST /admin/api/games/tictactoe/delete': { body: { success: true, detail: 'Deleted.' } } });
    const remove = [...el('games-list').querySelectorAll('.game-card')[0].querySelectorAll('button')]
      .find((b) => b.textContent === 'Delete');
    remove.click();

    expect(el('confirm-body').textContent).toContain('Tic-Tac-Toe');
    expect(el('confirm-body').textContent).toMatch(/cannot be undone/i);

    el('confirm-ok').click();
    await tick();
    await tick();

    expect(fake.calls.find((c) => c.method === 'POST').path).toBe('/admin/api/games/tictactoe/delete');
  });
});

describe('log stream', () => {
  async function openLogs(overrides) {
    fake = installFakeFetch(authedRoutes(overrides));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    admin.selectTab('logs');
    await tick();
    await tick();
  }

  it('renders entries with a level tag and a shortened subsystem', async () => {
    await openLogs();
    const lines = el('log-stream').querySelectorAll('.log-line');
    expect(lines).toHaveLength(2);
    expect(lines[0].querySelector('.log-level').textContent).toBe('INF');
    // The full category is long and repetitive; the last segment identifies the subsystem.
    expect(lines[0].querySelector('.log-category').textContent).toBe('GameCatalog');
    expect(lines[0].querySelector('.log-category').title).toBe('KnockBox.Server.Games.GameCatalog');
    expect(lines[1].classList.contains('log-warning')).toBe(true);
  });

  it('sends the filters as query parameters', async () => {
    await openLogs();
    el('log-filter-level').value = 'Warning';
    el('log-filter-level').dispatchEvent(new Event('change', { bubbles: true }));
    await tick();
    await tick();

    const call = [...fake.calls].reverse().find((c) => c.path === '/admin/api/logs');
    expect(call.url).toContain('level=Warning');
  });

  it('re-reads from the start of the buffer when a filter changes', async () => {
    await openLogs();
    // The first read establishes a cursor; a later poll carries it.
    el('log-filter-q').value = 'catalog';
    el('log-filter-q').dispatchEvent(new Event('input', { bubbles: true }));
    await tick();
    await tick();

    const call = [...fake.calls].reverse().find((c) => c.path === '/admin/api/logs');
    // Without resetting the cursor, a filter change would apply only to entries logged AFTER it.
    expect(call.url).not.toContain('after=');
    expect(call.url).toContain('q=catalog');
  });

  it('reports how much of the buffer is shown', async () => {
    await openLogs();
    expect(el('logs-note').textContent).toContain('41'); // total logged since start
  });

  it('lists downloadable log files with real download links', async () => {
    await openLogs();
    el('log-files-btn').click();
    await tick();
    await tick();

    expect(el('files-backdrop').classList.contains('hidden')).toBe(false);
    const row = el('files-list').querySelector('a.file-row');
    expect(row.getAttribute('href')).toBe('/admin/api/logs/files/knockbox-20260812.log');
    expect(row.download).toBe('knockbox-20260812.log');
  });
});

describe('session expiry', () => {
  it('returns to the login view when any request 401s', async () => {
    let authenticated = true;
    fake = installFakeFetch({
      ...authedRoutes(),
      'GET /admin/api/auth/status': () => ({ body: { configured: true, authenticated } }),
      'GET /admin/api/system/status': () => (authenticated ? { body: STATUS } : { status: 401, body: {} }),
    });
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    expect(el('dashboard-view').classList.contains('hidden')).toBe(false);

    // The session goes away — an expiry, or the password file changing, which revokes every session.
    authenticated = false;
    await admin.checkAuthStatus();
    await tick();

    expect(el('login-view').classList.contains('hidden')).toBe(false);
    expect(el('dashboard-view').classList.contains('hidden')).toBe(true);
  });
});

describe('setup and login forms', () => {
  it('refuses mismatched passwords without asking the server', async () => {
    fake = installFakeFetch({ 'GET /admin/api/auth/status': { body: { configured: false, authenticated: false } } });
    await importAdmin();
    admin.bootstrap();
    await tick();

    el('setup-password').value = 'a-long-enough-password';
    el('confirm-password').value = 'a-different-password';
    el('setup-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await tick();

    expect(el('setup-error').classList.contains('hidden')).toBe(false);
    expect(el('setup-error').textContent).toMatch(/do not match/i);
    expect(fake.calls.some((c) => c.path === '/admin/api/auth/setup')).toBe(false);
  });

  it('surfaces the server error text on a rejected login', async () => {
    fake = installFakeFetch({
      'GET /admin/api/auth/status': { body: { configured: true, authenticated: false } },
      'POST /admin/api/auth/login': { status: 401, body: { success: false, error: 'Invalid admin password.' } },
    });
    await importAdmin();
    admin.bootstrap();
    await tick();

    el('login-password').value = 'wrong';
    el('login-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await tick();
    await tick();

    expect(el('login-error').textContent).toBe('Invalid admin password.');
  });
});

describe('history graphs', () => {
  async function openOverview(overrides) {
    fake = installFakeFetch(authedRoutes(overrides));
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();
    await tick();
  }

  it('draws one sparkline per series from the server-side samples', async () => {
    await openOverview();

    const cards = [...document.querySelectorAll('#history-graphs .graph-card')];
    expect(cards.map((c) => c.dataset.graph)).toEqual(['cpu', 'memory', 'players', 'lobbies']);
    // A real path, not an empty box: the history came from the server, so the graph is populated on the
    // first poll rather than starting to fill from now.
    expect(cards[0].querySelector('.sparkline path')).not.toBeNull();
    expect(el('history-badge').textContent).toContain('samples');
  });

  it('polls with a cursor so an open dashboard fetches only what is new', async () => {
    // Fake timers BEFORE bootstrap: the poll interval is armed during it, so installing them afterwards
    // leaves a real interval that advanceTimersByTime can never fire.
    vi.useFakeTimers();
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await vi.advanceTimersByTimeAsync(1);

    const first = fake.calls.filter((c) => c.path === '/admin/api/metrics/history');
    expect(first[0].url).toContain('after=0');

    await vi.advanceTimersByTimeAsync(5000);
    const later = fake.calls.filter((c) => c.path === '/admin/api/metrics/history');
    expect(later.length).toBeGreaterThan(first.length);
    expect(later[later.length - 1].url).toContain('after=3');
  });

  it('restarts the graphs when the server does, instead of freezing on the old picture', async () => {
    // MetricHistory is in-memory, so a restart begins numbering at 1 again. Clamping the cursor upward
    // meant every later `?after=<pre-restart seq>` matched nothing and all four graphs held the old
    // picture until somebody reloaded — at exactly the moment an operator is watching them.
    vi.useFakeTimers();
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await vi.advanceTimersByTimeAsync(1);
    expect(el('history-badge').textContent).toBe('3 samples');

    // The server came back: sequences restart low, and it can only offer what it has sampled since.
    // `fake.routes` is the live table the mock reads, so replacing one entry redirects it mid-test.
    fake.routes['GET /admin/api/metrics/history'] = {
      body: { ...HISTORY, samples: [historySample(1, 0)], lastSequence: 1 },
    };
    await vi.advanceTimersByTimeAsync(5000);

    // The pre-restart samples are dropped rather than drawn continuously with the new ones, which would
    // show a gap that never happened.
    expect(el('history-badge').textContent).toBe('1 samples');

    // And the next poll asks from the server's sequence, not the stale high-water mark. Clamping upward
    // kept sending after=3 forever, which matched nothing and froze every graph.
    await vi.advanceTimersByTimeAsync(5000);
    const calls = fake.calls.filter((c) => c.path === '/admin/api/metrics/history');
    expect(calls[calls.length - 1].url).toContain('after=1');
  });

  it('says history is off rather than drawing an empty chart', async () => {
    await openOverview({
      'GET /admin/api/metrics/history': {
        body: { enabled: false, samples: [], lastSequence: 0, retained: 0, capacity: 240, sampleSeconds: 0, processorCount: 8 },
      },
    });

    expect(el('history-badge').textContent).toBe('Off');
    expect(el('history-note').textContent).toContain('MetricSampleSeconds=0');
    expect(document.querySelectorAll('#history-graphs .graph-card').length).toBe(0);
  });

  it('shows a dash for a game with no authority module, and the cost for one that has', async () => {
    await openOverview({
      'GET /admin/api/metrics': {
        body: {
          ...METRICS,
          games: [
            METRICS.games[0],
            {
              gameId: 'word-rush', framesIn: 10, framesOut: 10, bytesIn: 100, bytesOut: 100,
              framesDropped: 0, fanOut: 1, lobbies: 1, players: 2,
              socketFramesSent: 10, socketBytesSent: 100, socketFramesDropped: 0,
              authorityCalls: 400, authorityCpuSeconds: 1.25, authorityAverageMs: 3.1,
              authorityMaxMs: 40, authorityErrors: 0,
            },
          ],
        },
      },
    });

    const rows = [...document.querySelectorAll('#metrics-body tr')];
    // A browser-side game costs this process no CPU at all — which is a different statement from "0.00s".
    expect(rows[0].textContent).toContain('--');
    expect(rows[1].textContent).toContain('1.25s');
    expect(rows[1].textContent).toContain('3.1 ms/call');
  });
});

describe('sidebar navigation and collapse state', () => {
  afterEach(() => {
    try {
      localStorage.clear();
    } catch {
      // ignore
    }
  });

  it('toggles sidebar collapse state on button click and persists to localStorage', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    const dashboardView = el('dashboard-view');
    const toggleBtn = el('sidebar-toggle');
    expect(dashboardView.classList.contains('sidebar-collapsed')).toBe(false);
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('true');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Collapse sidebar');
    expect(toggleBtn.getAttribute('title')).toBe('Collapse sidebar');

    // Click to collapse
    toggleBtn.click();
    expect(dashboardView.classList.contains('sidebar-collapsed')).toBe(true);
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('false');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Expand sidebar');
    expect(toggleBtn.getAttribute('title')).toBe('Expand sidebar');
    expect(localStorage.getItem('kb_admin_sidebar_collapsed')).toBe('true');

    // Click to expand
    toggleBtn.click();
    expect(dashboardView.classList.contains('sidebar-collapsed')).toBe(false);
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('true');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Collapse sidebar');
    expect(toggleBtn.getAttribute('title')).toBe('Collapse sidebar');
    expect(localStorage.getItem('kb_admin_sidebar_collapsed')).toBeNull();
  });

  it('restores collapsed state from localStorage on bootstrap', async () => {
    localStorage.setItem('kb_admin_sidebar_collapsed', 'true');
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();
    await tick();

    const dashboardView = el('dashboard-view');
    const toggleBtn = el('sidebar-toggle');
    expect(dashboardView.classList.contains('sidebar-collapsed')).toBe(true);
    expect(toggleBtn.getAttribute('aria-expanded')).toBe('false');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Expand sidebar');
    expect(toggleBtn.getAttribute('title')).toBe('Expand sidebar');
  });

  it('supports programmatic setSidebarCollapsed and toggleSidebarCollapsed', async () => {
    fake = installFakeFetch(authedRoutes());
    await importAdmin();
    admin.bootstrap();
    await tick();

    const dashboardView = el('dashboard-view');
    admin.setSidebarCollapsed(true, { persist: false });
    expect(dashboardView.classList.contains('sidebar-collapsed')).toBe(true);
    expect(localStorage.getItem('kb_admin_sidebar_collapsed')).toBeNull();

    admin.toggleSidebarCollapsed();
    expect(dashboardView.classList.contains('sidebar-collapsed')).toBe(false);
  });
});

