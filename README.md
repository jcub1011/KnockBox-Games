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

## Install (Docker)

Run a prebuilt image from GitHub Container Registry with Docker Compose. Adjust the host paths
to folders you own (a TrueNAS dataset, `/srv/knockbox/...`, etc.), then `docker compose up -d`:

```yaml
services:
  knockbox:
    image: ghcr.io/jcub1011/knockbox-games:latest   # or :develop for the pre-release channel
    restart: unless-stopped
    ports:
      - "8080:8080"   # shell — players open this
      - "8081:8081"   # game origin — keep host:container 1:1 (see note below)
    volumes:
      - type: bind                       # your game library (read-only)
        source: /srv/knockbox/games
        target: /games
        read_only: true
      - type: bind                       # writable cache; must be owned by UID 1654
        source: /srv/knockbox/games-compressed
        target: /app/games-compressed
      # - type: bind                     # optional: persist logs
      #   source: /srv/knockbox/logs
      #   target: /app/logs
    environment:
      KnockBox__GamesPollSeconds: "10"   # hot-reload poll (bind-mount file events don't propagate)
```

Then open `http://<host>:8080` and drop game folders into your games directory — they hot-reload
in. The image is `linux/amd64` only. Keep the port mappings **1:1** (the server advertises its
internal game port `8081` to browsers); if you must change them, pin `KnockBox__GamesOrigin`.

**On TrueNAS SCALE:** keep the long-form mounts above — the short `:ro` form gets rewritten by
the app engine into an invalid spec. Put `source:` paths under your pool (`/mnt/<pool>/...`),
create those directories first (the host root is read-only, so Docker can't auto-create them),
and `chown -R 1654` the writable `games-compressed` dir.

See [`docs/HOSTING.md`](docs/HOSTING.md) for the full guide (TrueNAS, reverse proxy / TLS,
persistent caches, the `.env` quick start) and the repo's [`docker-compose.yml`](docker-compose.yml)
for a build-from-source setup.
