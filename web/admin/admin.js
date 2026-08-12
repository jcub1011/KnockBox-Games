// Admin Portal Client Logic

const setupView = document.getElementById('setup-view');
const loginView = document.getElementById('login-view');
const dashboardView = document.getElementById('dashboard-view');

const setupForm = document.getElementById('setup-form');
const setupPasswordInput = document.getElementById('setup-password');
const confirmPasswordInput = document.getElementById('confirm-password');
const setupError = document.getElementById('setup-error');

const loginForm = document.getElementById('login-form');
const loginPasswordInput = document.getElementById('login-password');
const loginError = document.getElementById('login-error');

const logoutBtn = document.getElementById('logout-btn');
const serverStatusText = document.getElementById('server-status-text');
const serverStatusDot = document.querySelector('.status-dot');

const metricUptime = document.getElementById('metric-uptime');
const metricLobbies = document.getElementById('metric-lobbies');
const metricGames = document.getElementById('metric-games');
const metricMemory = document.getElementById('metric-memory');
const metricHeap = document.getElementById('metric-heap');
const lastUpdatedText = document.getElementById('last-updated');

let pollTimer = null;

async function checkAuthStatus() {
  try {
    const res = await fetch('/admin/api/auth/status');
    if (!res.ok) {
      showErrorStatus('Server unreachable on admin port');
      return;
    }
    const data = await res.json();
    showOkStatus();

    if (!data.configured) {
      showView(setupView);
      logoutBtn.classList.add('hidden');
      stopPolling();
    } else if (!data.authenticated) {
      showView(loginView);
      logoutBtn.classList.add('hidden');
      stopPolling();
    } else {
      showView(dashboardView);
      logoutBtn.classList.remove('hidden');
      fetchMetrics();
      startPolling();
    }
  } catch (err) {
    showErrorStatus('Network Error');
    console.error('Failed to check auth status:', err);
  }
}

function showView(viewElement) {
  [setupView, loginView, dashboardView].forEach(v => v.classList.add('hidden'));
  viewElement.classList.remove('hidden');
}

// One function per state, both routed through setStatus. The previous shape was an
// updateServerStatus(online) whose offline branch nothing ever reached — every offline path called
// showErrorStatus — so the file carried two ways to paint the dot red and a reader had to check both to
// learn which one ran.
function showOkStatus() {
  setStatus('Admin Port Active', 'var(--success-color)');
}

function showErrorStatus(msg) {
  setStatus(msg, 'var(--error-color)');
}

function setStatus(text, color) {
  serverStatusText.textContent = text;
  serverStatusDot.style.backgroundColor = color;
  serverStatusDot.style.boxShadow = `0 0 8px ${color}`;
}

// Setup Form Submission
setupForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  setupError.classList.add('hidden');
  
  const password = setupPasswordInput.value;
  const confirm = confirmPasswordInput.value;

  if (password !== confirm) {
    setupError.textContent = 'Passwords do not match.';
    setupError.classList.remove('hidden');
    return;
  }

  try {
    const res = await fetch('/admin/api/auth/setup', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password })
    });
    const data = await res.json();

    if (!res.ok || !data.success) {
      setupError.textContent = data.error || 'Failed to setup admin password.';
      setupError.classList.remove('hidden');
      return;
    }

    setupPasswordInput.value = '';
    confirmPasswordInput.value = '';
    await checkAuthStatus();
  } catch (err) {
    setupError.textContent = 'Network error setting up password.';
    setupError.classList.remove('hidden');
  }
});

// Login Form Submission
loginForm.addEventListener('submit', async (e) => {
  e.preventDefault();
  loginError.classList.add('hidden');

  const password = loginPasswordInput.value;

  try {
    const res = await fetch('/admin/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password })
    });
    const data = await res.json();

    if (!res.ok || !data.success) {
      loginError.textContent = data.error || 'Invalid password.';
      loginError.classList.remove('hidden');
      return;
    }

    loginPasswordInput.value = '';
    await checkAuthStatus();
  } catch (err) {
    loginError.textContent = 'Network error during login.';
    loginError.classList.remove('hidden');
  }
});

// Logout
logoutBtn.addEventListener('click', async () => {
  try {
    await fetch('/admin/api/auth/logout', { method: 'POST' });
  } catch (e) {
    console.error('Logout error:', e);
  }
  await checkAuthStatus();
});

// Metrics Polling
async function fetchMetrics() {
  try {
    const res = await fetch('/admin/api/system/status');
    if (!res.ok) {
      if (res.status === 401) {
        checkAuthStatus();
      }
      return;
    }
    const data = await res.json();
    
    metricUptime.textContent = data.uptime || '--';
    metricLobbies.textContent = data.activeLobbies ?? 0;
    metricGames.textContent = data.registeredGames ?? 0;
    metricMemory.textContent = `${data.workingSetMb ?? '--'} MB`;
    metricHeap.textContent = `Managed heap: ${data.managedHeapMb ?? '--'} MB`;

    const now = new Date().toLocaleTimeString();
    lastUpdatedText.textContent = `Updated ${now}`;
  } catch (err) {
    console.error('Error fetching metrics:', err);
  }
}

function startPolling() {
  stopPolling();
  pollTimer = setInterval(fetchMetrics, 5000);
}

function stopPolling() {
  if (pollTimer) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

// Initial check on page load
checkAuthStatus();
