// Tic-Tac-Toe — host-authoritative. The host client owns the board and validates every move;
// guests only send intent and render whatever state the host broadcasts. The server is a blind
// relay. This proves authority with no server-side game code.
//
// Message kinds over the KnockBox relay:
//   { kind: 'move', cell }   guest/host intent -> host          (sendToHost)
//   { kind: 'sync' }         "send me current state" -> host    (sendToHost)
//   { kind: 'state', board, next, winner }   host -> everyone    (sendToAll)

const WINS = [
  [0, 1, 2], [3, 4, 5], [6, 7, 8],
  [0, 3, 6], [1, 4, 7], [2, 5, 8],
  [0, 4, 8], [2, 4, 6],
];

const turnEl = document.getElementById('turn');
const boardEl = document.getElementById('board');
const bannerEl = document.getElementById('banner');

let me = null;            // my playerId
let players = [];         // [{id, displayName}] — index 0 = X, index 1 = O
let isHost = false;

// Host-authoritative state (only meaningful on the host; mirrored on guests via broadcasts).
let board = Array(9).fill(0);
let next = null;          // playerId whose turn it is, or null when over
let winner = null;        // playerId | 'draw' | null

const cells = [];

function build() {
  boardEl.innerHTML = '';
  for (let i = 0; i < 9; i++) {
    const b = document.createElement('button');
    b.className = 'cell';
    b.dataset.cell = i;
    b.onclick = () => KnockBox.sendToHost({ kind: 'move', cell: i });
    boardEl.appendChild(b);
    cells[i] = b;
  }
}

function markOf(playerId) {
  return players.findIndex((p) => p.id === playerId) === 0 ? 1 : 2;
}
function nameOf(playerId) {
  const p = players.find((x) => x.id === playerId);
  return p ? p.displayName : playerId;
}

// ── Host authority ───────────────────────────────────────────────────────────
function applyMove(fromId, cell) {
  if (winner) return;                       // game over
  if (fromId !== next) return;              // not their turn
  if (cell < 0 || cell > 8 || board[cell] !== 0) return; // occupied / out of range
  board[cell] = markOf(fromId);
  winner = computeWinner();
  if (!winner) next = players.find((p) => p.id !== fromId).id; // flip turn
}

function computeWinner() {
  for (const [a, b, c] of WINS) {
    if (board[a] !== 0 && board[a] === board[b] && board[b] === board[c]) {
      // board[a] is a mark (1 or 2); map back to the playerId holding it.
      return players[board[a] - 1].id;
    }
  }
  return board.every((v) => v !== 0) ? 'draw' : null;
}

function broadcastState() {
  KnockBox.sendToAll({ kind: 'state', board, next, winner });
}

// ── Rendering (all clients render only from received state) ───────────────────
function render() {
  for (let i = 0; i < 9; i++) {
    const v = board[i];
    cells[i].textContent = v === 1 ? 'X' : v === 2 ? 'O' : '';
    cells[i].className = 'cell' + (v === 1 ? ' x' : v === 2 ? ' o' : '');
    cells[i].disabled = v !== 0 || winner !== null || next !== me;
  }

  if (winner === 'draw') {
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
  isHost = info.isHost;
  build();

  if (isHost) {
    next = players[0].id;     // X (the host / creator) moves first
    broadcastState();         // seed everyone
  } else {
    KnockBox.sendToHost({ kind: 'sync' }); // ask host for current state in case we missed the seed
  }
  render();
});

KnockBox.onMessage(({ from, payload }) => {
  if (!payload) return;

  if (payload.kind === 'state') {
    // Authoritative state from the host — adopt and render.
    board = payload.board;
    next = payload.next;
    winner = payload.winner;
    render();
    return;
  }

  // Only the host acts on intents.
  if (!isHost) return;
  if (payload.kind === 'move') applyMove(from, payload.cell);
  // Always rebroadcast — even an illegal move yields an (unchanged) authoritative state,
  // so the offending client re-renders the real board. (Acceptance #6.)
  broadcastState();
});
