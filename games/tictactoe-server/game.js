// Tic-Tac-Toe — SERVER-authoritative client. The rules live in authority.js, which the SERVER
// runs (GAME.json: "serverAuthority"); this file only sends intents and renders whatever state
// the authority publishes. There is deliberately NO isHost branch anywhere: in server-authority
// mode every client — including the lobby creator — is a guest (KnockBox.isHost is false).
//
// The wire contract is the _kb envelope (the raw-SDK version of what kb-authority.js speaks):
//   { _kb: 'intent', action: { kind: 'move', cell } }  → sendToHost ("host" routes to the authority)
//   { _kb: 'sync' }                                    → sendToHost (ask for the current state)
//   { _kb: 'delta', patch }  ← from 'server'           (this game's patches are the full state)
//   { _kb: 'state', state }  ← from 'server'

const turnEl = document.getElementById('turn');
const boardEl = document.getElementById('board');
const bannerEl = document.getElementById('banner');

let me = null;      // my playerId
let players = [];   // [{id, displayName}] — display names only; seating comes from the authority
let game = null;    // authoritative { seats, board, next, winner } — null until the first state

const cells = [];

function build() {
  boardEl.innerHTML = '';
  for (let i = 0; i < 9; i++) {
    const b = document.createElement('button');
    b.className = 'cell';
    b.dataset.cell = i;
    b.onclick = () => KnockBox.sendToHost({ _kb: 'intent', action: { kind: 'move', cell: i } });
    boardEl.appendChild(b);
    cells[i] = b;
  }
}

function nameOf(playerId) {
  const p = players.find((x) => x.id === playerId);
  return p ? p.displayName : playerId;
}

// ── Rendering (pure function of the authority's last state) ───────────────────
function render() {
  const board = game ? game.board : Array(9).fill(0);
  const { next = null, winner = null, seats = [null, null] } = game || {};
  const waiting = seats[0] === null || seats[1] === null;

  for (let i = 0; i < 9; i++) {
    const v = board[i];
    cells[i].textContent = v === 1 ? 'X' : v === 2 ? 'O' : '';
    cells[i].className = 'cell' + (v === 1 ? ' x' : v === 2 ? ' o' : '');
    cells[i].disabled = v !== 0 || winner !== null || next !== me;
  }

  if (waiting) {
    turnEl.textContent = 'Waiting for an opponent…';
    bannerEl.textContent = '';
  } else if (winner === 'draw') {
    turnEl.textContent = '';
    bannerEl.textContent = "It's a draw!";
  } else if (winner) {
    turnEl.textContent = '';
    bannerEl.textContent = winner === me ? 'You win! 🎉' : `${nameOf(winner)} wins`;
  } else {
    bannerEl.textContent = '';
    turnEl.textContent = next === me ? 'Your turn' : `Waiting for ${nameOf(next)}…`;
  }
}

// ── Wire up KnockBox ──────────────────────────────────────────────────────────
KnockBox.onReady((info) => {
  me = info.playerId;
  players = info.players;
  build();
  // Everyone is a guest: request the current state (idempotent — a broadcast may also be en route).
  KnockBox.sendToHost({ _kb: 'sync' });
  render();
});

KnockBox.onMessage(({ from, payload }) => {
  // Only the authority publishes state, always as 'server' (the relay also drops forgeries).
  if (from !== 'server' || !payload) return;
  if (payload._kb === 'delta') game = payload.patch;      // this game's patches are absolute full state
  else if (payload._kb === 'state') game = payload.state;
  else return;
  render();
});

// Roster changes only affect display names here — seating/turns are the authority's business, and
// it re-broadcasts state after every roster change.
KnockBox.onPlayerJoined(() => { players = KnockBox.players; render(); });
KnockBox.onPlayerLeft(() => { players = KnockBox.players; render(); });

// The owner (kick/open-close powers) can migrate when the current owner leaves — authority.js
// promotes the longest-standing member via kb.setOwner. Gate any owner-only UI on isOwner.
KnockBox.onOwnerChanged((ownerId) => {
  console.info('[tictactoe-server] lobby owner is now', nameOf(ownerId), '— am I the owner?', KnockBox.isOwner);
});
