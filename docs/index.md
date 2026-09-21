# KnockBox-Games Docs

KnockBox is a game-hosting platform for multiplayer web games. Drop an HTML5/WASM
game into `games/` — as a single `.kbg` package or a plain folder — and it becomes
playable with no server code and no restart.

Use the search box above to search within these docs, or pick an audience:

## Operate a server

- [Hosting Guide](HOSTING.md) — Docker, TrueNAS, reverse proxies / TLS, updating.
- [Admin Portal](ADMIN.md) — lobbies, catalog, marketplace installs, logs, limits.
- [Marketplace](MARKETPLACE.md) — where server games come from and how updates work.

## Build a game

- [Game Developer Guide](GAME_DEVELOPER_GUIDE.md) — manifests, networking, packaging.
- [KBG Package Format](KBG_FORMAT.md) — the single-file game package spec.
- [Server Authority](SERVER_AUTHORITY_DESIGN.md) — server-authoritative game logic.

## Reference

- [Infrastructure](INFRASTRUCTURE.md) — architecture, origins, config reference.
- [Client Addons](ADDONS.md) — distributing the Godot / Phaser / web clients.
