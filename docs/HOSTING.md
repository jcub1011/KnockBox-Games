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
```

The directory is mounted **read-only** (`:ro`) — the server never writes to it — so several server
instances can safely share one game library. `docker-compose.yml` contains a commented-out second
instance showing exactly that pattern.

> **Pre-compressed asset cache.** With `KnockBox__Precompress` on (the default), the server writes a
> `games-compressed/` cache of `.br`/`.gz` variants (built at max effort — the slow part of a cold
> boot) — it lives at `KnockBox__GamesCompressedRoot` (`/app/games-compressed` in the image), which
> must be **writable** and therefore **outside** the read-only `games/` mount. It is fully
> regenerable, so it *can* sit on container-local storage — but then it's wiped and rebuilt from
> scratch on every image update. To make it **survive updates** (and skip that full re-compression),
> the compose file mounts it on a volume: by default the Docker-managed `knockbox-compressed` named
> volume, or set `KNOCKBOX_COMPRESSED_DIR` to a host path to keep it on a disk you choose:
>
> ```bash
> KNOCKBOX_COMPRESSED_DIR=/srv/knockbox/games-compressed
> ```
>
> A **host path** must be writable by the container's non-root user (UID `1654`) — `chown -R 1654`
> the directory first, or the server can't write the cache and silently falls back to on-the-fly
> compression. A **named volume** (the default) gets the right ownership automatically, no setup.
> When several instances share one read-only library, give each its own compressed cache — it's
> writable and concurrent reconcilers would race. Disable the whole thing with
> `KnockBox__Precompress: "false"` to fall back to on-the-fly compression and write nothing.
>
> **On TrueNAS** (or any NAS), point both at datasets: `KNOCKBOX_GAMES_DIR` at a read-only games
> dataset and `KNOCKBOX_COMPRESSED_DIR` at a separate **writable** dataset owned by UID `1654`. Both
> then persist across app/image updates.

There are no secrets to configure. Player identities are anonymous, per-tab, and ephemeral by
design: a restart mints fresh ids, which is expected — in-memory lobbies drop on restart anyway.

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

Publish a self-contained build (a Native AOT compile — needs the MSVC C++ build tools, i.e. Visual
Studio's "Desktop development with C++" workload):

```bash
dotnet publish KnockBox.Server -p:PublishProfile=win-x64-desktop
# → KnockBox.Server/bin/publish/win-x64/
```

`KnockBox.Server.exe` is a native binary (no managed runtime alongside it). Copy that folder anywhere
and run it. Layout (the publish folder is `win-x64/`):

```
win-x64/
├─ KnockBox.Server.exe
├─ appsettings.json      # optional config (KnockBox:* keys)
├─ web/                  # platform shell (baked in by publish)
├─ games/                # auto-created on first run — drop game folders here
├─ games-compressed/     # auto-created — regenerable .br/.gz asset cache (rebuilt from games/)
└─ logs/                 # daily rolling logs
```

- With no configuration the exe serves the shell at `http://localhost:5114` and the games origin at
  `http://localhost:5115` — open `http://localhost:5114`. (Both origins must be served for games to
  load; the exe binds both automatically when you haven't set ports yourself.)
- To change the ports, set `ASPNETCORE_URLS` (e.g. `http://0.0.0.0:5114;http://0.0.0.0:5115`) together
  with `KnockBox:GamesPort` so the games origin matches. (The Docker image instead uses
  `ASPNETCORE_HTTP_PORTS="8080;8081"` — same effect, the newer port-only form.) Any explicit setting
  takes over from the built-in default above.
- For LAN play, bind `0.0.0.0` via `ASPNETCORE_URLS` (as above), allow both ports through Windows
  Firewall, and have players open `http://<your-LAN-IP>:5114` — the games origin is derived from the
  same host automatically.
- To store games (and/or the compressed cache) elsewhere — a data drive, a NAS share — set
  `KnockBox:GamesRoot` and/or `KnockBox:GamesCompressedRoot` to your paths. Three interchangeable
  ways to supply them (later wins): the `KnockBox` section of `appsettings.json` next to the exe —
  ```json
  "KnockBox": { "GamesRoot": "D:/KnockBoxData/games", "GamesCompressedRoot": "D:/KnockBoxData/games-compressed" }
  ```
  environment variables (`KnockBox__GamesRoot`, `KnockBox__GamesCompressedRoot`), or CLI args
  (`KnockBox.Server.exe --KnockBox:GamesRoot=D:\KnockBoxData\games`). An **absolute** path is used
  as-is; a **relative** one resolves against the exe's folder. Unlike Docker these are plain on-disk
  folders, so they already survive app updates — relocate them only to put data on a chosen disk or
  share. `games-compressed/` must be writable; it's regenerable, so deleting it just triggers a
  rebuild. (Set `KnockBox:Precompress` to `false` to skip the cache entirely.)

---

## 3. Configuration reference

All keys live under `KnockBox:` in `appsettings.json`, or as environment variables with `__`
separators (`KnockBox__GamesRoot`). The full table is in
[INFRASTRUCTURE.md §9](./INFRASTRUCTURE.md#9-running-locally); the hosting-relevant ones:

| Key | Default | Purpose |
|---|---|---|
| `WebRoot` / `GamesRoot` / `LogsRoot` | auto-resolved | Override where the shell / games / logs live. Relative paths resolve against the app's content root. |
| `Precompress` | `true` | Keep a `.br`/`.gz` cache of game assets and serve it via `Accept-Encoding`; `false` ⇒ on-the-fly compression only, writes nothing. |
| `GamesCompressedRoot` | `/app/games-compressed` (Docker) | Where the pre-compressed cache lives. Must be **writable** and outside the read-only `games/` mount. Mount a volume / host path here to persist it across updates (see above). |
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
