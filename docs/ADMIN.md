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

**Recent History** graphs the same four numbers over the retained window. The samples are taken by the
**server**, not by this page, so the graphs are populated the moment you open the portal — including on a
different machine, and including after a reload. That matters because you open this page when something has
already gone wrong. `MetricSampleSeconds=0` turns it off, and the card says so rather than drawing an empty
chart.

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

**`Authority CPU`** is the only real per-game CPU figure this server has, and it is measured rather than
estimated: the total time this process has spent executing that game's authority module, with the mean per
call. A game without a `serverAuthority` module shows **`--`**, not `0.00s` — it runs entirely in the
player's browser and executes nothing here, which is a different statement from "used no measurable CPU".

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

**Sources…** lists the registered marketplaces, each with a **Disable** button. Disabling keeps a source's
configuration but stops it being fetched, so it offers nothing until you switch it back on — the official
one is built in and this is the only control it has, since it cannot be removed. Extra ones need an `https`
catalog URL (plain `http` is allowed only against loopback, which is what an offline mirror or a test uses),
and are capped by `MarketplaceMaxSources`. The official source's off switch is the one marketplace setting
stored under its own key (`officialSourceDisabled`) rather than in `sources`, because it has no row there —
its URLs come from configuration.

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

## 6. Platform

Settings, not a live view — and the one tab that **does not poll**. Everything here is a form, and a timer
would overwrite what you are halfway through typing. It reads when you open the tab, after every save, and
when you click Refresh.

### Limits & Caps

Abuse protection and capacity, editable at runtime. A change is **in force immediately, including for
sockets that are already open** — which is the whole point: the connections a flood is arriving on are, by
definition, already connected. It is also saved, so it survives a restart.

| Field | What it bounds |
| :--- | :--- |
| Control messages / second, Control burst | Lobby operations from one shell socket. Sustained spam past the burst closes that connection. |
| Game messages / second, Game burst | Frames from one game socket. Every frame fans out per recipient, so inbound spam multiplies. |
| Lobby creates / minute | Per player. Refuses the operation without closing the connection. |
| Connections per IP | One player legitimately holds two (shell + game) per tab. Only meaningful with `ForwardedHeaders` behind a proxy. |
| Max lobbies (platform) | Total simultaneous lobbies across every game. |
| Max lobbies per game | Stops one popular title consuming every remaining slot. |

Three rules worth knowing:

- **Empty means "use the default"** — the value from configuration, shown as the field's placeholder. That is
  also how you revert one field: clear the box and save. **0** is different: it means *disable this limit
  entirely*.
- **A lowered cap never disconnects anyone, and never closes a lobby.** It refuses the next connection or
  the next lobby. If you cap lobbies below what is already running, the running ones finish and no new ones
  start until the count falls under the cap; the portal says so when you save.
- **A rate above 0 with a burst below 1 is refused.** It would refuse *every* message forever, which for the
  control plane means nobody can create or join a lobby again until someone hand-edits the settings file.

**Set in Configuration** lists four limits that are deliberately *not* editable here. The handshake timeout
and the reconnect grace window are read when the server starts (the reaper's own interval is derived from
the grace window), and the two admin-login caps bound the CPU an unauthenticated caller can spend on
password hashing — a lock that opens from inside the room it protects is not a lock. Change those with
configuration and restart.

### Update Schedule

When this server checks its marketplaces for newer versions of the games you enrolled in automatic updates
(§4). **Daily at 03:00 UTC** unless you change it — a catalog changes a handful of times a year, an enrolled
game updating within a day of publication is well inside what anyone expects, and a check that finds
something ends in a game being swapped, which should land when the fewest people are playing.

- **Cadence** is Never, Hourly, Daily or Weekly. Weekly also takes a day; daily and weekly take an hour.
- **Times are UTC**, so the schedule does not move when the host's time zone or its tzdata does, and does
  not shift by an hour twice a year. The line under the form restates the next run in *your* zone, which is
  the only way to check that what you set is what you meant.
- **A check also runs ~30 s after every start.** Someone who has just restarted a server is exactly the
  person who wants to know whether anything is out of date, and waiting until the small hours to find out
  is not useful. It costs nothing on a default deployment: with nothing enrolled, a pass makes no outbound
  request at all.
- Each due time carries **up to 5 minutes of jitter**, so a fleet on the same schedule doesn't reach the
  catalog host at the same second.
- The schedule does not gate **Refresh Catalog** on the Marketplace tab. That checks right now, always.
- A schedule with nothing enrolled installs nothing — the form says so rather than leaving you to wonder
  why a schedule you set never does anything.

This card is on the Platform tab rather than the Marketplace tab because it is a form, and Platform is the
one tab that never polls. The Marketplace tab re-reads its catalog whenever an operation finishes, which
would re-render a half-typed field.

### Player Announcement

One banner, shown on the player home page until they dismiss it or you clear it. Everyone connected sees it
immediately; anyone who arrives later is sent it on connect, so a notice does not only reach whoever
happened to be online when you posted it.

- **Scope** is all games, or one game — a game-scoped notice is shown labelled with that game's title.
- **Severity** is Information or Warning; Warning is the one that catches an eye already looking elsewhere.
- **Editing is re-posting.** Each post gets a new id and a dismissal is remembered against that id, so an
  edited notice comes back for everyone who dismissed the previous wording.
- It is **purely informational**. It stops nothing — maintenance mode (§1) is the control that blocks new
  lobbies. Announcing something and doing it are separate acts on purpose.

### Banned Room Codes

Codes the generator will never hand out. Two kinds of entry:

- A **word** is blocked as a substring anywhere in a code: `XQ` blocks `XQ4B` and `7XQ2`.
- A **pattern** matches a whole code, where `?` is one character and `*` any run: `Q7*` blocks every code
  starting `Q7`, and `?K??` every code whose second character is `K`.

Matching is case-insensitive. Patterns are globs, deliberately **not** regular expressions — this runs on
the lobby-create path, and an operator-typed regex there is a denial-of-service lever pointed at the thing
every player needs.

The card reports **exactly** how much of the code space a list removes, counted by walking all 1,048,576
possible codes. A list that would remove more than **50%** is refused: past that, lobby creation starts
failing for a reason no player could act on. Codes already in use are never revoked, and an entry using a
character the alphabet leaves out (`O`, `0`, `I`, `1` — too easily misread aloud) is flagged as unreachable
rather than silently doing nothing.

### Webhooks

Outbound HTTP POSTs on platform events, to Discord, Slack, or any endpoint that accepts JSON.

| Event | Fires when |
| :--- | :--- |
| Errors | Any error-or-worse log event. Rate-limited — see below. |
| Update applied | A game finished installing or updating. |
| Update failed | An install, update, rollback or uninstall failed or was cancelled. |
| Maintenance toggled | Global maintenance mode went on or off. |
| Resource threshold | Memory or CPU crossed the configured threshold, or came back under it. |

- **Select no events and the endpoint receives all of them** — a registered endpoint that silently receives
  nothing is the worse of the two possible surprises.
- The URL must be **https**, or **http on loopback** (for a local monitoring agent). That is the same rule
  the package downloader applies to a marketplace URL.
- **One attempt per event, no retries.** The last result per endpoint is shown instead, so a dead endpoint is
  visible without turning one failed delivery into several at the worst possible moment.
- **Error alerts are capped** (`WebhookErrorsPerMinute`, default 6/min) and the next delivery carries a count
  of what was suppressed. An error storm is exactly when this fires most and is worth least per message.
  Set it to **`0` to send no error alerts at all** — unlike the connection rate limits, `0` here is off, not
  unlimited, because the value you reach for to quieten a chat channel must not be the one that floods it.
  The other event kinds (maintenance, updates, resource thresholds) are unaffected.
- The payload carries the same one-line summary as `content` **and** `text`, which is what makes one POST
  render in Discord *and* Slack with no per-service configuration, alongside structured fields
  (`event`, `at`, `server`, `gameId`, `level`) for a real monitoring endpoint.
- Only the **origin** of each URL is shown in the table, and the URL is never logged: a webhook URL is a
  bearer credential — anyone holding it can post to that channel.
- **Test** sends through the real delivery path and reports the actual outcome, so you can check a URL before
  enabling it.

Resource-threshold alerts are **edge-triggered**: crossing fires once, and coming back under fires once.
Both thresholds are off by default, because a number that fits one host is noise on another.

---

## 7. Where operator policy is stored

Availability overrides, maintenance mode, registered marketplaces, per-game update enrolments, the update
schedule, runtime limit
overrides, the room-code blocklist, the live announcement and the webhook endpoints are all written to
**`admin-settings.json`**, next to the admin
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
  "schedule": {
    "cadence": "weekly",
    "dayOfWeek": "tuesday",
    "hourUtc": 14
  },
  "limits": {
    "maxLobbies": 40,
    "maxLobbiesPerGame": 8
  },
  "roomCodes": {
    "words": ["XQ"],
    "patterns": ["Q7*"]
  },
  "announcement": {
    "id": "9f2c…",
    "text": "Scheduled maintenance at 09:00 UTC.",
    "postedAt": "2026-08-13T10:00:00+00:00",
    "severity": "warning",
    "gameId": null
  },
  "webhooks": [
    {
      "id": "ops",
      "name": "Ops channel",
      "url": "https://discord.com/api/webhooks/…",
      "events": ["logError", "updateFailed"],
      "enabled": true
    }
  ],
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

Defaults are recorded by **absence**: a game left Available has no `games` row, one left Manual has no
`updates` row, a limit left at its default has no `limits` entry, a schedule you never chose has no
`schedule` object (the configured one stands), and an empty blocklist or webhook
list is omitted entirely. Otherwise the file would accumulate an entry per game you ever looked at, and "no override"
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

## 8. Configuration

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
| `MetricSampleSeconds` | `15` | How often the dashboard's time series takes a sample. `0` = no history and no graphs. |
| `MetricHistoryPoints` | `240` | Samples retained. 240 x 15s = one hour; memory is a fixed handful of numbers per sample plus one small row per game. |

Capacity and abuse limits are editable from the portal (§6) and persisted, so these are the **starting**
values a fresh deployment gets:

| Key | Default | What it does |
| :--- | :--- | :--- |
| `MaxLobbies` | `0` (unlimited) | Cap on simultaneous lobbies across every game. |
| `MaxLobbiesPerGame` | `0` (unlimited) | Cap per game, so one popular title can't take every slot. |
| `ControlMessagesPerSecond` / `…Burst` | `5` / `10` | Lobby operations per shell socket. |
| `GameMessagesPerSecond` / `…Burst` | `30` / `60` | Frames per game socket. |
| `LobbyCreatesPerMinute` | `10` | Lobby creates per player. |
| `MaxConnectionsPerIp` | `32` | Concurrent `/ws` sockets from one address. |

Outbound webhooks (§6):

| Key | Default | What it does |
| :--- | :--- | :--- |
| `WebhooksEnabled` | `true` | Off ⇒ no dispatcher, no HTTP client, and the webhook routes refuse naming this key. |
| `MaxWebhooks` | `8` | Endpoints that may be registered. |
| `WebhookTimeoutSeconds` | `10` | Per-delivery deadline. A slow endpoint must not hold the queue while alerts drop. |
| `WebhookErrorsPerMinute` | `6` | Error-log events turned into deliveries; `0` sends none (off, **not** unlimited). The next alert reports what was suppressed. |
| `WebhookMemoryThresholdMb` | `0` (off) | Working set that counts as a breach. |
| `WebhookCpuPercentThreshold` | `0` (off) | Process CPU (percent of one core-equivalent) that counts as a breach. |

Package management and the marketplace (§4):

| Key | Default | What it does |
| :--- | :--- | :--- |
| `GamesManagedRoot` | sibling `games-managed` | Where portal-installed `.kbg` packages and their rollback backups live. Must be writable and **outside** the read-only games mount. Not regenerable — an uploaded package exists nowhere else. |
| `ManagedPackages` | `true` | Master switch for portal installs. Off ⇒ the root is never created and every install is refused with a reason; hand-placed packages still work. |
| `PackageBackupCount` | `1` | Previous versions retained per game for rollback. `0` keeps none. |
| `MaxConcurrentInstalls` | `1` | Downloads/extractions in flight at once. Bounds bandwidth and peak disk, not the number of jobs. |
| `PackageJobRetention` | `50` | Finished operations kept for the list. Never evicts a running one. |
| `MarketplaceEnabled` | `true` | Off ⇒ no catalog is fetched and the server holds no HTTP client at all. |
| `MarketplaceUpdateCadence` | `daily` | Scheduled check for enrolled games: `off`, `hourly`, `daily`, `weekly`. The **starting** value only — the Platform tab overrides it and persists. With nothing enrolled it makes no request regardless. |
| `MarketplaceUpdateHourUtc` | `3` | Hour (0-23 UTC) the daily/weekly check runs at. |
| `MarketplaceUpdateDayOfWeek` | `sunday` | Day the weekly check runs on. |
| `MarketplaceMaxSources` | `8` | Extra marketplaces that may be registered, beyond the built-in official one. |
| `MaxPackageBytes` | 512 MiB | Also the upload cap, enforced against bytes actually received. |

---

## 9. Not built yet

The portal covers live operations. Deliberately absent, and specified in
[issue 39](https://github.com/jcub1011/KnockBox-Games/issues/39) for later passes:

- **Signature verification beyond the published hash.** A catalog commits to a `sha256` and that is
  enforced on every download, but nothing is signed. See [MARKETPLACE.md](./MARKETPLACE.md).
- **Scheduled update windows.** The check runs on a fixed interval; there is no "only between 03:00 and
  05:00".

**Decided against, rather than deferred:**

- **Reserved room codes** (spec §2.4 also names "reserve or spawn" specific codes such as `TEST` or `DEMO`).
  With no player accounts there is nobody a reservation could be held *for*: it would have to be handed to
  whoever created the next lobby for that game, or backed by a phantom lobby the reaper is taught to spare.
  Both are more machinery than a code you can simply read out loud is worth. Banned codes — the half that
  protects players — are built (§6).
- **Per-game memory.** Games run in the player's browser; the only server-side per-game cost is a
  server-authority module's execution, and that is measured (§1). A memory figure would have to be inferred
  from engine count times the configured cap, which looks measured and isn't.
