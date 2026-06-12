# KnockBox Games — Hosting Guide

How to run a KnockBox server as an admin: Docker (recommended) or a plain desktop app. Either way,
hosting a game is the same: **copy its folder into your games directory** — the server discovers it
within seconds, no restart, no code.

> For how the platform works internally, see [INFRASTRUCTURE.md](./INFRASTRUCTURE.md). For building
> a game, see [GAME_DEVELOPER_GUIDE.md](./GAME_DEVELOPER_GUIDE.md).

---

## 1. Docker (recommended)

```bash
# From the repo root:
docker compose up -d --build
# → shell at http://localhost:8080, games origin at http://localhost:8081
```

Drop a game folder into `./games/` (or your configured games dir) and it appears in the lobby
browser within a few seconds.

### Use a stable games directory

Your games live **outside** the container, in any host directory you choose — they survive image
updates, container recreation, and restarts. Point the compose file at it with a `.env` file next
to `docker-compose.yml`:

```bash
KNOCKBOX_GAMES_DIR=/srv/knockbox/games
KNOCKBOX_TOKEN_SECRET=<a long random string>
```

The directory is mounted **read-only** (`:ro`) — the server never writes to it — so several server
instances can safely share one game library. `docker-compose.yml` contains a commented-out second
instance showing exactly that pattern.

Set `KNOCKBOX_TOKEN_SECRET` for any real deployment: it keeps player identity tokens valid across
container restarts (without it, a restart logs everyone out of their anonymous identities).

### Port-mapping foot-gun

The server tells browsers to load games from its **internal** games port (8081). Keep host:container
mappings 1:1 (`8080:8080`, `8081:8081`) — or, if your host ports differ, pin the games origin
explicitly:

```yaml
environment:
  KnockBox__GamesOrigin: "http://your-host:8091"
```

### Behind a reverse proxy (TLS)

Terminate TLS at your proxy (Caddy, nginx, Traefik) and run the container plain-HTTP behind it:

1. Set `KnockBox__ForwardedHeaders: "true"` so the server trusts `X-Forwarded-Proto/Host/For` —
   without it, game origins resolve to `http://`/`ws://` and break under HTTPS.
2. Either keep two ports (proxy `play.example.com` → 8080 and `games.example.com` → 8081), or use
   single-port mode: route both hosts to 8080 and set `KnockBox__GamesHost: "games.example.com"`
   (the server routes by `Host` header).
3. Lock down origins: `KnockBox__AllowedOrigins__0/1` to your two public origins.
4. Make sure the proxy allows WebSocket upgrade on `/ws`.

### Hot-reload on Docker Desktop

File-change events don't cross Windows/macOS bind mounts, so the image enables a polling fallback
(`KnockBox__GamesPollSeconds`, default 10 in the image; the compose file uses 5). On a Linux host
the watcher works natively and discovery is sub-second; polling stays on as a harmless safety net.

---

## 2. Desktop app (no Docker, no .NET install)

Publish a self-contained build:

```bash
dotnet publish KnockBox.Server -p:PublishProfile=win-x64-desktop
# → KnockBox.Server/bin/publish/win-x64/
```

Copy that folder anywhere and run `KnockBox.Server.exe`. Layout:

```
KnockBoxServer/
├─ KnockBox.Server.exe
├─ appsettings.json      # optional config (KnockBox:* keys)
├─ web/                  # platform shell (baked in by publish)
├─ games/                # auto-created on first run — drop game folders here
└─ logs/                 # daily rolling logs
```

- Default ports are Kestrel's unless configured; set them with the `ASPNETCORE_URLS` environment
  variable or `appsettings.json` (e.g. `"Urls": "http://0.0.0.0:5114;http://0.0.0.0:5115"` with
  `KnockBox:GamesPort: 5115`).
- For LAN play, allow the two ports through Windows Firewall and have players open
  `http://<your-LAN-IP>:5114` — the games origin is derived from the same host automatically.
- To use a games folder elsewhere (e.g. a NAS share), set `KnockBox:GamesRoot` to its path.

---

## 3. Configuration reference

All keys live under `KnockBox:` in `appsettings.json`, or as environment variables with `__`
separators (`KnockBox__GamesRoot`). The full table is in
[INFRASTRUCTURE.md §9](./INFRASTRUCTURE.md#9-running-locally); the hosting-relevant ones:

| Key | Default | Purpose |
|---|---|---|
| `TokenSecret` | random per process | Set it so identities survive restarts. |
| `WebRoot` / `GamesRoot` / `LogsRoot` | auto-resolved | Override where the shell / games / logs live. Relative paths resolve against the app's content root. |
| `GamesPollSeconds` | `0` (off; `10` in Docker) | Polling fallback for games hot-reload where file watching doesn't work (bind mounts). |
| `GamesPort` / `GamesHost` / `GamesOrigin` | `5115` / — / — | How the separate game origin is addressed (port in dev, subdomain or explicit origin in prod). |
| `ForwardedHeaders` | `false` | Trust `X-Forwarded-*` from a fronting reverse proxy. |
| `AllowedOrigins` | `[]` (allow all) | `/ws` Origin allowlist — set for production. |

### Abuse protection (public servers)

Defaults are sized for casual play; `0` disables any of them:

| Key | Default | Purpose |
|---|---|---|
| `HandshakeTimeoutSeconds` | `10` | A socket must send its first frame within this deadline. |
| `MaxConnectionsPerIp` | `32` | Concurrent `/ws` sockets per client IP (a player uses 2 per tab). Needs `ForwardedHeaders` behind a proxy. |
| `GameMessagesPerSecond` / `GameMessagesBurst` | `30` / `60` | Per-connection in-game message rate; sustained violation closes the socket terminally (`1008`). |
| `ControlMessagesPerSecond` / `ControlMessagesBurst` | `5` / `10` | Same, for shell/lobby traffic. |
| `LobbyCreatesPerMinute` | `10` | Per-player lobby-creation rate (rejects the create, keeps the connection). |
