# KnockBox-Games

A game hosting platform for collaborative and competitive multiplayer web games. Drop an HTML5 or
WASM game (hand-written, or a Godot/Unity web export) into `games/` and it becomes playable — no
server code, no restart. Games use the **KnockBox** client library (`web/knockbox.js`) to send and
receive messages over a websocket; the server owns discovery, lobbies, anonymous player identity,
and message routing, while games own all logic and state.

- **Players:** open the site, pick a game, create or join a lobby.
- **Server managers:** drop a game folder into `games/`; it hot-reloads in.
- **Game developers:** see [`docs/GAME_DEVELOPER_GUIDE.md`](docs/GAME_DEVELOPER_GUIDE.md).
- **Architecture:** see [`docs/INFRASTRUCTURE.md`](docs/INFRASTRUCTURE.md).

Run locally: `dotnet run --project KnockBox.Server --launch-profile http` (shell at
`http://localhost:5114`, games at `http://localhost:5115`).