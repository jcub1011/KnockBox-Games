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

### Run a prebuilt image

Instead of building from source you can pull a published image from GitHub Container Registry:

```bash
docker pull ghcr.io/jcub1011/knockbox-games:latest
```

Two tags are published:

| Tag | Channel | Built from |
|---|---|---|
| `:latest` | **Stable release** — run this in production. | A git release tag (`v1.2.3`). Versioned tags (`:1.2.3`, `:1.2`) are published alongside it if you want to pin. `:latest` tracks the most recently pushed `v*` tag, so release in increasing version order. |
| `:develop` | **Pre-release test build** — run an unstable build (e.g. to verify a deployment before promoting it to stable). | Every push to `main`. |

Published images are `linux/amd64` only (the server is a Native AOT `linux-x64` build) — they
will not run on ARM hosts.

> **First publish is private.** New GHCR packages start private. A maintainer sets the visibility
> to **Public** once — repo **Packages** → the package → **Package settings** → **Change
> visibility** → *Public*. Visibility is per **package**, not per tag, so this single flip exposes
> **both** `:latest` and `:develop` (and the version tags). That is intentional: a server admin who
> wants to run an unstable build can pull `:develop` with no credentials, just like `:latest`.

To use it with the compose file, comment out the `build:` block on the `knockbox` service and set
an `image:` instead (the commented lines are already there):

```yaml
services:
  knockbox:
    image: ghcr.io/jcub1011/knockbox-games:latest   # or :develop for the test channel
```

> **TrueNAS** (or any OCI host): point a Custom App at `ghcr.io/jcub1011/knockbox-games:latest`
> (or `:develop`). Once the package is public (see the one-time step above), no registry
> credentials are needed. Mount your games
> directory read-only at `/games` and a writable cache at `/app/games-compressed`, and map ports
> `8080`/`8081` 1:1 (or pin `KnockBox__GamesOrigin`) — same as the compose setup below.

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

### Behind Cloudflare Tunnel (cloudflared)

A complete, copy-paste way to serve KnockBox over HTTPS with a free Cloudflare Tunnel — no reverse
proxy to install, no TLS certificates to manage, and no ports opened on your router or firewall.

**What you'll end up with:** two HTTPS addresses on your domain —
- `play.example.com` — the shell, where players go;
- `games.example.com` — the game origin, which the shell loads game iframes from.

**Why two?** KnockBox runs each (untrusted) game in an iframe on a *separate* web origin, so a game
can't read players' identities or tamper with the shell. Origins are distinguished by hostname, so
the game origin needs its own. It does **not** need its own server, port, or container — both
hostnames point at the *same* KnockBox container, which tells them apart by hostname. (A single
hostname cannot work: the shell hostname deliberately refuses to serve game files.)

**Before you start** you need a domain managed by Cloudflare (free) and Docker installed. If you
haven't added your domain to Cloudflare yet, do that first — start here:
<https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/>

#### 1. Pick your two hostnames

Any two subdomains on your Cloudflare domain. Throughout this guide they're `play.example.com`
(shell) and `games.example.com` (games) — substitute your own everywhere.

#### 2. Create the tunnel and copy its token

In the Cloudflare dashboard go to **Zero Trust → Networks → Tunnels → Create a tunnel** and choose
the **Cloudflared** connector type. When it shows an installation command, you only need the long
**tunnel token** from it (the value after `--token`). Keep it for the next step.

The dashboard changes over time, so follow Cloudflare's current walkthrough:
<https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/get-started/create-remote-tunnel/>

#### 3. Start KnockBox and the tunnel together

Save this as `docker-compose.yml`. KnockBox has **no `ports:`** — only the tunnel can reach it, so
nothing is exposed to the internet directly.

```yaml
services:
  knockbox:
    image: ghcr.io/jcub1011/knockbox-games:latest
    environment:
      KnockBox__ForwardedHeaders: "true"                          # serve https/wss behind the tunnel — REQUIRED
      KnockBox__GamesHost: "games.example.com"                    # your GAMES hostname (no https://, no slash)
      KnockBox__AllowedOrigins__0: "https://play.example.com"     # your SHELL hostname (with https://, no trailing slash)
      KnockBox__AllowedOrigins__1: "https://games.example.com"    # your GAMES hostname (with https://, no trailing slash)
      KnockBox__GamesPollSeconds: "10"
    volumes:
      - /srv/knockbox/games:/games:ro                 # your game folders (read-only)
      - /srv/knockbox/games-compressed:/app/games-compressed
      - /srv/knockbox/logs:/app/logs
    restart: unless-stopped

  cloudflared:
    image: cloudflare/cloudflared:latest
    command: tunnel run
    environment:
      TUNNEL_TOKEN: "paste-your-tunnel-token-from-step-2"
    restart: unless-stopped
```

Create the host folders first and make them owned by UID `1654` (see the permissions notes earlier
in this section). Common slip-ups: `GamesHost` must **exactly** equal your games hostname, and the
two `AllowedOrigins` must have **no trailing slash**.

#### 4. Point both hostnames at KnockBox

Back in your tunnel's settings, add **two Public Hostnames**. The **Service** is identical for both
— it's the KnockBox container's name and internal port from the compose file (they share a network,
so Cloudflare's connector resolves `knockbox` by name):

| Public hostname     | Service          |
|---------------------|------------------|
| `play.example.com`  | `HTTP` `knockbox:8080` |
| `games.example.com` | `HTTP` `knockbox:8080` |

Cloudflare creates the DNS records automatically, and WebSockets work with no extra setting.

#### 5. Launch and check

```bash
docker compose up -d
```

Open `https://play.example.com`, create a lobby, and start a game. To confirm it's healthy, open
your browser's developer tools:
- **Console:** no "Mixed Content" error, and the game loads.
- **Network → WS:** the connection to `games.example.com/ws` shows **101 Switching Protocols**.
- The game iframe's address starts with `https://games.example.com/games/…` (not `http://`, no `:8081`).

If the home page shows a configuration warning instead of the lobby, it's almost always folder
permissions — see "The home page shows a configuration warning" below.

### Hot-reload on Docker Desktop

File-change events don't cross Windows/macOS bind mounts, so the image enables a polling fallback
(`KnockBox__GamesPollSeconds`, default 10 in the image; the compose file uses 5). On a Linux host
the watcher works natively and discovery is sub-second; polling stays on as a harmless safety net.

### The home page shows a configuration warning

The server is deliberately resilient to file-access problems: an unreadable games mount, a missing
shell, or an unwritable cache/log dir won't crash it. Instead it starts and **replaces the home page
with a warning** listing exactly what's wrong, so a misconfiguration is obvious during setup rather
than showing a blank or empty site. Almost always it's **permissions** — the container runs as
**UID 1654**, so:

- **Games folder not readable:** the mount must grant UID 1654 *read + execute*. `chown -R 1654`
  the games dir (read-only mounts still need read access). This one clears automatically once fixed —
  the games folder is re-checked continuously, no restart needed.
- **Pre-compressed cache / logs not writable:** `chown -R 1654` those dirs (these are warnings, not
  fatal — the server degrades to on-the-fly compression / console logging — but fix them for a proper
  deployment). Applies on the next restart.
- **Platform shell missing:** the web root has no `index.html`; verify the image/publish output or
  set `KnockBox__WebRoot`.

On TrueNAS, set ownership via **Datasets → Edit Permissions** if a plain `chown` doesn't stick (ACLs
override POSIX mode).

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
