// KnockBox Logs Terminal View (Standalone Live Console)
// Runs in browser (served from /terminal.html) and tested under jsdom.
import {
  appendLogEntries,
  formatClock,
  formatCount,
  formatBytes,
  logLevelClass,
  logLevelTag,
} from './admin-core.js';

export const LOG_VIEW_LIMIT = 1000;
export const POLL_INTERVAL_MS = 2000;

export let logCursor = 0;
export let logEntries = [];
export let pollTimer = null;
export let isConnected = true;

function el(id) {
  return document.getElementById(id);
}

export function getLogEntries() {
  return logEntries;
}

export function getLogCursor() {
  return logCursor;
}

export async function request(path, init = {}) {
  const options = {
    credentials: 'same-origin',
    ...init,
    headers: {
      Accept: 'application/json',
      ...(init.headers || {}),
    },
  };

  try {
    const res = await fetch(path, options);
    if (res.status === 401) {
      handleUnauthorized();
      return null;
    }
    if (!res.ok) {
      updateStatus(false, 'ERROR');
      return null;
    }
    updateStatus(true, el('term-follow')?.checked ? 'LIVE' : 'PAUSED');
    return await res.json();
  } catch {
    updateStatus(false, 'DISCONNECTED');
    return null;
  }
}

export function updateStatus(connected, label) {
  isConnected = connected;
  const pill = el('terminal-status-pill');
  const text = el('terminal-status-text');
  if (!pill || !text) return;

  pill.classList.remove('live', 'disconnected', 'paused');
  if (!connected) {
    pill.classList.add('disconnected');
    text.textContent = label || 'DISCONNECTED';
  } else if (label === 'PAUSED') {
    pill.classList.add('paused');
    text.textContent = 'PAUSED';
  } else {
    pill.classList.add('live');
    text.textContent = 'LIVE';
  }
}

export function handleUnauthorized() {
  updateStatus(false, 'AUTH REQUIRED');
  const overlay = el('terminal-login-overlay');
  if (overlay) overlay.classList.remove('hidden');
}

export async function refreshLogs() {
  const follow = el('term-follow')?.checked ?? true;
  if (!follow && logEntries.length > 0) {
    updateStatus(true, 'PAUSED');
    return;
  }

  const params = new URLSearchParams();
  if (logCursor > 0) params.set('after', String(logCursor));
  const level = el('term-filter-level')?.value;
  if (level) params.set('level', level);
  const category = el('term-filter-category')?.value.trim();
  if (category) params.set('category', category);
  const search = el('term-filter-q')?.value.trim();
  if (search) params.set('q', search);
  params.set('limit', String(LOG_VIEW_LIMIT));

  const data = await request(`/admin/api/logs?${params}`);
  if (!data) return;

  logEntries = appendLogEntries(logEntries, data.entries, LOG_VIEW_LIMIT);
  logCursor = data.lastSequence ?? logCursor;
  renderLogs(data);
}

export function renderLogs(data) {
  const stream = el('terminal-stream');
  if (!stream) return;

  const atBottom = stream.scrollHeight - stream.scrollTop - stream.clientHeight < 40;

  stream.innerHTML = '';
  const emptyEl = el('terminal-empty');
  if (emptyEl) {
    emptyEl.classList.toggle('hidden', logEntries.length > 0);
  }

  for (const entry of logEntries) {
    const line = document.createElement('div');
    line.className = `log-line ${logLevelClass(entry.level)}`;

    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = formatClock(entry.time);

    const level = document.createElement('span');
    level.className = 'log-level';
    level.textContent = logLevelTag(entry.level);

    const category = document.createElement('span');
    category.className = 'log-category';
    category.textContent = String(entry.category || '').split('.').pop() || '-';
    category.title = entry.category || '';

    const message = document.createElement('span');
    message.className = 'log-message';
    message.textContent = entry.message;

    line.append(time, level, category, message);
    if (entry.exception) {
      const ex = document.createElement('pre');
      ex.className = 'log-exception';
      ex.textContent = entry.exception;
      line.appendChild(ex);
    }
    stream.appendChild(line);
  }

  if (atBottom && (el('term-follow')?.checked ?? true)) {
    stream.scrollTop = stream.scrollHeight;
  }

  const metricsEl = el('terminal-metrics');
  if (metricsEl) {
    metricsEl.textContent = data
      ? `Showing ${logEntries.length} of ${data.buffered ?? logEntries.length} buffered (${formatCount(data.totalWritten ?? 0)} total)`
      : `Showing ${logEntries.length} entries`;
  }
}

export function resetLogStream() {
  logCursor = 0;
  logEntries = [];
  refreshLogs();
}

export function clearLogs() {
  logEntries = [];
  const stream = el('terminal-stream');
  if (stream) stream.innerHTML = '';
  const emptyEl = el('terminal-empty');
  if (emptyEl) emptyEl.classList.remove('hidden');
  const metricsEl = el('terminal-metrics');
  if (metricsEl) metricsEl.textContent = 'Cleared (resumes on new entries)';
}

export async function openLogFiles() {
  const data = await request('/admin/api/logs/files');
  if (!data) return;
  const host = el('term-files-list');
  if (!host) return;
  host.innerHTML = '';
  const note = el('term-files-note');
  if (note) note.textContent = data.error || `From ${data.logsRoot}`;

  for (const file of data.files || []) {
    const row = document.createElement('a');
    row.className = 'file-row';
    row.href = `/admin/api/logs/files/${encodeURIComponent(file.name)}`;
    row.download = file.name;
    const name = document.createElement('span');
    name.textContent = file.name;
    const meta = document.createElement('span');
    meta.className = 'file-meta';
    meta.textContent = `${formatBytes(file.bytes)} — ${formatClock(file.modified)}`;
    row.append(name, meta);
    host.appendChild(row);
  }
  if (!(data.files || []).length) {
    const empty = document.createElement('p');
    empty.className = 'empty-state';
    empty.textContent = 'No log files found on disk.';
    host.appendChild(empty);
  }
  el('term-files-backdrop')?.classList.remove('hidden');
}

export async function onLoginSubmit(e) {
  e?.preventDefault?.();
  const passwordInput = el('term-password');
  const errorBanner = el('term-login-error');
  if (!passwordInput) return;

  const password = passwordInput.value;
  if (errorBanner) errorBanner.classList.add('hidden');

  try {
    const res = await fetch('/admin/api/auth/login', {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        Accept: 'application/json',
        'Sec-Fetch-Site': 'same-origin',
      },
      body: JSON.stringify({ password }),
    });

    const body = await res.json();
    if (res.ok && body.success) {
      passwordInput.value = '';
      el('terminal-login-overlay')?.classList.add('hidden');
      resetLogStream();
      startPolling();
    } else {
      if (errorBanner) {
        errorBanner.textContent = body.error || 'Authentication failed.';
        errorBanner.classList.remove('hidden');
      }
    }
  } catch {
    if (errorBanner) {
      errorBanner.textContent = 'Network error while authenticating.';
      errorBanner.classList.remove('hidden');
    }
  }
}

export function startPolling() {
  stopPolling();
  pollTimer = setInterval(refreshLogs, POLL_INTERVAL_MS);
}

export function stopPolling() {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

export function bindEvents() {
  el('term-filter-level')?.addEventListener('change', resetLogStream);
  el('term-filter-category')?.addEventListener('input', resetLogStream);
  el('term-filter-q')?.addEventListener('input', resetLogStream);
  el('term-follow')?.addEventListener('change', () => {
    if (el('term-follow').checked) {
      updateStatus(true, 'LIVE');
      refreshLogs();
    } else {
      updateStatus(true, 'PAUSED');
    }
  });
  el('term-clear-btn')?.addEventListener('click', clearLogs);
  el('term-files-btn')?.addEventListener('click', openLogFiles);
  el('term-files-close')?.addEventListener('click', () => {
    el('term-files-backdrop')?.classList.add('hidden');
  });
  el('term-files-backdrop')?.addEventListener('click', (e) => {
    if (e.target === el('term-files-backdrop')) {
      el('term-files-backdrop').classList.add('hidden');
    }
  });
  el('terminal-login-form')?.addEventListener('submit', onLoginSubmit);

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      el('term-files-backdrop')?.classList.add('hidden');
    }
  });
}

export function initFiltersFromUrl() {
  try {
    const params = new URLSearchParams(window.location?.search || '');
    const level = params.get('level');
    if (level && el('term-filter-level')) el('term-filter-level').value = level;
    const category = params.get('category');
    if (category && el('term-filter-category')) el('term-filter-category').value = category;
    const q = params.get('q');
    if (q && el('term-filter-q')) el('term-filter-q').value = q;
  } catch {
    // Ignore URL parsing failure in test env
  }
}

export function bootstrap() {
  initFiltersFromUrl();
  bindEvents();
  refreshLogs();
  startPolling();
}

if (typeof window !== 'undefined' && document.getElementById('terminal-app')) {
  bootstrap();
}
