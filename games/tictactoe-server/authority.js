// Tic-Tac-Toe authority module — the SERVER runs this, sandboxed, one instance per lobby
// (GAME.json: "serverAuthority": "authority.js"). Pure game rules, no rendering, no ambient I/O:
// the game's client (game.js) only sends intents and renders whatever state arrives. This is the
// port of games/tictactoe's host-authoritative applyMove to the server module contract.
//
// Contract (docs/SERVER_AUTHORITY_DESIGN.md §3): a single-file ES module exporting
// createAuthority(kb) whose returned object implements init / applyIntent / snapshot (plus
// optional roster hooks). Patches are ABSOLUTE — here simply the full state, which is tiny.
// The kb capability object provides now/log/setLobbyOpen/setOwner; note there is NO Date global
// in the sandbox (use kb.now()).

export function createAuthority(kb) {
  const WINS = [
    [0, 1, 2], [3, 4, 5], [6, 7, 8],
    [0, 3, 6], [1, 4, 7], [2, 5, 8],
    [0, 4, 8], [2, 4, 6],
  ];

  let roster = [];            // current member ids, in join order
  let ownerId = null;         // tracked for the owner-succession demo below
  let seats = [null, null];   // [X playerId, O playerId] — spectators (if any) are unseated
  let board = Array(9).fill(0); // 0 empty, 1 X, 2 O
  let next = null;            // playerId to move, or null (waiting / game over)
  let winner = null;          // playerId | 'draw' | null

  const state = () => ({ seats, board, next, winner });

  function markOf(playerId) { return playerId === seats[0] ? 1 : 2; }

  function computeWinner() {
    for (const [a, b, c] of WINS) {
      if (board[a] !== 0 && board[a] === board[b] && board[b] === board[c]) {
        return seats[board[a] - 1]; // the mark (1|2) maps back to the seat holding it
      }
    }
    return board.every((v) => v !== 0) ? 'draw' : null;
  }

  function trySeat(playerId) {
    if (seats.includes(playerId)) return;
    if (seats[0] === null) seats[0] = playerId;
    else if (seats[1] === null) seats[1] = playerId;
  }

  // (Re)start the round when both seats are filled; otherwise wait.
  function resetRound() {
    board = Array(9).fill(0);
    winner = null;
    next = seats[0] !== null && seats[1] !== null ? seats[0] : null;
  }

  function updateJoinGate() {
    // Close joins while both seats are taken; reopen when one frees up. The join gate is a lobby
    // power, but the MODULE holds it here — the owner player has no special game logic.
    kb.setLobbyOpen(seats[0] === null || seats[1] === null);
  }

  return {
    init(players) {
      roster = players.map((p) => p.id);
      ownerId = roster.length > 0 ? roster[0] : null;
      for (const id of roster) trySeat(id);
      resetRound();
      updateJoinGate();
      kb.log.info('tictactoe authority started at ' + kb.now());
    },

    // The ported applyMove guards: reject (return null → nothing is sent) unless the round is
    // live, it's the sender's turn, and the cell is a legal move. On a legal move, mutate and
    // return the full (absolute) state as the patch.
    applyIntent(fromId, action) {
      if (!action || action.kind !== 'move') return null;
      if (seats[0] === null || seats[1] === null) return null; // waiting for an opponent
      if (winner !== null) return null;                        // game over
      if (fromId !== next) return null;                        // not their turn
      const cell = action.cell;
      if (!Number.isInteger(cell) || cell < 0 || cell > 8 || board[cell] !== 0) return null;

      board[cell] = markOf(fromId);
      winner = computeWinner();
      next = winner !== null ? null : (fromId === seats[0] ? seats[1] : seats[0]);
      if (winner !== null) kb.log.info('round over: ' + (winner === 'draw' ? 'draw' : winner + ' wins'));
      return state();
    },

    snapshot() { return state(); },

    onPlayerJoined(player) {
      roster.push(player.id);
      const wasWaiting = seats[0] === null || seats[1] === null;
      trySeat(player.id);
      if (wasWaiting && seats[0] !== null && seats[1] !== null) resetRound(); // opponent arrived — play
      updateJoinGate();
      return null; // the server re-broadcasts state after every roster change
    },

    onPlayerLeft(playerId) {
      roster = roster.filter((id) => id !== playerId);

      // Owner succession (the kb.setOwner pattern, design §3): when the departed player held the
      // lobby powers, promote the longest-standing remaining member. Policy is the game's; the
      // platform only ships the primitive.
      if (playerId === ownerId && roster.length > 0) {
        ownerId = roster[0];
        kb.setOwner(ownerId);
      }

      const seatIdx = seats.indexOf(playerId);
      if (seatIdx !== -1) {
        seats[seatIdx] = null;
        for (const id of roster) trySeat(id); // seat a spectator if one is waiting
        resetRound();                          // a seated player left — the round can't continue
        updateJoinGate();
      }
      return null;
    },
  };
}

// Broadcast mode (no hidden information), no server tick — turn-based.
export const config = {};
