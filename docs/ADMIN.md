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

**For a game that came from your read-only `games/` mount, Delete does not work, and the portal tells you
so up front.** `docker-compose.yml` mounts that directory `:ro` and the server only ever reads it, so the
button is disabled with the blocking path named. Use **Disabled** instead, or remove the file from the host
and let hot-reload notice.

**A game the portal installed deletes normally**, on every deployment: its package lives in the managed
root, which is writable by design. Prefer **Uninstall** on the Marketplace tab for those — same effect,
and it shows up in the operations list with everything else.

**Rescan Now** asks the catalog to re-scan immediately rather than waiting for the watcher or the poll.

### Lifecycle badges

Beside the availability badge you may see **Draining** or **Updating**. These are the install engine's
state, not yours: a game is Draining while it waits for running lobbies to finish before an update
applies, and Updating while its files are being swapped. In both, new lobbies are refused with a message
that tells the player to try again shortly — the game stays listed rather than vanishing from the grid and
reappearing a minute later.

While a badge is showing, the availability control and Delete are held: an availability write racing a
directory swap is arbitration the engine should not have to do. The card links across to the Marketplace
tab, where the operation is.

These deliberately are **not** options in the availability dropdown. That control is a command — picking
an option applies it — and offering a value the server would have to refuse is worse than not offering it.
They are also never persisted, so a server killed mid-update comes back with the game perfectly launchable
rather than stuck.

---

## 4. Marketplace & Packages

Where games come from and go. Three ways in: a marketplace catalog, a `.kbg` you upload, or (unchanged) a
file copied into `games/` by hand.

### What the statuses mean

| Status | Meaning |
| :-- | :-- |
| **Not installed** | Offered by a marketplace, not installed here. |
| **Up to date** | The installed version matches what is offered. |
| **Update available** | A newer version is published. |
| **Ahead of catalog** | The installed version is newer than the offered one — usually a hand-built package. |
| **Version unknown** | The game declares no version, so there is nothing to compare. Normal for hand-made games, and deliberately distinct from "update available" so every one of them isn't nagging you. |
| **Incompatible** | The offered version does not run on this server version. Never offered as an update — an update that can't run is worse than none. |
| **Unusable** | The catalog entry is malformed and can't be acted on. |
| **Installed** | Installed here, but no registered marketplace offers it — an upload, or an entry that was withdrawn. |

### Installing, updating, rolling back

One dropdown picks the version, because rolling back *is* targeting an older version you already hold. It
offers what the marketplace has, what is running now, and each retained backup; the button relabels itself
to **Install**, **Update**, **Reinstall** or **Roll back** to match.

Every path — a download, an upload, and a rollback — is validated by the same reader. A retained package
that has sat on disk for months is re-checked exactly like one off the network: age is not trust.

### What an update does to games in progress

| Mode | New lobbies | Running lobbies | When it applies |
| :-- | :-- | :-- | :-- |
| **When games finish** (drain) | refused | play on | once the last one ends |
| **Only if idle** (auto) | unaffected | untouched | immediately, or not at all |
| **Now (closes games)** (force) | refused | **closed** | immediately |

Drain is the default: it never interrupts anyone, and unlike auto it does not quietly give up because
somebody happened to be playing.

The per-game **update policy** is the same choice made standing: it says what the scheduled check may do
unattended. It is **Manual** for every game until you change it — nothing updates itself unless you enrol
it, and with nothing enrolled the scheduled check makes no outbound request at all.

### Operations

Anything that touches files runs as a **job**, because a download plus an extraction outlives any request
and a drain is open-ended. The list shows what is running and what recently finished, so switching tabs,
reloading, or closing the browser changes nothing — come back and the outcome is still there.

A job can be cancelled while it is queued, downloading, verifying or waiting for lobbies. **Once it starts
installing files it cannot be**: a half-swapped game directory is the one outcome worth refusing to create.

### Backups and rollback

Each update copies the previous package aside before overwriting it. `PackageBackupCount` (default 1) is
how many are kept; `0` turns backups off entirely and with them the ability to roll back. They count
toward the game's disk figure on the Game Catalog tab, so a large game's footprint roughly doubles.

Rolling back **swaps**: the version you leave becomes the retained one. With the default of 1 that means
two versions trade places, so repeated rollback toggles predictably instead of growing the folder.

### Uploading a package

**Upload .kbg…** takes a package built by `knockbox-pack`. A plain folder ZIP is refused, and says so: it
has no `KBG.json`, so no declared sizes, no per-file checksums and no unambiguous id — accepting one would
mean a second, weaker validation path beside the real reader.

Note the trust difference. A marketplace package is checked against a hash its catalog committed to. **An
uploaded one has no such hash** — it is validated for structure and safety, but the person supplying the
bytes is the only thing vouching for what is inside.

Once the bytes are accepted the request is over. Anything after that — a malformed archive, an id already
provided by `games/`, a full disk — surfaces on the job, not on the upload dialog.

### Sources

**Sources…** lists the registered marketplaces. The official one is built in: you can disable it, but not
remove it. Extra ones need an `https` catalog URL (plain `http` is allowed only against loopback, which is
what an offline mirror or a test uses), and are capped by `MarketplaceMaxSources`.

If two sources offer the same game id, the first wins and the loser's card says so rather than silently
disappearing. A source that can't be reached reports its error and does not stop the others — one dead
community feed must not hide the official catalog.

With `MarketplaceEnabled=false` this server fetches nothing and holds no HTTP client at all. Upload,
rollback, uninstall and the update policy all keep working.

---

## 5. System Logs

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

Availability overrides, maintenance mode, registered marketplaces and per-game update enrolments are
written to **`admin-settings.json`**, next to the admin
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
  },
  "updates": {
    "word-rush": "drain"
  },
  "sources": [
    {
      "id": "staging",
      "name": "Our staging repo",
      "catalogUrl": "https://example.com/.plugins/CATALOG.json",
      "downloadBaseUrl": "https://example.com",
      "enabled": true
    }
  ]
}
```

Defaults are recorded by **absence**: a game left Available has no `games` row, and one left Manual has no
`updates` row. Otherwise the file would accumulate an entry per game you ever looked at, and "no override"
and "explicitly the default" would become two ways to say one thing.

Two behaviours worth knowing:

- **A change always takes effect immediately, even if it can't be saved.** If the file is unwritable the
  portal tells you the change is live but will be lost on restart, rather than pretending nothing
  happened.
- **A file that can't be read is not fatal.** The server boots with platform defaults and reports the
  problem on the Overview tab — because from the outside, "policy lost" and "policy ignored" look
  identical, and the only symptom you'd otherwise notice is a disabled game serving players again. A
  single unusable row (a marketplace with no URL, say) is dropped on its own rather than costing you the
  rest of the file.

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

Package management and the marketplace (§4):

| Key | Default | What it does |
| :--- | :--- | :--- |
| `GamesManagedRoot` | sibling `games-managed` | Where portal-installed `.kbg` packages and their rollback backups live. Must be writable and **outside** the read-only games mount. Not regenerable — an uploaded package exists nowhere else. |
| `ManagedPackages` | `true` | Master switch for portal installs. Off ⇒ the root is never created and every install is refused with a reason; hand-placed packages still work. |
| `PackageBackupCount` | `1` | Previous versions retained per game for rollback. `0` keeps none. |
| `MaxConcurrentInstalls` | `1` | Downloads/extractions in flight at once. Bounds bandwidth and peak disk, not the number of jobs. |
| `PackageJobRetention` | `50` | Finished operations kept for the list. Never evicts a running one. |
| `MarketplaceEnabled` | `true` | Off ⇒ no catalog is fetched and the server holds no HTTP client at all. |
| `MarketplacePollMinutes` | `360` | Scheduled check for enrolled games. `0` disables it. With nothing enrolled it makes no request regardless. |
| `MarketplaceMaxSources` | `8` | Extra marketplaces that may be registered, beyond the built-in official one. |
| `MaxPackageBytes` | 512 MiB | Also the upload cap, enforced against bytes actually received. |

---

## 7. Not built yet

The portal covers live operations. Deliberately absent, and specified in
[issue 39](https://github.com/jcub1011/KnockBox-Games/issues/39) for later passes:

- **Runtime-editable rate limits and lobby caps.** The limits are read once at startup, and each
  connection builds its buckets from that snapshot.
- **Reserved and banned room codes.**
- **Player-facing announcement banners** (there is no wire message for one).
- **Outbound webhooks** on critical events.
- **Historical metric graphs.** The counters are cumulative; the portal differences them between polls but
  keeps no time series.
- **Signature verification beyond the published hash.** A catalog commits to a `sha256` and that is
  enforced on every download, but nothing is signed. See [MARKETPLACE.md](./MARKETPLACE.md).
- **Scheduled update windows.** The check runs on a fixed interval; there is no "only between 03:00 and
  05:00".
