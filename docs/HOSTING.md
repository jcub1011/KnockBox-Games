# KnockBox Games — Hosting Guide

How to run a KnockBox server as an admin: Docker (recommended) or a plain desktop app. Either way,
hosting a game is the same: **copy it into your games directory** — the server picks it up within
seconds, no restart, no code.

A game arrives in one of two forms, and both work the same way from your side:

- a **`.kbg` package** — one file, the normal way a game is distributed. The server unpacks and
  installs it for you; you never need to unzip anything or run a command.
- a **plain folder** — the older layout, still fully supported. Handy if you're editing a game
  yourself.

If a folder and a package ever supply the same game id, the folder wins and the server logs a warning
naming both.

> For how the platform works internally, see [INFRASTRUCTURE.md](./INFRASTRUCTURE.md). For building
> a game, see [GAME_DEVELOPER_GUIDE.md](./GAME_DEVELOPER_GUIDE.md).

---

## 1. Docker (recommended)

```bash
# From the repo root:
docker compose up -d --build
# → shell at http://localhost:8080, games origin at http://localhost:8081
```

Copy a `.kbg` game package (or a plain game folder) into `./games/` — or your configured games dir —
and it appears in the lobby browser within a few seconds.

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

> **Unpacked game packages.** The same story, for the same reason. A `.kbg` cannot be expanded inside
> the read-only games mount, so the server extracts it to `KnockBox__GamesUnpackedRoot`
> (`/app/games-unpacked` in the image) — also **writable** and **outside** `games/`. It is
> regenerable, so container-local storage works, but then the whole library is re-extracted on every
> image update. To persist it, the compose file mounts the Docker-managed `knockbox-unpacked` named
> volume by default, or set a host path:
>
> ```bash
> KNOCKBOX_UNPACKED_DIR=/srv/knockbox/games-unpacked
> ```
>
> Same ownership rule: a **host path** must be `chown -R 1654`, a **named volume** handles it for you.
> Give each instance its **own** unpacked directory when several share one library — each one extracts
> and prunes independently and they would race. Unlike the compressed cache, an unwritable location
> here is not merely slower: `.kbg` packages **cannot install at all**, so the home page shows a
> configuration warning saying so (plain game folders keep working). Set
> `KnockBox__Packages: "false"` to ignore packages entirely and support only folders.
>
> A **server-authoritative** game packages like any other: its `serverAuthority` module and any
> `authorityWords` dictionaries are extracted here with the rest of the build, and the server runs
> them from this directory. They are still never served on the game origin, so this directory holds
> files that are readable server-side but not over HTTP — worth knowing if the games library contains
> hidden-information answer lists.

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

The **admin** port is exempt: nothing advertises it to a browser, so you can remap 8082 to any host port
you like (keeping it bound to `127.0.0.1`).

### The admin portal

A **third** origin (container port **8082**, `5116` for the desktop exe) serves an operator dashboard:
uptime, active lobbies, registered games, memory. It is a separate origin so that no page a player can
browse can reach it — every `/admin*` path returns 404 on the shell and games origins.

**It is claim-on-first-use.** There are no accounts: until a password is set the portal is *unclaimed*,
and the first person to open it sets the password. So:

- **Do not publish the admin port.** `docker-compose.yml` maps it to `127.0.0.1:8082:8082` deliberately —
  reachable from the host only. Reach a remote server over an SSH tunnel instead:
  `ssh -L 8082:localhost:8082 you@server`, then open `http://localhost:8082`.
- **Claim it right after the first `docker compose up`**, before anything else can.
- If you do want it reachable over the network, put it behind your proxy with its own authentication and
  set `KnockBox__AdminHost`/`KnockBox__AdminOrigin` — don't simply widen the port mapping.

**Password rules.** Minimum 12 characters. Attempts are rate-limited to
`KnockBox__AdminLoginAttemptsPerMinute` (default 10) per client IP, and a throttled attempt gets `429` with
`Retry-After`. That limit is doing more than stopping password guessing: each attempt deliberately costs a
600k-iteration PBKDF2 (~0.4 s of one core), so without it anyone who can reach the port could saturate your
CPU with unauthenticated requests and starve the game relay. Behind a proxy, set
`KnockBox__ForwardedHeaders: "true"` or every request shares the proxy's IP and one bucket.

**Where the password lives.** Hashed (PBKDF2-HMAC-SHA256, 600k iterations) into
`KnockBox__AdminPasswordPath` — `/app/data/admin.secret` in the image, on the `knockbox-admin` volume
(override with `KNOCKBOX_ADMIN_DIR`). Unlike the two asset caches this is **not** regenerable: lose it and
the portal reverts to unclaimed. Keep the volume, and note that the path must be writable by UID `1654`
if you bind-mount a host directory. The file is created mode `600` so another account on the box can't read
the hash and attack it offline — if you bind-mount a host directory, don't loosen that.

**To reset a forgotten password**, delete the secret file and reload the portal — it returns to setup mode:

```bash
docker compose exec knockbox rm -f /app/data/admin.secret
```

**Resetting also revokes every admin session immediately.** The session-cookie signing key is derived from
a per-process secret *and* a fingerprint of the stored hash, so any change to that file invalidates
outstanding cookies — resetting a password you believe is compromised really does lock the other party out,
rather than leaving their session working until the next restart. A restart ends every session too, and
`KnockBox__AdminSessionTtlHours` (default 8) bounds one otherwise.

> **The secret file is the credential.** Anyone who can write it controls the portal — they can delete it
> and claim a new password, or restore an old copy to bring an old password back. That is true of any
> file-backed credential without external state (`/etc/shadow` included), and it is not something the
> server can detect: a rollback check would need state the same attacker could roll back. **Filesystem
> permissions on that path are the real security boundary**, so keep the volume owned by UID `1654` and
> off any share other people can write. What the server does guarantee is that sessions follow the current
> file exactly, so a swap never leaves both the old and new holders logged in.

> `KnockBox__AdminHost` routes by `Host` header, exactly like `GamesHost`. Once it is set, **any** request
> carrying that host reaches the admin app — including one arriving on the public port, where the `/admin*`
> 404 gate no longer applies. Only set it behind a proxy you trust to set `Host`, together with
> `KnockBox__ForwardedHeaders: "true"`.

### Behind a reverse proxy (TLS)

Terminate TLS at your proxy (Caddy, nginx, Traefik) and run the container plain-HTTP behind it:

1. Set `KnockBox__ForwardedHeaders: "true"` so the server trusts `X-Forwarded-Proto/Host/For` —
   without it, game origins resolve to `http://`/`ws://` and break under HTTPS.
2. Either keep two ports (proxy `play.example.com` → 8080 and `games.example.com` → 8081), or use
   single-port mode: route both hosts to 8080 and set `KnockBox__GamesHost: "games.example.com"`
   (the server routes by `Host` header).
3. Lock down origins: `KnockBox__AllowedOrigins__0/1` to your two public origins.
4. Make sure the proxy allows WebSocket upgrade on `/ws`.
5. Leave the **admin** port (8082) out of the proxy unless you are deliberately exposing the portal —
   see [The admin portal](#the-admin-portal). Publishing it also makes the session cookie `Secure`
   automatically, since the server sees the forwarded `https` scheme.

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
- **Game packages could not be installed:** either the unpacked-package dir isn't writable
  (`chown -R 1654` it) or a specific `.kbg` is malformed — the warning names the file and the reason,
  and the server log carries the same message. This is treated as **fatal** when `.kbg` packages are
  present but no games got installed, because the site then has nothing to serve; it clears on its own
  once they install. Plain game folders are unaffected either way.
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
├─ games/                # auto-created on first run — copy .kbg packages or game folders here
├─ games-compressed/     # auto-created — regenerable .br/.gz asset cache (rebuilt from games/)
├─ games-unpacked/       # auto-created — games extracted from .kbg packages (regenerable)
├─ admin.secret          # created when you set an admin password — NOT regenerable; delete to reset
└─ logs/                 # daily rolling logs
```

- With no configuration the exe serves the shell at `http://localhost:5114`, the games origin at
  `http://localhost:5115`, and the admin portal at `http://localhost:5116` — open
  `http://localhost:5114`. (The first two must both be served for games to load; the exe binds all
  three automatically when you haven't set ports yourself.)
- To change the ports, set `ASPNETCORE_URLS` — listing **every** origin, e.g.
  `http://0.0.0.0:5114;http://0.0.0.0:5115;http://0.0.0.0:5116` — together with `KnockBox:GamesPort`
  and `KnockBox:AdminPort` so those origins match. (The Docker image instead uses
  `ASPNETCORE_HTTP_PORTS="8080;8081;8082"` — same effect, the newer port-only form.) **Any explicit
  setting takes over from the built-in default above completely**: it replaces the port list rather
  than adding to it, so an origin you leave out is never listened on and answers `connection refused`
  — even though `GamesPort`/`AdminPort` still route it. Watch the startup log: it prints the address
  each origin actually bound, and warns `Admin portal is UNREACHABLE …` when the admin port isn't
  among them.
- For LAN play, bind `0.0.0.0` via `ASPNETCORE_URLS` (as above), allow the **shell and games** ports
  through Windows Firewall, and have players open `http://<your-LAN-IP>:5114` — the games origin is
  derived from the same host automatically. Leave the **admin** port on `localhost` (don't add
  `0.0.0.0:5116`, don't open it in the firewall) unless you have set an admin password already — see
  [The admin portal](#the-admin-portal).
- To store games (and/or the two derived caches) elsewhere — a data drive, a NAS share — set
  `KnockBox:GamesRoot`, `KnockBox:GamesCompressedRoot` and/or `KnockBox:GamesUnpackedRoot` to your
  paths. Three interchangeable
  ways to supply them (later wins): the `KnockBox` section of `appsettings.json` next to the exe —
  ```json
  "KnockBox": { "GamesRoot": "D:/KnockBoxData/games", "GamesCompressedRoot": "D:/KnockBoxData/games-compressed" }
  ```
  environment variables (`KnockBox__GamesRoot`, `KnockBox__GamesCompressedRoot`), or CLI args
  (`KnockBox.Server.exe --KnockBox:GamesRoot=D:\KnockBoxData\games`). An **absolute** path is used
  as-is; a **relative** one resolves against the exe's folder. Unlike Docker these are plain on-disk
  folders, so they already survive app updates — relocate them only to put data on a chosen disk or
  share. `games-compressed/` and `games-unpacked/` must both be writable and stay **outside**
  `games/` (the server refuses an overlapping configuration); both are regenerable, so deleting either
  just triggers a rebuild. (Set `KnockBox:Precompress` or `KnockBox:Packages` to `false` to skip the
  respective one entirely.)

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
| `Packages` | `true` | Install `.kbg` game packages copied into the games dir. `false` ⇒ only plain game folders are supported. |
| `GamesUnpackedRoot` | `/app/games-unpacked` (Docker) | Where games extracted from `.kbg` packages live. Must be **writable** and outside the read-only `games/` mount. Mount a volume / host path here to avoid re-extracting the library on every update (see above). |
| `MaxPackageBytes` / `MaxPackageEntries` / `MaxPackageRatio` | 512 MiB / `20000` / `200` | Ceilings that stop a malformed or malicious package filling the disk. Raise `MaxPackageBytes` only if you host a genuinely larger game; `0` disables a check. |
| `GamesPollSeconds` | `0` (off; `10` in Docker) | Polling fallback for games hot-reload where file watching doesn't work (bind mounts). |
| `GamesPort` / `GamesHost` / `GamesOrigin` | `5115` / — / — | How the separate game origin is addressed (port in dev, subdomain or explicit origin in prod). |
| `AdminPort` / `AdminHost` / `AdminOrigin` | `5116` (`8082` Docker) / — / — | How the admin portal origin is addressed. Do **not** expose it publicly — see [The admin portal](#the-admin-portal). |
| `AdminPasswordPath` | `admin.secret` next to the exe (`/app/data/admin.secret` Docker) | Where the admin password hash is stored. Must be **writable** and, in Docker, on a **persisted volume** — otherwise the password is lost on every image update. Delete the file to reset the password. |
| `AdminSessionTtlHours` | `8` | Admin session-cookie lifetime. A restart also ends every admin session. |
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
| `AdminLoginAttemptsPerMinute` | `10` | Per-IP admin password attempts (`429` + `Retry-After` over the limit). Guards CPU as much as the password: each attempt costs ~0.4 s of a core. Needs `ForwardedHeaders` behind a proxy. |
