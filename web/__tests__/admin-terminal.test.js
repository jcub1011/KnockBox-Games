// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { loadTerminalDom, installFakeFetch } from './helpers.js';

const LOG_RESPONSE = {
  buffered: 2,
  totalWritten: 41,
  lastSequence: 102,
  entries: [
    {
      seq: 101,
      time: '2026-08-12T14:32:00Z',
      level: 'Information',
      category: 'KnockBox.Server.Games.GameCatalog',
      message: 'Catalog loaded 3 games.',
      exception: null,
    },
    {
      seq: 102,
      time: '2026-08-12T14:32:01Z',
      level: 'Warning',
      category: 'KnockBox.Server.Relay',
      message: 'Slow client detected.',
      exception: 'SocketTimeoutException: timed out',
    },
  ],
};

const LOG_FILES_RESPONSE = {
  logsRoot: '/var/log/knockbox',
  files: [
    { name: 'knockbox-20260812.log', bytes: 1024, modified: '2026-08-12T14:00:00Z' },
  ],
};

function el(id) {
  return document.getElementById(id);
}

function tick() {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('terminal.js (dedicated log terminal)', () => {
  let terminal;
  let fake;

  beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] });
    loadTerminalDom();
    fake = installFakeFetch({
      'GET /admin/api/logs': () => ({ body: LOG_RESPONSE }),
      'GET /admin/api/logs/files': () => ({ body: LOG_FILES_RESPONSE }),
      'POST /admin/api/auth/login': () => ({ body: { success: true } }),
    });

    vi.resetModules();
    terminal = await import('../admin/terminal.js');
  });

  afterEach(() => {
    terminal?.stopPolling?.();
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it('renders log entries with level, category, and formatted message', async () => {
    terminal.bootstrap();
    await tick();

    const lines = el('terminal-stream').querySelectorAll('.log-line');
    expect(lines).toHaveLength(2);

    expect(lines[0].querySelector('.log-level').textContent).toBe('INF');
    expect(lines[0].querySelector('.log-category').textContent).toBe('GameCatalog');
    expect(lines[0].querySelector('.log-message').textContent).toBe('Catalog loaded 3 games.');

    expect(lines[1].querySelector('.log-level').textContent).toBe('WAR');
    expect(lines[1].querySelector('.log-category').textContent).toBe('Relay');
    expect(lines[1].querySelector('.log-exception').textContent).toContain('SocketTimeoutException');

    expect(el('terminal-metrics').textContent).toContain('41');
    expect(el('terminal-status-text').textContent).toBe('LIVE');
  });

  it('sends filter parameters when level or category inputs change', async () => {
    terminal.bootstrap();
    await tick();

    el('term-filter-level').value = 'Warning';
    el('term-filter-level').dispatchEvent(new Event('change', { bubbles: true }));
    await tick();

    const call = [...fake.calls].reverse().find((c) => c.path === '/admin/api/logs');
    expect(call.url).toContain('level=Warning');

    el('term-filter-category').value = 'Relay';
    el('term-filter-category').dispatchEvent(new Event('input', { bubbles: true }));
    await tick();

    const callCat = [...fake.calls].reverse().find((c) => c.path === '/admin/api/logs');
    expect(callCat.url).toContain('category=Relay');
  });

  it('toggles paused status when follow is unchecked', async () => {
    terminal.bootstrap();
    await tick();

    expect(el('terminal-status-text').textContent).toBe('LIVE');

    el('term-follow').checked = false;
    el('term-follow').dispatchEvent(new Event('change', { bubbles: true }));
    await tick();

    expect(el('terminal-status-text').textContent).toBe('PAUSED');
  });

  it('clears visible logs when Clear button is clicked', async () => {
    terminal.bootstrap();
    await tick();

    expect(el('terminal-stream').querySelectorAll('.log-line')).toHaveLength(2);

    el('term-clear-btn').click();
    await tick();

    expect(el('terminal-stream').querySelectorAll('.log-line')).toHaveLength(0);
    expect(el('terminal-empty').classList.contains('hidden')).toBe(false);
  });

  it('opens log files download dialog when Download button is clicked', async () => {
    terminal.bootstrap();
    await tick();

    el('term-files-btn').click();
    await tick();

    expect(el('term-files-backdrop').classList.contains('hidden')).toBe(false);
    const row = el('term-files-list').querySelector('a.file-row');
    expect(row.getAttribute('href')).toBe('/admin/api/logs/files/knockbox-20260812.log');
    expect(row.download).toBe('knockbox-20260812.log');

    el('term-files-close').click();
    expect(el('term-files-backdrop').classList.contains('hidden')).toBe(true);
  });

  it('displays login overlay on 401 response and resumes on authentication', async () => {
    let authed = false;
    fake = installFakeFetch({
      'GET /admin/api/logs': () => (authed ? { body: LOG_RESPONSE } : { status: 401, body: {} }),
      'POST /admin/api/auth/login': () => {
        authed = true;
        return { body: { success: true } };
      },
    });

    terminal.bootstrap();
    await tick();

    expect(el('terminal-login-overlay').classList.contains('hidden')).toBe(false);
    expect(el('terminal-status-text').textContent).toBe('AUTH REQUIRED');

    el('term-password').value = 'valid-password-123';
    el('terminal-login-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
    await tick();

    expect(el('terminal-login-overlay').classList.contains('hidden')).toBe(true);
    expect(el('terminal-stream').querySelectorAll('.log-line')).toHaveLength(2);
  });
});
