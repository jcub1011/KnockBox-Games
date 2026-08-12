# The Admin Portal

The operator dashboard: what the server is doing right now, and the controls to act on it. It is served
at the **admin origin** — a third origin, isolated from the two players can reach — and every page and
API under it requires an admin session.

This document covers *using* the portal. For getting it reachable and claiming the password, see
[HOSTING.md § The admin portal](./HOSTING.md); for how the origin is routed and how sessions are signed,
see [INFRASTRUCTURE.md § The admin origin](./INFRASTRUCTURE.md).

> **The portal is not meant to be publicly reachable.** Access control is the port (or the subdomain) plus
> your firewall; the password is the second lock, not the first. The compose file binds it to loopback for
> exactly this reason.

---

## 1. Overview

The landing tab answers "is this server healthy, and what is it carrying".

| Card | What it is |
| :--- | :--- |
| Server Uptime | Process runtime. A restart drops every lobby and identity token by design. |
| Active Lobbies | Live lobbies, with how many are server-authority (each of those holds a Jint engine). |
| Connected Players | Live control (shell) sockets — one per player. The sub-line counts game sockets, i.e. players actually inside a game. |
| Registered Games | Discovered games, before availability policy. A disabled game still counts here. |
| Memory Working Set | Process working set, with the managed heap beneath it. |
| Process CPU | Measured **between polls**. The sub-line is the lifetime average, which stops moving once the server has been up a while and hides every spike. |

**Deployment problems** appear as a banner above the cards — an unreadable games mount, a missing web
root, an unwritable log directory, a settings file that couldn't be parsed. These are the same issues that
replace the *shell's* home page when they're blocking; they're repeated here so you don't have to open the
player site to find out something is wrong. They re-evaluate on every poll, so a fixed problem disappears
without a restart.

### Per-Game Server Cost

Games are HTML5/WASM and run in the player's browser, so it's tempting to treat them as free server-side.
They aren't:

- every connected socket holds a bounded outbound queue plus a writer task,
- a `to:"all"` broadcast serializes **once** and then sends **once per recipient**,
- and a **server-authority** game additionally runs its module here, in a per-lobby Jint engine.

The table breaks that down per game. `Frames in` is what clients sent for relay; `Frames out` counts each
recipient separately, so `Fan-out` is how much the game multiplies its own traffic. `Rate` is measured
between polls. **`Dropped`** is the column to care about: it counts frames evicted unsent because a
socket's queue was full, which means that client couldn't keep up.

### Maintenance Mode

Blocks new lobby creation across **every** game. Sessions already running are untouched and finish
normally — this is a drain, not a stop. Use it before a restart or a deploy.

The optional message is shown to a player whose lobby creation is refused, so
*"Back at 09:00 UTC"* beats the generic text. Maintenance mode is persisted (see §5), so it survives a
restart — including a restart you didn't intend.

---

## 2. Active Lobbies

Every lobby on the server, oldest first, filterable by game (title or id), room code, and status.

| Status | Meaning |
| :--- | :--- |
| `waiting` | Open to joins. |
| `in-game` | Closed to joins. **This is the normal state once play begins** — the game closes its own lobby. It is not "draining". |
| `stale` | Nobody in it holds a live shell socket, **or** nothing has happened for `AdminStaleLobbyMinutes` (default 30). |
| `empty` | No members at all. Should be momentary; the reaper closes these. |

`Uptime` counts from creation. `Idle` counts from the last relayed frame, join or leave — so a lobby whose
players wandered off shows a climbing idle time while its uptime keeps pace.

**Members** are chips; a dashed, faded chip is a member inside the reconnect grace window (their tab
dropped, they're still in the roster, and they have `DisconnectGraceSeconds` to come back). `(owner)`
marks the member holding the lobby powers — for a server-authority game that is the *owner*, not the
authority, which is the server itself.

### Actions

- **Close** one lobby. Its players get a `LobbyClosed` carrying your reason and return to the home page;
  their game sockets are cut. A server-authority lobby's engine is released too.
- **Kick** one member (the `×` on their chip). Same effect as a host kick, and they're barred from
  rejoining *that* lobby.
- **Purge Stale** closes everything `stale` or `empty`. This is the housekeeping button — it never touches
  a lobby someone is playing in.
- **Close All** closes every lobby on the server. It asks first, and it means it.

---

## 3. Game Catalog

One card per discovered game, with its disk footprint, what it's running right now, and where it came
from.

**Disk** is the total of three things, broken out beneath it:

- the game's own files (under `games/`, or under the unpacked-package root if it was installed from a
  `.kbg`),
- the pre-compressed `.br`/`.gz` variants the server derived from them,
- and the source `.kbg` archive, if there is one — it stays in `games/`, because that file is what the
  installer watches to decide whether the game should still exist.

Reporting only the first would understate a large WASM game by roughly the size of its own cache.

### Availability

| State | New lobbies | Existing lobbies | In the player catalogue |
| :--- | :--- | :--- | :--- |
| **Available** | Allowed | Allowed | Yes |
| **Disabled** | Refused | **Continue** | No |
| **Staged** | Allowed | Continue | No |

The column that surprises people is the third one: **changing availability never ends a session**. A game
you disable keeps its running lobbies until they finish. The portal says so when you disable a game that
has players in it, and if you need those sessions gone, close them from the Lobbies tab.

**Staged** is for keeping a game off the public grid while still being able to play it — an unreleased
title, a test build. The portal offers a **Copy launch link** button giving you `/?game=<id>`, which the
shell honours for an unlisted game.

> Staged is **visibility, not access control.** KnockBox has no player accounts, so there is no identity
> to authorize a launch against — anyone who has the link can use it. Don't stage a game you'd be unhappy
> for a stranger to open.

### Delete

Deletes the game's files: its directory, its compressed cache, and its source `.kbg`. Its running lobbies
are closed first, with a reason.

Every path is checked for writability **before** anything is closed or removed, so a delete either
completes or changes nothing. That matters because a half-delete is genuinely bad: remove the unpacked copy
while leaving the `.kbg` and the installer reinstalls the game on its next pass, so you'd watch a deletion
undo itself — having torn down live lobbies for nothing.

**On the recommended Docker deployment, Delete does not work, and the portal tells you so up front.**
`games/` is mounted read-only there (`docker-compose.yml` mounts it `:ro`, and the server only ever reads
it), so the button is disabled with the blocking path named. Use **Disabled** instead, or remove the file
from the host and let hot-reload notice. On the self-contained desktop build there is no such mount and
Delete works normally.

**Rescan Now** asks the catalog to re-scan immediately rather than waiting for the watcher or the poll.

---

## 4. System Logs

A live view of the server's log, read from an in-memory ring buffer (`AdminLogBufferSize`, default 2000
events) — **not** by tailing the log file. That matters for filtering: level and subsystem are still
structured fields here, so "errors only" and "just the `KnockBox.GameLog` category" are exact rather than
a guess at parsing rendered text.

- **Level** filters at that level and above.
- **Subsystem** matches part of Serilog's `SourceContext`. Useful ones: `GameLog` (output games emit via
  `kb.log`), `Authority` (server-authority modules), `GameCatalog`, `WebSocketHandler`, `LobbyCloser`.
- **Search** matches the message or the exception text.
- **Follow** keeps the stream advancing; untick it to read without new lines arriving. Auto-scroll only
  happens when you're already at the bottom.

Messages are rendered exactly as the log file renders them, so the two agree line for line.

**Download…** lists the rolling daily files under `LogsRoot` and downloads them raw — including today's,
which the server still has open for writing. The ring buffer only holds what happened since the server
started; anything older is only in the files.

---

## 5. Where operator policy is stored

Availability overrides and maintenance mode are written to **`admin-settings.json`**, next to the admin
password file (`AdminSettingsPath`; by default the same directory as `AdminPasswordPath`, which in the
image is the persisted `/app/data` volume).

Everything else about this server is deliberately ephemeral — a restart drops every lobby, token and
session. Policy isn't, because re-applying it by hand after every deploy is how a platform ships a game it
meant to keep hidden.

The file is indented and safe to hand-edit while the server is stopped:

```json
{
  "maintenanceMode": false,
  "maintenanceMessage": null,
  "games": {
    "unreleased-game": "staged",
    "broken-game": "disabled"
  }
}
```

Two behaviours worth knowing:

- **A change always takes effect immediately, even if it can't be saved.** If the file is unwritable the
  portal tells you the change is live but will be lost on restart, rather than pretending nothing
  happened.
- **A file that can't be read is not fatal.** The server boots with platform defaults and reports the
  problem on the Overview tab — because from the outside, "policy lost" and "policy ignored" look
  identical, and the only symptom you'd otherwise notice is a disabled game serving players again.

To reset all policy, delete the file and restart. To reset the password, delete `admin.secret` — which
also revokes every outstanding session.

---

## 6. Configuration

All keys take the `KnockBox:` prefix (`KnockBox__Key` as an environment variable).

| Key | Default | What it does |
| :--- | :--- | :--- |
| `AdminPort` | `5116` (dev), `8082` (image) | The admin origin's port. |
| `AdminHost` / `AdminOrigin` | — | Route the portal by subdomain instead of by port. |
| `AdminPasswordPath` | `admin.secret` beside the app | The PBKDF2 password hash. Must be writable and persisted. |
| `AdminSessionTtlHours` | `8` | Session cookie lifetime. Sessions also drop on restart. |
| `AdminSettingsPath` | `admin-settings.json` beside the password | Persisted operator policy (§5). |
| `AdminStaleLobbyMinutes` | `30` | Idle time before a lobby counts as stale. `0` judges staleness only by "nobody is connected". |
| `AdminLogBufferSize` | `2000` | Events held for the live log view. |
| `AdminDiskUsageCacheSeconds` | `60` | How long disk measurements are reused. `0` walks the directories on every read. |
| `AdminLoginAttemptsPerMinute` | `10` | Per-IP password attempts. |
| `AdminLoginAttemptsPerMinuteGlobal` | `60` | Server-wide password attempts, bounding PBKDF2 CPU regardless of the per-IP key. |

---

## 7. Not built yet

The portal covers live operations. Deliberately absent, and specified in
[issue 39](https://github.com/jcub1011/KnockBox-Games/issues/39) for later passes:

- **Marketplace UI and the update engine.** `MarketplaceClient` can fetch the catalog and download a
  verified package, but nothing installs one — dropping a `.kbg` into `games/` is still the only way in.
  See [MARKETPLACE.md](./MARKETPLACE.md).
- **Runtime-editable rate limits and lobby caps.** The limits are read once at startup, and each
  connection builds its buckets from that snapshot.
- **Reserved and banned room codes.**
- **Player-facing announcement banners** (there is no wire message for one).
- **Outbound webhooks** on critical events.
- **The Draining / Updating lifecycle states**, which arrive with the update engine.
- **Historical metric graphs.** The counters are cumulative; the portal differences them between polls but
  keeps no time series.
