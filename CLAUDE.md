# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

KnockBox is a game-hosting platform for multiplayer web games. Drop an HTML5/WASM game into
`games/` — as a single `.kbg` package or a plain folder — and it becomes playable with no server
code and no restart. The server owns discovery, lobbies, anonymous identity, and message routing;
**games own all logic and state** (host-authoritative). Games talk to the server over WebSocket via
the `web/knockbox.js` SDK. See `docs/INFRASTRUCTURE.md` (architecture),
`docs/GAME_DEVELOPER_GUIDE.md` (authoring), and `docs/KBG_FORMAT.md` (the package format).

## Commands

Solution file is `KnockBox-Games.slnx` (modern `.slnx`, not legacy `.sln`). All projects target `net10.0`.

- Build: `dotnet build KnockBox-Games.slnx`
- Run (dev): `dotnet run --project KnockBox.Server --launch-profile http`
  — shell at http://localhost:5114, games origin at http://localhost:5115
- All .NET tests (xUnit): `dotnet test KnockBox-Games.slnx --nologo`
- Single .NET test: `dotnet test KnockBox.Server.Tests --filter "Name~SomeTestName"`
  (or `--filter "FullyQualifiedName~Namespace.Class.Method"`)
- Web tests (Vitest, from `web/`): `npm ci && npm test` (watch: `npm run test:watch`)
- Phaser client tests (from `clients/phaser/`): `npm ci && npm run lint && npm test`
- CLI tests (from `tools/pack-game/`): `npm ci && npm test`
- Build the addon release archives + index: `node tools/build-addons.mjs` (writes `.addons/`)
- Desktop publish (self-contained win-x64 exe): `dotnet publish KnockBox.Server -p:PublishProfile=win-x64-desktop`

The `web/` frontend is plain ES modules — **no build step**; it is served directly and baked
into publish/Docker output. Unit-tested under `web/__tests__/`: `web/kb-core.js` (pure protocol
logic, Node env) plus `shell.js` and `knockbox.js` (jsdom, against the **real** `index.html` —
`helpers.js` injects it, so element ids stay in sync with production markup). `index.html` loads
`/shell.js?v=N` — **bump `N` whenever you change `shell.js`**, or browsers serve the stale module
against new markup.

## Docker / CI

Docker does not build locally on this machine — verify container changes via GitHub Actions
(`gh run watch`).

**Three workflow files, and the split between them is deliberate.** `gate.yml` holds the six
build-and-test jobs as a **reusable workflow** (`workflow_call` only — it has no trigger of its own and
cannot be run from the Actions tab); `ci.yml` calls it on push/PR and then publishes `:develop` or an
addon release; `release.yml` calls it as the gate for a manual platform release. One definition of
"is this commit good?", so a release can't be verified by a second, drifting copy of the suite.

It is factored **suite-extracted rather than publish-extracted**, and that direction is the whole
point: a called workflow's jobs cannot request more `GITHUB_TOKEN` permission than the calling job
holds, and that is a workflow *validation* error — it fires even for jobs that will be skipped. When
`release.yml` called `ci.yml`, its gate job had to grant `contents`/`packages`/`id-token` write purely
to satisfy `publish` and `addons`, which never run during a release. `gate.yml` contains no publishing
job, so `release.yml`'s gate needs nothing but `contents: read`. Moving `publish`/`addons` out instead
would have worked equally well for permissions and was rejected: **the npm trusted publisher on
npmjs.com names the publishing workflow by filename**, and both OIDC claims it can be matched against
(`workflow_ref`, `job_workflow_ref`) resolve to the file the job is *defined* in — so relocating
`addons` breaks `npm publish` with an auth error until that config is edited by hand.
`ReleaseWorkflowTests` pins all of this: `release.yml` must call `gate.yml` and not `ci.yml`, and
`gate.yml` must grant no write permission anywhere.

The six jobs in `gate.yml`:
- `dotnet` — .NET build & tests.
- `aot` — Native AOT publish with `/warnaserror`; any new trim/AOT `ILxxxx` warning fails the
  build (mirrors the Dockerfile build stage, needs clang + zlib). Keeps the server AOT-clean.
- `web` — shell + SDK Vitest tests.
- `clients-phaser` — Phaser client lint + tests.
- `pack-game` — CLI tests, **plus a `node tools/build-addons.mjs` run**: the addon manifest's file
  lists are the release job's only input, so a stale entry fails a PR rather than a tag build.
- `docker` — image build + smoke test (boots the container, checks shell/SDK serving, hot-reload
  discovery, and that the admin portal binds its own port, claims a password once and stays 404 on the
  public origins — the only place a real listener is exercised). Build context is the repo root;
  `web/` must be present.
The two publishing jobs live in `ci.yml`, each `needs: [gate]`:
- `publish` — **`:develop` only**, from `main`. Versioned and `:latest` tags are `release.yml`'s and
  are deliberately unreachable from here: this job publishes whatever `main` happens to be, which is
  exactly what a release must not be. `release.yml` cannot reach it at all — it calls `gate.yml`, not
  this file.
- `addons` — **`addons-v*` tags only**: verifies the tag matches `addons.manifest.json`'s `sdkVersion`, builds the
  addon archives + `ADDONS.json`, uploads the archives to the release, commits the index to `main`, and
  publishes `knockbox-cli` to npm via **trusted publishing (OIDC)** — which keeps the repo's
  "no long-lived secrets beyond `GITHUB_TOKEN`" property. Also needs `id-token: write`.

`gate.yml`'s one input is `export_image`, which `release.yml` passes as `true`: the `docker` job then
`docker save`s the image it just smoke-tested and uploads it as an artifact, so a release pushes
**those bytes** rather than a cache-hit rebuild of them — same layers, but a rebuild is still a
separate build, and what users pull should be what was tested. Off by default so PRs don't pay the
artifact round trip. The `docker` job also runs `tools/compose-release.mjs --check` on every PR: the
release bundle's compose file is *generated* from the repo's, so a compose edit that moves an anchor
must fail on the PR rather than during a release.

### Releasing the platform (`.github/workflows/release.yml`)

**Two release tag namespaces, and they must stay separate.** `v1.2.3` releases the *platform* (the
csproj `<Version>`); `addons-v1.2.3` releases the *client addons* (the manifest's `sdkVersion`). The
two version numbers are independent by design, so one tag namespace made every tag claim both at
once: a server-only release failed the `addons` job's tag-vs-manifest guard for no real reason, and
an addon-only release published an image tagged with a version its own assembly didn't report — and
`KnockBoxVersion` (from the assembly, not the tag) is what marketplace `minAppVersion` bounds are
judged against.

Only **`addons-v*` is a push trigger**. `v*` used to be, and a hand-pushed tag was then a second path
to a platform release — one that bypassed every guard `release.yml` adds. Two paths to one artifact
drift, and the unguarded one wins by being easier to reach; worse, re-adding it is *silent*, because
a tag pushed by CI with `GITHUB_TOKEN` does not trigger further workflow runs, so the duplicate only
fires when you tag by hand. `ReleaseWorkflowTests` asserts it stays absent.

**`release.yml` is `workflow_dispatch` only, and takes no version input.** The version is read from
`KnockBox.Server.csproj` `<Version>` with `dotnet msbuild -getProperty:Version` (a real MSBuild
evaluation, not a grep) — the same number `KnockBoxVersion` reports off the assembly, so tag and
binary cannot disagree. That also makes "I forgot to bump the version" a hard stop rather than a bad
release: the tag for an un-bumped csproj already exists, and preflight refuses a reused tag. Two
inputs only: `OverwriteExisting` (replace an existing release + tag) and `DryRun` (run every gate,
mutate nothing — the only way to exercise the workflow, notably the `windows-latest` AOT publish,
without publishing).

**"If any build fails, upload nothing" is the `needs:` list, not an `if:` chain.** Every build, test
and asset is a gate job; `release` is the only job that writes anything and `needs:` all of them, and
a dependency that fails *or is skipped* skips the dependent job. `ReleaseWorkflowTests` pins that
list — dropping one entry silently converts a gate into an advisory.

**Ordering inside `release` is load-bearing, because cross-service atomicity doesn't exist.** GHCR
and the Releases API share no transaction, so: push the image **first** (a stray image tag nobody has
been pointed at is harmless and idempotently re-pushable; a published release whose `docker pull`
line 404s is not), then create the release as a **draft**, upload assets, and only then flip it to
published — so a partial upload never becomes visible, watchers get exactly one notification, and the
git tag (which a draft does not create) appears at that same instant. On failure the draft is deleted
and the pushed image tags are **named in the job summary** rather than auto-deleted: removing a GHCR
version needs `packages: delete` plus a version id, and an orphan you know about is a re-run away
from correct.

**`OverwriteExisting` deletes in the `release` job, never in preflight.** Preflight only *verifies*
that overwriting is permitted — deleting up front and then failing the suite would leave you with
less than you started with, which is the opposite of the point. Preflight checks **three** claims on
the version, each invisible to the others: the git tag, a published release, and a **draft** release
(which creates no git ref, so neither of the first two sees it). That third check is why `preflight`
carries a job-level `contents: write` **despite writing nothing**: the Releases API lists drafts only
to a token with push access, so under the workflow's `contents: read` the check silently returned
nothing and reported the version unclaimed — after which `gh release create` made a *second* draft on
the same tag, which `gh release upload`/`edit` then resolve by tag **name**.

**The failure cleanup only removes what the run itself created.** `failure()` also fires for the steps
*before* the first mutation (collect the assets, load the image, plan the tags), and an unguarded
`gh release delete "$TAG"` there deletes whatever already held the tag — under `OverwriteExisting`,
the release the operator still has, destroyed while shipping nothing. So the delete is gated on the
`Create the draft release` step's own output, and the "these image tags may be orphaned" summary on
the push step's (set *before* its loop, since a push that dies on the second of three tags has already
made the first live). Having deleted the draft it also drops the git **tag** if one exists — the case
where `gh release edit --draft=false` succeeds server-side but reports failure, which otherwise leaves
the next attempt failing preflight's `git-tag` claim and demanding `OverwriteExisting` to recover from
a run that shipped nothing.

**`:latest` is computed, not assumed — and `:MAJOR.MINOR` is computed separately.**
`docker/metadata-action`'s `latest=auto` only ever saw the current ref, so re-running an older `v*` tag
silently moved `:latest` backwards. Preflight instead compares against every existing `v*` tag with
`sort -V`. `sort -V` is correct there *because* prereleases are excluded up front — it does not
implement semver prerelease precedence, and comparing plain `X.Y.Z` is all it is asked to do. The two
tags answer **different questions** and briefly shared one variable, which was a bug: `:latest` asks
"highest anywhere?", `:MAJOR.MINOR` asks "highest *in its own line*?" — so a back-ported `1.2.4` cut
after `1.3.0` correctly leaves `:latest` alone but must still move `:1.2`, which is the only case that
tag exists for. The line is selected with an `awk` **prefix** match (`index($0, "1.2.") == 1`) rather
than a regex, so no dot escaping is needed and `1.2.` cannot match `1.20.0`. A prerelease declines
both.

**Released images carry OCI labels applied at the *gate*, not by the pusher.** `release` pushes the
exported tar of the image `gate.yml` smoke-tested, and re-labelling afterwards rewrites the image
config and changes the digest — so the bytes users pull would stop being the bytes that were tested.
`gate.yml`'s `Build image` step therefore sets `org.opencontainers.image.source`/`.revision`/`.version`
itself, taking the last from an optional `image_version` input (`release.yml` passes preflight's
version; a called workflow can only see `github.ref_name`, which on a release run is `main`). This was
lost for stable releases when the suite moved out of `ci.yml` — the old `v*` publish path passed
`docker/metadata-action`'s labels and `:develop` still does, so only releases regressed, which is the
harder case to notice. Without `.source` the GHCR package stops being linked to the repo.

Release assets (all built in the gate, so a broken one cancels the release): a `windows-latest` AOT
desktop zip, a `linux-x64` tarball (tar, not zip — zip drops the executable bit), and a Docker bundle
whose `docker-compose.yml` is generated by `tools/compose-release.mjs` with the repo's `build:` stanza
rewritten to a pinned `image:`. One compose file in the repo rather than two that drift — the same
reasoning as the generated `ADDONS.json`. The transform makes **three** anchored, *counted* edits
(promote the commented `# image: …:latest` line, comment out the `build:`/`context:`/`dockerfile:`
trio, and mount the bundle's `appsettings.json` after the `/games` mount); each throws unless its
count is exact, which is what `--check` on every PR is actually checking. The appsettings mount is
bundle-only and load-bearing: the image bakes its own copy and nothing reads the one beside the
compose file, so without it a user edits a `KnockBox:` knob, restarts, and sees no change and no
reason for it. The image ref is passed with `--image` from the workflow rather than left to the
script's hardcoded default, which is a different expression from the one `release` pushes to and
agreed with it only for this owner.

Deployment: the `games/` directory is mounted **read-only** from a stable host path
**outside** the image, so it survives image updates (see `docs/HOSTING.md`). That read-only-ness is
why both derived caches (`GamesCompressedRoot`, `GamesUnpackedRoot`) live outside it on their own
writable mounts. On bind mounts, file-watch events don't propagate, so the image sets
`KnockBox__GamesPollSeconds=10` as a polling fallback for hot-reload — which is also the only signal
that notices a dropped `.kbg` there.

### Surviving an image update (`docs/HOSTING.md` → "Updating KnockBox")

**Updating an image destroys the container, so state is kept exactly by what is mounted — and this is
the one requirement most installations depend on.** The predecessor project lost operator settings and
games on every TrueNAS Custom App update, and the post-mortem is worth keeping because two of its three
causes are shaped like ordinary improvements. It believed `VOLUME ["/app/data"]` guaranteed persistence:
it does not — an unmounted `VOLUME` becomes an *anonymous* volume, which Compose usually carries across
a recreate but `docker run` replaces per container and Kubernetes ignores outright, so it works just
often enough to be trusted. **This image therefore declares no `VOLUME`, deliberately**, and
`DockerPersistenceTests` fails if one appears. Then a memory optimization switched to a chiseled base
and, because a chiseled image has no shell, deleted the `mkdir -p … && chown $APP_UID` line — leaving
root-owned mounts an unprivileged process could not write, while the portal still reported saves as
succeeding. (Ours *does* surface that: `AdminSettingsStore.Save` returns "active now but could not be
saved", and `admin.js` toasts it.) Third, and unrelated to Docker: a commit renamed every browser
`localStorage` key with no migration, orphaning per-game saved settings. The `kb.*` keys are a small
flat set with no migration helper — **renaming one orphans it**, and so does changing
`GamesOrigin`/`GamesHost`, which moves every game to a new origin.

`Hosting/StatePersistence.cs` is the answer to all of that: pure mount-point parsing over
`/proc/self/mountinfo` (string ops only, so the `aot` gate stays green), reporting through
`DeploymentDiagnostics` from the bootstrap block in `Program.cs` — **before** the issue-logging loop, or
the warning never reaches the startup log, which is the only place a container operator looks. Gated on
`InContainer()`, since on a normal host "nearest mount is `/`" is the ordinary case; the Dockerfile sets
`DOTNET_RUNNING_IN_CONTAINER=true` **explicitly** rather than inheriting it, because `/.dockerenv` does
not exist under Kubernetes or containerd and a base-image bump must not be able to switch the safeguard
off in silence — which is exactly how the chown was lost. Only the **non-regenerable** roots warn
(admin state, `GamesManagedRoot`, `GamesRoot`); the two caches and the logs do not, because a warning
that fires on everything trains an operator to skim past the one that matters.

Two more pieces, both about copies rather than the canonical file. `docker-compose.yml` pins a top-level
`name: knockbox` **and** each volume's own `name:` — without them Compose prefixes volumes with the
*directory name*, so upgrading by unzipping a release bundle into a new folder silently starts against
four new empty volumes. And the repo compose file was already correct while every published copy of it
(`README.md`'s quick start, the Cloudflare Tunnel block, the TrueNAS paragraph) omitted `/app/data` and
`/app/games-managed` — operators paste the copies, so `DockerPersistenceTests` asserts that **every
fenced block in the docs that mounts `/games` also mounts both**. The gate's `docker` job proves the
real thing: it mounts actual volumes (previously only `/games`, which made every `docker exec … test -f
/app/data/…` assertion a statement about the container's own writable layer), then **`docker rm -f`s the
container and starts a new one on the same volumes** — a `restart` would pass regardless of mounts and
is the bug, not the test. A second, volume-less container asserts the warning fires.

## Architecture

### Projects
- `KnockBox.Contracts` — shared wire DTOs: `Messages.cs` (polymorphic, `type`-discriminated,
  camelCase on wire), `GameManifest.cs` (the GAME.json shape), `Player.cs`, `Protocol.cs`
  (wire version, currently 1).
- `KnockBox.Server` — ASP.NET Core host. **No database, no EF** — all state is in-memory
  singletons (`Program.cs` wires `GameCatalog`, `TokenService`, `LobbyManager`,
  `ConnectionManager`, `WebSocketHandler`, `ServerLimits`, `TimeProvider`).
- `KnockBox.Server.Tests` / `KnockBox.Contracts.Tests` — xUnit.

Outside the .NET solution, three Node subprojects (each its own npm package, Vitest-tested):
- `web/` (`knockbox-web`, private) — the shell + the reference browser SDK. Not published; baked into
  the server's publish output by a csproj `Content Include`.
- `clients/phaser/` (`knockbox-phaser`, private) — networking client for Phaser. Ships `kb-core.js`
  (pure protocol logic, same concept as `web/kb-protocol.js`), `knockbox-local.js` (server-less
  local peer), and `kb-authority.js` (host-authoritative helper).
- `tools/pack-game/` (`knockbox-cli`, **the one published package**) — the game developer CLI. Two
  subcommands: `knockbox pack` (engine-agnostic packager, the former `knockbox-pack`, which is kept as
  an alias bin; `--dir` still emits the plain folder layout) and `knockbox addon` (see below).
  `kbg.mjs` is the dependency-free stored-ZIP writer + Brotli pipeline; its compress-or-store decision
  deliberately mirrors `GameAssetPrecompressor.ShouldCompress`, so keep the two in sync.

Plus `clients/godot/addons/knockbox/` — a Godot 4 GDScript addon, no npm identity (a `plugin.cfg`
instead). Its `clients/godot/` wrapper is a dev harness, not a sample game, and Godot is **not** in CI.

### Addon distribution (`docs/ADDONS.md`)
The client addons are **vendored into game repos** — unavoidably so for Godot, since GDScript compiles
into the export — and used to be acquired by hand-copying, with nothing recording the version copied.
They are now published like the games the marketplace serves, that mechanism pointed the other way.

**`clients/addons.manifest.json` `sdkVersion` is the ONLY real version number in the repo.** Releasing
edits that one line. Every other declaration holds the sentinel `0.0.0-dev` and is filled in by the
build: `tools/build-addons.mjs` stamps the Godot `plugin.cfg` inside the archive (driven by the
manifest's own `versionFiles`), CI stamps `tools/pack-game/package.json` before `npm publish`, and
`Hosting/KnockBoxSdk.cs` **reads the manifest embedded into the assembly** by an `<EmbeddedResource>`
(embedded, not copied to the publish output: no path to resolve and no file to lose; an unreadable one
yields `Current == null`, which reports every game as `unknown` rather than mislabelling them all as
`ahead` off a `0.0.0` fallback). `clients/phaser/` and `web/` `package.json` versions are not tracked at
all — both are private and unpublished, so the number was pure ceremony.

`AddonManifestTests` (built on `OriginPortBindingTests`' repo-file-consistency pattern, via the shared
`RepoFile` helper) therefore asserts each in-repo declaration is **still the sentinel** rather than
that it equals `sdkVersion`: an equality check still permits six real numbers that must be edited
together, which is exactly the arrangement that had already drifted to three different values. The
packer is the one thing that resolves the real version from a checkout (`resolveVersion()` falls back
to the manifest when its own `package.json` reads the sentinel), so a locally built `.kbg` carries the
same `packedBy` a released one would. The Godot updater **refuses to run** on the sentinel — that means
it is looking at this source tree, where updating would overwrite the repo's own files.

Compatibility with a server is `minAppVersion`/`maxAppVersion` vs `KnockBoxVersion` — *not* matching
numbers — so an addon release doesn't force a server release. **But editing those bounds does require
an addon release of its own**, and that is not obvious: the bound reaches users only through
`.addons/ADDONS.json`, which an `addons-v*` tag regenerates and nothing else does. Lowering the
platform `<Version>` to `0.1.0` and following it in the manifest left all three *published* records
still claiming `minAppVersion: 1.0.0` — i.e. the index advertised every addon as incompatible with the
first platform release. `AddonManifestTests` now fails a checkout where the index and the manifest
claim the **same** `sdkVersion` but disagree about `minAppVersion`; a differing `sdkVersion` means the
bump is staged and the regenerating release simply hasn't run yet, so that state is allowed rather
than trapping the PR that stages it.

**`tools/build-addons.mjs`** (repo tooling, excluded from the npm package) builds one stored-ZIP per
addon plus `.addons/ADDONS.json`. The index is **generated, never hand-edited**: its `sha256` values are
the trust root, and a stale hash fails every install with a tampering error that isn't tampering. The
index is committed (served from raw.githubusercontent); the `.zip`s are release assets and gitignored.
Same trust model as `docs/MARKETPLACE.md` §3 — required `sha256`, **derived** URLs
(`{base}/{repo}/releases/download/{tag}/{asset}`), `repo`/`tag`/`asset` pattern-checked before any
request leaves the process. Version **pinning is served from the index's `versions` history, never a
guessed URL**: a version the index doesn't publish has no verified hash.

**Archive layout is PROJECT-relative** (`addons/knockbox/…` + `knockbox.json` at the root), which is
what makes "unzip at your project root" a first-class install rather than a fallback — the same
convention a Godot AssetLib zip uses. The archive ships the *same* `knockbox.json` the CLI writes,
**byte-identical**; a test asserts it because nothing at runtime does. That is also why the record
carries no `archiveSha256`: it can't be known when the release job writes the copy that lives inside
the archive it would have to hash.

**`add` and `update` take opposite defaults, and that asymmetry is the design.** `add` at the same
version is a developer saying "make this pristine", so it overwrites a locally-modified file and names
it — which makes it the *repair* path, with no separate `reset` verb. `update` changes to a different
version, where silently discarding an edit is a surprise nobody asked for, so it refuses and points at
`--force`. Both have their own tests or a later "consistency" refactor will collapse them. Pruning is
scoped to the *recorded* file list, never a directory wipe: a developer's own script in
`addons/knockbox/` was never ours to delete.

**Godot updates itself** (`clients/godot/addons/knockbox/updater.gd`, two `add_tool_menu_item` actions
mirroring `update`/`add`). Core-only — `HTTPRequest`, `HashingContext`, `ZIPReader`, `ConfigFile` — so a
Godot developer needs no Node and no terminal, which matters because they're the audience worst served
by copy-paste and least likely to have a JS toolchain. Inert until clicked (no timer, no autoload: a
game project must not phone home while someone types), and `plugin.gd` guards for its absence so the
file can simply be deleted. Note `--check-only --script` can't resolve `class_name` references without
the editor's global class cache, which is why `test_authority.gd` fails that check standalone.

**Games record what they were built with.** `GAME.json` gains optional `sdk` (`{ "godot": "1.0.0" }`),
stamped by `knockbox pack` from the project's `knockbox.json` — read, never asked for on the command
line, so it reports what was installed rather than what the author remembered. The author's file on
disk is never modified; the stamp is generated into the package (which is why `plan()`'s `contents` map
now accepts a `Buffer` as well as a source path). Never validated, like `Version`.
`KnockBoxSdk.StatusOf` → `unknown`/`current`/`behind`/`ahead`; the portal badges only the two
actionable ones, because a badge on nearly every card is not read. **`unknown` must stay distinct from
`behind`** — most games carry no stamp, and flagging them all trains an operator to ignore the column.

### One `/ws` endpoint, two roles (the core idea)
`/ws` is served on **both** the shell origin and the game origin. The **first frame** selects the role:
- **Control role** (`HelloMessage`, the shell's socket): identity handshake, lobby ops
  (list/create/join/leave), and `RequestTicket`. Handled by `RunControlAsync` in
  `KnockBox.Server/Networking/WebSocketHandler.cs`.
- **Data role** (`AttachMessage`, the game iframe's own socket): authenticates with a
  lobby-scoped ticket, then relays `Game{to, payload}` messages where `to` ∈
  `{"host","all","<playerId>"}`; the server stamps `from` and fans out. Handled by `RunDataAsync`.

### Three origins
Shell origin (5114 dev) serves the shell UI + SDK; game origin (5115 dev, a subdomain in
prod) serves `/games/{id}/…` builds. Games run in **cross-origin iframes** so untrusted game
code cannot read the shell's identity token. `Hosting/OriginRouting.cs` resolves which origin
a request is on; `Hosting/ContentPaths.cs` resolves the web/games/logs locations.

Third: the **admin origin** (`AdminPort`, 5116 dev / 8082 image; `AdminHost`/`AdminOrigin` as a subdomain
in prod), an operator dashboard served from `web/admin/` at that origin's **root**, claimed in a `MapWhen`
branch ahead of the game and shell pipelines. Every `/admin*` path 404s on the two public origins. The API
under `/admin/api/*` lives in `Hosting/AdminApi.cs` (`MapAdminApi`), not in `Program.cs`: one `WriteJson`
helper, one `RequireSession` wrapper and a route table, so an endpoint's handler is only the part specific
to it. Mutating routes additionally go through `WriteGuard` (JSON content type + `Sec-Fetch-Site`), which is
defence in depth behind `SameSite=Strict`, not the primary control — the port is. **Except on the three
auth routes, where it is the only control there is**, and where it was missing: they were mapped bare and
`ReadPassword` checked no content type, so an HTML form with `enctype="text/plain"` posts a body that
`AdminPasswordRequest` parses — a simple request, no preflight, and `auth/setup` needs no cookie because
it is claim-on-first-use. A page the operator merely visited could therefore claim an unclaimed portal
**from inside the loopback binding**, permanently (there is no overwrite path). They now take
`MediaKind.JsonRequired`, which demands the type outright rather than only when `ContentLength` says a
body exists — because `Transfer-Encoding: chunked` declares no length, and reading that absence as "no
body" let a cross-site post through on a technicality *and* made `ReadJson` discard it, so every handler
substituted its all-defaulted record (for `lobbies/close`: close every lobby on the server, answered
`success: true`). The decision is a pure `WriteGuardRefusal`, kept free of `HttpContext` for the reason
`OriginRouting` is — composing the route table needs thirty-odd dependencies, so otherwise the rule would
be pinned only by the Docker job. That leaves whether a route still ASKS uncovered, and a rewrite of the
route table duly dropped the wrapper from ten of them while their neighbours kept it, so nothing failed:
`AdminRouteGuardTests` now reads `AdminApi.cs` and asserts every `MapPost` registration contains
`WriteGuard(`, matched to the registration's own parens — a fixed line window runs into the next
registration and reads a neighbour's guard as this route's, which is how the first version of that test
passed against the very file it was written to fail on. Operator guide:
`docs/ADMIN.md`.

**Portal tabs** (six; the frontend is `web/admin/{index.html,admin.js,admin-core.js,admin.css}`, where
`admin-core.js` is the pure/tested half exactly as `kb-core.js` is to `shell.js`, and `admin.js` exports
`bootstrap()` so it can be driven under jsdom). Only the **visible tab polls** — six panels each polling
would multiply the request rate for five nobody is looking at, and the games tab can trigger a disk walk.
- **Overview** — platform counters, the history graphs, `DeploymentDiagnostics` issues (repeated here because
  the warning page only replaces the *shell's* home page), maintenance toggle, per-game relay + authority cost.
- **Active Lobbies** — the directory, with single/bulk close, stale purge and per-member kick.
- **Game Catalog** — availability, disk footprint, delete, rescan, lifecycle badges.
- **Marketplace & Packages** — catalogs, install/update/rollback/uninstall, upload, the operations list.
- **System Logs** — the live stream plus raw file download.
- **Platform** — runtime limits & lobby caps, the marketplace update schedule, the player announcement,
  banned room codes, webhook endpoints.

There is **no "admin port active" indicator**, and the absence is deliberate: `#server-status-pill` ships
`hidden` and is painted only by `showErrorStatus`. A pill reporting that the admin port is up, on a page
that port just served, can never be read while it is false. (Its CSS needs `.status-pill[hidden]` for the
same reason `.home-announcement[hidden]` does — see the launch-overlay note about `display: flex` beating
the UA `[hidden]` rule.)

**`POLL_MS.platform = 0`: the Platform tab is the only one that does not poll**, and `startPolling` treats a
falsy interval as "no timer" rather than falling back to 5 s. It is a set of forms, not a view — a poll would
re-render a field mid-edit and throw away what the operator was typing. It reads on entry, after each save,
and on Refresh. A jsdom test asserts it arms no timer, beside the one asserting the others do.

**The marketplace tab's poll rate is split, and that split is the design.** `POLL_MS.marketplace = 3000`
hits only what this server already holds — the in-memory job feed and the local game list; the catalog,
which reaches the network with a 30-second timeout, is read on tab entry, on Refresh, and when a job
reaches a terminal status (which is what flips a card from "Update to 1.3.0" to "Up to date" the moment
it's true). `web/__tests__/admin-marketplace.test.js` asserts exactly this.

**`refreshActiveTab` is keyed on the visible PANEL, not on `activeTab`.** Each top tab renders several of
the old tabs at once, so refreshing only the one `activeTab` names leaves the rest of the same screen
frozen: Monitoring showed live counters above a lobby table fetched once on arrival, and scrolling down to
Active Lobbies froze the counters instead. `enterTab` therefore does the cursor resets and the one read
that is NOT on the poll path (`refreshCatalog`), and leaves the rest to `refreshActiveTab` — doing both
fetched everything twice on entry.

**`shell.test.js` runs entirely on a fake clock, and that is load-bearing.** Same root as the traps below
— one `window` per file — but through timers rather than listeners: a **real** timer armed by one test's
module copy is still pending when the next test starts, and its callback resolves `document.body` and
`getElementById` against the DOM that exists **when it fires**, which is the next test's. The launch fade
(220 ms) and the morph safety net (420 ms) both end by adding `body.in-game`, so a launch examined in one
test dropped that class onto a test that had launched nothing, and the launch-overlay assertions read it as
state they had caused. The launch block alone used to install its own fake clock, which stopped it *arming*
leaky timers but not the rest of the file arming them **at** it — so it still failed about one run in three
while passing every time the file ran alone. The file-level `beforeEach` now owns the clock and `afterEach`
throws it away, which is what makes a timer unable to outlive the test that armed it. One production
consequence worth knowing: a fake clock freezes `performance.now()` at 0, which caught `lastClickAt = 0` as
the room-code button's "no previous click" sentinel — a value the clock really can report, so the first
click read as a double-click. It is `-Infinity` now, which is also correct for the real clock's first
quarter-second.

**Two jsdom traps that file documents**, both from vitest reusing one `window` per file: every
previously-imported copy of `admin.js` still holds its `hashchange` listener, so assigning
`location.hash` in `beforeEach` makes stale modules re-render and re-fetch into the fresh DOM (use
`history.replaceState`); and their poll intervals keep firing against the next test's fetch stub unless
`stopPolling()` — exported for exactly this — is called in `afterEach`.

**Upload is `XMLHttpRequest`, not `fetch`**, and it is the only such path in the codebase. `fetch` has no
upload-progress event, and a half-gigabyte upload with no progress reads as hung — so the operator clicks
again and starts a second one. Two consequences: the 401 funnel `request()` owns had to be extracted into
`handleUnauthorized()` so the XHR path shares it, and `WriteGuard` grew a media-kind parameter scoped to
that single route (its content-type check is skipped when `ContentLength` is null, so the upload route
requires `application/octet-stream` *positively* rather than on that technicality).

**Runtime-editable limits are a live read, not a re-read.** `Networking/LimitsProvider.cs` holds the configured
baseline plus the operator's overrides (`OperatorLimits`, persisted, every member nullable so the file records
only what changed) and publishes the merged `ServerLimits` as one volatile reference. The seam that makes it
matter is that `TokenBucket` and `IpConnectionGate` take a **delegate** rather than captured numbers, so an
edit reaches sockets that are **already open** — the connections a flood arrives on are by definition already
connected, which is what made a capture-at-construction design nearly useless as a control. Three knobs stay
startup-only and say so in the portal: `HandshakeTimeout` and `DisconnectGrace` (the reaper's interval, and
whether its timer exists at all, are derived from grace at startup) and both `AdminLoginAttemptsPerMinute`
caps (they bound PBKDF2 CPU for an unauthenticated caller — a lock that opens from inside the room it protects
is not a lock). The **lobby caps** (`MaxLobbies`, `MaxLobbiesPerGame`) are enforced in `HandleCreateLobby`,
between the policy gate and `LeaveLobbiesExcept` — *not* behind `IPlatformPolicy`, because neither policy
implementation knows anything about live lobbies while that method already holds the manager that does, and
that ordering is what stops a refused player also losing the lobby they were in.

**Banned room codes are globs, deliberately not regexes.** `Lobby/RoomCodeFilter.cs` compiles two operator
lists — substring `words` and whole-code `patterns` (`?`/`*`) — and `LobbyManager` reads it per draw, so an
edit applies to the next lobby. A blocked draw is **re-drawn without consuming one of the five placement
attempts**: those exist for code collisions, and spending them on a blocklist would quietly raise the failure
rate of starting a game. An operator-typed regex on the lobby-create path would be a DoS lever pointed at the
thing every player needs, and buys nothing over a glob on four characters. The API refuses a list removing
more than half the code space, counted **exactly** by walking all 32^4 codes on save — a starved generator
surfaces to players as "could not create a lobby" with nothing to connect it to the cause. Spec §2.4's
*reserved* codes were **dropped by decision**, not deferred (docs/ADMIN.md §9 records why).

**Announcements are the second thing shaped like `MaintenanceMessage`.** `AnnouncementPostedMessage` /
`AnnouncementClearedMessage` are additive server→client pushes (no protocol bump: the version gate only
rejects clients *newer* than the server, and an old shell drops an unknown `type`). `IPlatformPolicy` gained
`Announcement` so the relay passes a payload through without interpreting it and still knows nothing about
settings files; `ConnectionManager.BroadcastToAllControl` is the only platform-wide fan-out in the server and
iterates the registry directly rather than snapshotting every player. The banner is **also pushed right after
`WelcomeMessage`**, which is what makes a late arrival see the same notice without the server keeping
per-viewer state — and reuses the message type instead of adding a `WelcomeMessage` field the Phaser and Godot
SDKs would have to mirror. Dismissal lives in `localStorage` keyed by the announcement **id**, so an edited
notice (new id) returns for everyone who dismissed the old one.

**The webhook loop guard is the load-bearing part of §4.2.** `Webhooks/WebhookLogSink.cs` turns error-level
log events into deliveries; a failed delivery logs. Without excluding `WebhookDispatcher`'s own
`SourceContext`, those two facts are a loop that grows one event per failure until the endpoint recovers —
which it cannot, because the server is now busy posting to it. (The dispatcher also logs failures at Warning,
so the exclusion is the second line, not the only one.) A `TokenBucket` caps alerts per minute and the count
suppressed rides the next delivery. `WebhookQueue` is created **pre-`Build()`** like `AdminLogBuffer`, because
the sink is constructed inside `UseSerilog` where DI does not exist yet while the dispatcher needs the settings
store; it is bounded drop-oldest with a counter, the same policy a game socket's outbound queue uses. Delivery
is **one attempt, no retry**, with the last result kept per endpoint — and the payload carries the same summary
as both `content` (Discord) and `text` (Slack) plus structured fields, so one POST serves all three kinds of
endpoint with no per-service formatting in the server. `PackageJobRegistry` grew a settable `OnFinished` hook
(the `LobbyCloser.OnClosing` shape) so update outcomes reach it without the install engine knowing webhooks
exist. The URL rule is `MarketplaceClient.IsAllowedUrl` — exposed, not copied — plus, since `TestWebhook`
awaits the delivery and returns the upstream status, a **destination** rule the marketplace does not
share: `Webhooks/PrivateAddressGuard.cs` refuses loopback/link-local/private targets (default on,
`KnockBox:WebhookAllowPrivateTargets` lifts it) so an admin session cannot use the test button as a port
scanner for the network this host sits in. It is enforced in a `SocketsHttpHandler.ConnectCallback` on
the address actually dialled, never on the URL string: rebinding DNS and redirects both come back
through the callback and would walk past a string check. Only the webhook client gets it — they already
hold separate `HttpClient` instances, and the marketplace's own rule deliberately permits a loopback
`http` mirror.

**Per-game CPU exists only for server-authority games, and is measured.** `Games/AuthorityMetrics.cs` counts
calls, total time and the slowest call per game, instrumented at the **one** place in `ServerAuthority`'s drain
loop where the module owns the thread — one measurement point rather than five around the individual
`_runtime.Invoke` sites. A failed call still counts its time (it ran to the point of throwing). Every other
game reports `--`, not `0.00s`: it executes nothing in this process, which is a different statement. Per-game
*memory* is deliberately not reported, since it could only be inferred from engine count × cap.

**`Admin/MetricHistory.cs` is the fourth cursor-polled feed** (after `AdminLogBuffer` and
`PackageJobRegistry`): a bounded ring sampled by a timer in `Program.cs`, read with `?after=<seq>`. Sampled
**server-side** on purpose — the portal already differences consecutive polls but holds one prior sample, so a
tab switch, a reload or a second machine starts the picture from nothing, and an operator opens this page
precisely when something has already gone wrong. `admin-core.js` derives the series (`seriesRate` omits a pair
whose counter went backwards rather than drawing a false trough) and builds an inline-SVG sparkline path;
there is no charting library, because the portal has no build step. Resource-threshold webhooks are
edge-triggered from the same sampler, so a sustained breach alerts once rather than every 15 seconds.

**Server counters are cumulative; rates are the client's job.** `RelayMetrics` and `Connection`'s
frame/byte/drop counters only ever increase, and `system/status` reports `cpuSecondsTotal` rather than a
percentage: a rate needs two samples, and producing one server-side would mean sleeping inside a request or
keeping per-viewer state. `admin-core.js` `ratePerSecond` differences them, and returns **null** rather
than a negative when a counter goes backwards — that means the server restarted, and drawing it as a spike
would mislead at exactly the wrong moment.

**Per-game relay cost is measured, because games are not server-side-free.** Every socket holds a bounded
outbound `Channel` plus a writer task, and a `to:"all"` fan-out serializes once then sends per recipient.
`Networking/RelayMetrics.cs` counts frames/bytes in and out per game (fan-out counted per recipient, from
the actual send loop, so a member with no attached game socket costs nothing), and `Connection` counts
frames the `DropOldest` policy discarded — which used to be visible only in a log line nobody watches.

**Operator policy is the one persisted thing.** `Admin/AdminSettingsStore.cs` writes game availability
(`Available`/`Disabled`/`Staged`), maintenance mode, runtime limit overrides, the room-code blocklist, the
live announcement and the webhook endpoints to `AdminSettingsPath` (default: beside
`AdminPasswordPath`, i.e. the persisted `/app/data` volume). Everything else here is deliberately ephemeral,
but an admin who disables a game means it to stay disabled across the next image update. Reads are lock-free
(a `volatile` immutable snapshot swapped atomically, the same discipline as `GameCatalog`) because the
lobby-create path calls them; a change **takes effect in memory even when the write fails**, and the setter
returns that as a warning rather than rejecting the change. The relay sees it through the narrow
`Admin/IPlatformPolicy.cs` — `WebSocketHandler` has no business knowing about settings files, and
`PlatformPolicy.OpenPlatform` keeps the flow tests free of one.

**What policy does and doesn't touch:** it gates **creation and listing only**. Existing lobbies play on
through both a disable and maintenance mode (spec §3.1), and join is deliberately ungated. `Staged` =
hidden from the catalog but still startable, which is why `ListGamesMessage` grew an optional `Include`:
the shell allowlists every launch against the catalog it was given, so a staged game reached via its
`/?game=<id>` link has to come back in that list or the shell rejects its own `EnterGame` as unknown. A
**disabled** game is never re-admitted by `Include` — that would only move the refusal to the create round
trip, after the launch overlay was already up. Staged is **visibility, not access control**: there are no
player accounts, so nothing can be authorized and the link is a weak secret at best.

**Closing a live lobby has exactly one implementation.** `Lobby/LobbyCloser.cs` (detach the authority actor,
remove the lobby, one `LobbyClosedMessage` fanned out, abort the game sockets) was extracted from
`ServerAuthorityManager.HandleFatal`, which now calls it — two copies would drift, and whichever gained the
next step would leave the other half-closing lobbies. Distinct from `CloseLobbyIfDark`, which broadcasts
nothing because nobody is left to tell. The authority hook is a settable `OnClosing` rather than a ctor
dependency (the manager needs the closer, so it can't also be an argument to it), but `HandleFatal` still
stops its **own** actor first: a class must not depend on external wiring to reach its own invariant.

**The live log view reads a ring buffer, not the log file.** `Admin/AdminLogBuffer.cs` is a hand-written
bounded `ILogEventSink` (no `Serilog.Sinks.*` package — same AOT reasoning that rejected
`ReadFrom.Configuration`), wired via `WriteTo.Sink` and constructed before `UseSerilog` since the host isn't
built yet. Level and `SourceContext` stay structured, so filtering is exact instead of a guess at parsing
rendered text; a monotonic sequence per event makes ordinary polling a stream (`?after=<seq>`), with no SSE
and no second socket role. It renders with `MessageTemplateTextFormatter("{Message:lj}")` **on purpose** —
Serilog's own `RenderMessage()` quotes string properties, so the portal and the log file would disagree
about the same event.

**Deleting a game is all-or-nothing, and usually impossible in production.** `Admin/AdminOperations.cs`
probes every parent directory for writability *before* closing a single lobby, because the bad outcome isn't
failure — it's removing the unpacked copy while leaving the `.kbg`, so the installer reinstalls the game and
the operator watches a deletion undo itself after their lobbies were torn down for nothing. `games/` is
mounted `:ro` in the shipped compose file and the server only ever reads it, so the API answers **409** (a
deployment limit, not a fault) and the portal disables the button with the blocking path named. Disk usage
counts the game folder **plus** its compressed cache **plus** its source `.kbg`; reporting only the first
understates a large WASM game by roughly its own cache. A missing `web/admin` is reported through `DeploymentDiagnostics` **and** answered with an
explanatory 503 at the origin, because the warning page only replaces the *shell* home page.
Auth is one PBKDF2-hashed password (min 12 chars, file created mode `600`) in `AdminPasswordPath`
(`Security/AdminAuthService.cs`) — **claim-on-first-use**: while no password is set, whoever reaches the
origin sets it, which is why compose binds 8082 to loopback. Claiming writes with `FileMode.CreateNew`, so
concurrent setups yield one winner and one 409 — a check-then-write let both "succeed" and left the loser
holding a cookie signed under a key that no longer existed. The session cookie's HMAC key is derived from a
per-process secret **plus a fingerprint of the stored hash**, so any change to that file revokes all
sessions (a reset actually locks an intruder out); its `Secure` flag follows the request scheme **or** an
`https://` `AdminOrigin`, since behind a TLS proxy without `ForwardedHeaders` the request here is plain
HTTP. The file *is* the credential — write access to it is
total control, and rollback is deliberately not defended against (it needs state the attacker doesn't
control); filesystem permissions are the boundary. Password attempts are rate-limited **before** hashing —
at 600k PBKDF2 iterations (~0.4s of a core) an unthrottled endpoint is an unauthenticated CPU-exhaustion
lever, not just a guessing oracle — by **two** buckets: per IP
(`AdminLoginAttemptsPerMinute`, `Networking/IpRateLimiter.cs`) for fair share, plus a server-wide one
(`AdminLoginAttemptsPerMinuteGlobal`, `TokenBucket`) that bounds CPU regardless. The second exists because
the first keys on an address `X-Forwarded-For` can invent unless `KnownProxies` names the proxy.

**Port-binding trap (this shipped broken once):** `Program.cs` binds all three origins itself *only* when
nothing else set ports. Any explicit `ASPNETCORE_URLS` / `ASPNETCORE_HTTP_PORTS` / `Kestrel:Endpoints`
**replaces** that list instead of adding to it, and `GamesPort`/`AdminPort` only tell the *router* which
port maps to which origin — they bind nothing. So every origin must be listed in `launchSettings.json`
`applicationUrl`, the Dockerfile's `ASPNETCORE_HTTP_PORTS`, and any env you set, or it is routed but never
listened on and answers `connection refused`. A startup check logs the address each origin actually bound
and warns when the admin port isn't among them; `OriginPortBindingTests` asserts the repo's own files stay
in sync. NOTE: `launchSettings.json` is parsed as **strict JSON** — a `//` comment there makes the whole
profile silently fail to apply (falling back to Production + the built-in ports).

### Identity & tickets (ephemeral by design)
`Security/TokenService.cs` issues HMAC-SHA256 signed tokens. The signing secret is **random
per process** and never persisted — restarting the server invalidates all tokens and lobbies;
this is intentional (anonymous, no accounts).
- **Identity token** (`{playerId, exp}`, ~30d TTL): minted on `Hello`, stored in the shell's
  `sessionStorage` (per-tab), proves ownership on reconnect.
- **Game ticket** (`{playerId, lobbyId, gameId, exp}`, ~12h TTL): scoped to one lobby+game,
  handed to the game iframe via the **URL fragment** (never query/Referer/logs). On `Attach`,
  validity is re-checked against **live lobby membership** (primary) plus ticket signature/expiry.

### Game discovery & hot-reload
`Games/GameCatalog.cs` scans `<root>/*/GAME.json` at startup and on change (debounced
`FileSystemWatcher`, plus the polling fallback above). It is **multi-root**: `games/` first, then
`GamesUnpackedRoot` (where `.kbg` packages are extracted), and the first root to claim an id wins with
a warning on duplicates. Only `roots[0]` is watched/polled — the installer owns the other root and
triggers rediscovery itself. The folder name **must equal** the manifest `id`, and `entry` is
path-traversal–checked to stay inside the game folder. **One** dictionary of `GameEntry(manifest,
directory)` is swapped atomically — two parallel dictionaries could not be swapped together, letting a
reader pair a pre-swap manifest with a post-swap path. `TryGetDirectory`/`GameLocations` expose the
resolved path (never put it on `GameManifest`, which goes over the wire); `Count` reads the game count
without `Games`' snapshot allocation. A games dir
that is missing OR present-but-unreadable (e.g. a Docker mount the UID-1654 user can't read) does
**not** crash startup: `Discover()` catches the access error and exposes `GameCatalog.ScanError`,
which `Hosting/DeploymentDiagnostics.cs` surfaces (with other file-access problems found at
bootstrap) by replacing the shell home page with `Hosting/DeploymentWarningPage.cs` — see the
home-page warning middleware in `Program.cs` and `docs/HOSTING.md`.

**A rescan that changed nothing logs nothing new.** Discovery re-runs on every file event, on the
bind-mount poll and whenever `GamePackageInstaller` asks for another pass, so the overwhelming majority of
scans find exactly what the last one found — and reporting the whole catalog each time buried the one pass
that mattered, both in the log file and in `AdminLogBuffer`'s bounded ring, which is what an operator reads
when something has gone wrong. `GameCatalog.PassLog` therefore **defers** the routine lines (`Discovered
game …`, `Skipping game …`, `Game catalog ready …`) and picks their level at the end of the pass: as asked
for when the pass changed something, `Debug` when its signature matches the last published one. Template and
args are kept apart so the eventual call is still structured — the portal filters on level and
`SourceContext`, and pre-rendering would hand it one opaque string. Exceptions and an unreadable games root
are logged where they happen and never demoted; an unreadable primary root also **clears** the signature, so
the pass that recovers reports the whole catalog rather than matching a pre-outage one and going quiet.
`Discovered` still fires on every pass — only the logging is conditional, and a test says so, because the
installer and the precompressor reconcile off that event.

GAME.json fields: `id`, `name`, `entry` (entry HTML), `thumbnail`, `maxPlayers`, `version` (optional,
never validated — the marketplace's installed-side version, see below),
`crossOriginIsolated` (optional, for threaded engine exports), and `themeColor` /
`themeTextColor` (optional CSS colors the shell tints the in-game header chrome with;
shell-validated, so invalid values are ignored — no CSS injection), and `sdk` (optional
`{ "<addon>": "<version>" }`, stamped by `knockbox pack`, never validated — see Addon distribution).

Five more feed the home page's search, filter and sort controls, and all are **display metadata**:
`minPlayers` (defaults to 1), `tags`, `description`, `createdAt`, `updatedAt`. Two catalog behaviours
are worth knowing because both were bugs first. `minPlayers` is **clamped** into `[1, maxPlayers]`
with a warning rather than skipping the game: a game must not vanish over a cosmetic field, but left
as declared an inverted range renders "4–2" in the chin bar *and* makes the shell's player-count
filter false for every option, so the game is reachable only via "All" with nothing to explain it.
`knockbox pack` takes the opposite side of that same split and **rejects** the range outright — the
author is standing in front of the message, which is the one place the typo can still be fixed.
And the dates, when the author declares none, are derived from the manifest file — `createdAt` via
`GameCatalog.FileCreated`, which falls back to last-write time where **there is no birthtime**
(overlayfs: a container's own layers, and the usual backing for the unpacked-games volume, where
.NET reports 1601-01-01 for every file, tying the whole catalog under the default "Newest" sort and
silently degrading it to the alphabetical tie-break). A derived `createdAt` therefore means "when
this build appeared on this server", not when the game was authored — a package-backed game is
re-extracted on every update, so an author who wants a stable position under "Newest" declares
`createdAt` themselves. `tags`/`description` are never validated; `web/kb-core.js` `normalizeTags`
is the one rule for what counts as a renderable tag, shared by the chips, the tooltip and the
search, so they cannot disagree about a `GAME.json` declaring `["", null, 3]`.

### `.kbg` game packages
A game can be installed as a single `.kbg` file instead of a folder: copying it into `games/` is the
whole procedure, no CLI and no restart. Spec: `docs/KBG_FORMAT.md`. It is a ZIP with every entry
**stored**, a `KBG.json` header (`formatVersion`, `id`, `files[]`), and per-file **Brotli** payloads
under `<path>.br`. Brotli rather than deflate/LZMA because it is built into both .NET and Node (zero
new dependencies, keeping the `aot` job clean) and lands within ~3% of LZMA; per-file rather than solid
so `GameAssetPrecompressor.SeedFromPackage` can copy the blobs straight into `games-compressed/`,
skipping the ~49s-per-asset max-effort Brotli the server otherwise pays on every cold boot.

`Games/GamePackageInstaller.cs` extracts into `GamesUnpackedRoot` (the `games/` mount is read-only in
production). It owns **no watcher and no timer**: it hangs off `GameCatalog.Discovered` and calls
`catalog.ScheduleRescan()` — never `Discover()`, which has no mutual exclusion and could let an older
scan win the publish. `ComputeFingerprint` therefore also stats `*.kbg`; without that, packages would
never install under Docker, where the poll is the only signal that fires. A package must present the
same (mtime, length) on **two** consecutive passes before it is read (so a half-copied archive never
is), and must be absent for two passes before its game is uninstalled (so delete-then-copy doesn't
drop a live game). `Reconcile()` returns `Pending` for both of those deferrals — the caller must
rescan, or that work stalls until an unrelated file event arrives.

A **missing or unreadable package root is skipped, not fatal to the pass.** The hazard the old
whole-pass bail guarded against is real but narrow — an unreadable root is indistinguishable from every
package in it having been deleted — so it is answered by treating games whose `PackageMarker` names that
root as live (never uninstalled) while the healthy roots install and uninstall normally, and by reporting
it through `InstallFailure` so it reaches the deployment-warning page. Bailing outright meant one bad root
silently switched off `.kbg` hot-drop for the other one too.

`Games/GamePackageReader.cs` treats packages as untrusted: full validation before any byte is written,
manual entry iteration (**never** `ZipFile.ExtractToDirectory` — no caps, can't pre-validate, and on
.NET 7+ it restores the entry's Unix file mode), strict path rules, and byte caps counted **while
copying** because declared sizes are attacker-controlled. Tests: `GamePackageReaderTests` (validation),
`GamePackageInstallerTests` (lifecycle), `PackageFixture` (builds deliberately malformed packages).

### The official marketplace (`KnockBox.Server/Marketplace/`)
Where admins get games from: a catalog index (`.plugins/CATALOG.json` in the separate
`jcub1011/KnockBox-Games-Marketplace` repo) listing one entry per published game, each pointing at a
`.kbg` on a GitHub release. `MarketplaceClient` is the **only outbound HTTP in the server** — a plain
singleton `HttpClient` over a `SocketsHttpHandler` with `PooledConnectionLifetime`, deliberately not
`IHttpClientFactory`, so no new package has to clear the AOT gate. Spec + trust model:
`docs/MARKETPLACE.md`.

**The install engine (`Games/PackageManager.cs`, `Games/PackageJobRegistry.cs`).** `DownloadAsync` still
installs nothing — `PackageManager` places what it hands back, and the same `PlaceAsync` serves a
download, an upload and a rollback, so none of them re-decides what "valid" means. Every path re-runs
`GamePackageReader.Read`, **including rollback**: a file that has sat on disk for months is not more
trustworthy than one off the network.

Packages the portal installs land in **`GamesManagedRoot`** (default sibling `games-managed`), a writable
*package* root the installer also scans — `games/` is `:ro` in production, so the portal cannot write
there at all. Extraction still goes to `GamesUnpackedRoot`, so the catalog's root list and asset serving
are unchanged, and `games/` still wins a contested id. Layout in `Games/ManagedPackageLayout.cs`. Unlike
the two derived caches this root is **not regenerable**: a marketplace package can be re-fetched, an
uploaded one exists nowhere else.

`PlaceAsync`'s order is load-bearing: **copy** the current package to `.backups/` (a *move* would leave
the id with no package, and a reconcile pass landing in that window starts the two-pass uninstall
countdown on a healthy game), then one `File.Move(..., overwrite: true)`, then stamp the mtime **strictly
past** the previous value — the installer keys freshness on `(mtime, length)`, so two same-length versions
inside one filesystem tick would otherwise look identical and the second would never extract (a rollback
is the likeliest way to hit that). `GamePackageInstaller.Adopt` then vouches for the file so it installs
on the next pass rather than waiting out the two-pass settle check, which exists for copy-in and has no
bearing on an atomic rename.

**The overwriting rename is retried, and must never become delete-then-move.** `Hosting/AtomicFile.cs`
wraps `File.Move(..., overwrite: true)` in a tiny bounded retry (4 attempts / ~50 ms ⇒ ~150 ms). On Windows
that move is `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`, which needs delete access to the destination and so
fails outright while *anything* holds a handle — a virus scanner or the search indexer opening the file
microseconds after it was written is enough, and was: a marketplace download died with
`UnauthorizedAccessException` once in ~50 full test runs, discarding a verified package the next attempt
would have placed. Deleting first would trade that rare transient failure for a rare *permanent* one, which
is the window the single rename exists to close. The budget is small because every caller is holding
something — `AdminSettingsStore.Save` runs under `_writeGate` (a `System.Threading.Lock`, so the sync form
there is forced, not preferred) and `PackageManager.Place` holds an install slot. A real ACL denial or
read-only mount still fails, with the original exception, ~150 ms later. Four call sites use it:
`MarketplaceClient.DownloadAsync` (async, and it passes the **caller's** token, not its download deadline —
by then the transfer is verified, and `timeout.Token` would both abort a valid publish and mislabel it
"downloading timed out"), `PackageManager.Place`, `AdminSettingsStore.Save`, and
`GameAssetPrecompressor.SaveIndex`. The precompressor's two *per-file* moves deliberately do **not**:
they're already caught per file and retried by the next reconcile pass, a finer granularity than this adds
— but `SaveIndex` is caught per **game**, so losing it re-Brotlis the whole game at max effort.
Tests are split for a reason: a real sharing violation is unreproducible on the Linux CI runners
(`rename(2)` ignores open handles, and .NET's `FileShare.None` is an advisory `flock` that `rename` never
consults), so `AtomicFile.Retry` takes an injected operation for the portable tests, and the Windows-only
pair feeds a real `File.Move` through that same seam — releasing the handle from *inside* the operation, so
ordering is guaranteed rather than raced.

**`MoveDirectoryWithRetry` is the same primitive for a whole tree, and the directory case is the worse
one.** Windows denies a directory rename while **any** file anywhere beneath it is open without
share-delete, so a folder of files written microseconds ago offers a scanner one chance to lose per file,
not one per rename. `GamePackageInstaller.SwapIntoPlace` does exactly that shape — extract into
`.staging/<id>-<guid>`, rename it over the live folder — and without the retry it failed with "Access to
the path ….staging<id>-<guid> is denied" on roughly **one full test run in two**, a different package each
time. All three of its renames use it (move-aside, publish, and the rollback that puts the previous version
back). The log line said "it will be retried", and it was — but only on the next reconcile pass, which
comes from a file event or the poll, so under the image's 10-second `GamesPollSeconds` that is a
ten-second-late install, and for `PackageManager` it is long past the bounded wait on `Installed` that
holds the lifecycle gate. Note `Directory.Move` has no overwrite form, which is why the caller moves the
live folder aside rather than passing a flag.

**Placing is not installing, and the lifecycle gate spans both.** `Place` only renames the `.kbg` and asks
for a rescan; the extraction — and `SwapIntoPlace` moving the live directory aside — happens on a later
installer pass. So `ApplyAsync` waits on `GamePackageInstaller.Installed` (a new event, raised only for a
real extraction) before finishing the job and releasing `GameLifecycleGate`. Releasing it at the end of
`Place` meant a force update closed every lobby, reported success, re-opened the game, and served a player
who started a lobby in that window the **old** build — then 404s mid-session as it was swapped underneath
them, which is the exact outcome force and drain exist to prevent. The wait is bounded
(`PackageManager.ExtractionWait`) and times out into a job *warning* rather than holding the gate forever:
those states are never persisted precisely so a game can't be left permanently unstartable. `PackageManager`
subscribes in its own constructor rather than from `Program.cs` — its correctness depends on the event, and
a class must not need external wiring to reach its own invariant. The install **slot** is taken *after*
`WaitForLobbiesAsync`, not around it: a drain wait is open-ended, and holding the single slot across it left
every unrelated install queued behind one draining game.

**`_installSlots` is acquired in exactly two disjoint windows, and that is load-bearing.** `RunDownload`
holds it across the download only and **releases before calling `ApplyAsync`**, which takes it again for the
apply. Holding it across the call is a self-deadlock on the default `MaxConcurrentInstalls` of 1 — and it
shipped that way: every marketplace install wedged in `Verifying` forever while the download had in fact
succeeded, and since `Verifying` is `Cancellable` the job stayed cancellable, which is what made it look
like anything but a deadlock. The leaked permit then queued every later install, upload, rollback and
uninstall behind it until a restart. Uploads were unaffected (they never enter `RunDownload`), which is why
"upload works, marketplace doesn't" was the reported shape. `PackageManagerTests` now drives a real
marketplace install over `FakeHttpMessageHandler` and asserts `AvailableInstallSlots` returns to full after
every job kind — a leaked permit does not fail a job, it hangs one, so nothing in the job feed would say so.

**Jobs, not blocking requests** — a download plus extraction outlives any request and a drain is
open-ended, so every operation answers `202` + `jobId` and the portal polls a cursor change feed. This is
the **third** use of that house pattern (after `AdminLogBuffer`): still no SSE and no second socket role.
A job is cancellable until `Applying` and refused after — a half-swapped game directory is the one outcome
worth refusing to create, the same reasoning `DeleteGame` applies to a half-delete. Retention never evicts
an active job, so the cap is deliberately soft.

**Sources** (`Marketplace/MarketplaceSourceRegistry.cs`): one `MarketplaceClient` per source, since each
holds exactly one catalog+ETag pair and so cannot be parameterised by URL; all share one `HttpClient`
(`CreateHttpClient()` reads nothing source-specific — it used to take an options argument it never looked
at). Per-source options are `global with { CatalogUrl, DownloadBaseUrl }`; the caps and timeouts stay
shared because those are policy about *this server*. Registrations are validated with
`MarketplaceClient.IsAllowedUrl` — the downloader's own rule, exposed rather than copied. One unreachable
source is an error string, never a failed aggregate (`GameCatalog.ScanError` discipline).

`Marketplace/MarketplaceProjection.cs` merges catalogs against installed state and is
`PluginUpdateEvaluator`'s **first production caller**. It synthesizes `installedOnly` there rather than in
the enum, because every `PluginUpdateStatus` is a statement about a catalog *entry* and that one is a
statement about the absence of one.

**Update policy** is per game and persisted (`manual`/`auto`/`drain`/`force`, recorded by *absence* when
manual, like `Available`); `Admin/GameUpdateCoordinator.cs` is one pass of it. With nothing enrolled a pass
**makes no outbound request at all**, so a default deployment doesn't quietly start phoning home.

**When that pass runs is a wall-clock schedule, not an interval.** `Admin/UpdateSchedule.cs` is the
persisted policy (`off`/`hourly`/`daily`/`weekly` + day + hour, **UTC**, default daily at 03:00, recorded by
absence like `OperatorLimits`) and `Admin/UpdateScheduler.cs` owns a **one-shot timer re-armed after every
fire** — a periodic timer can only say "every N since this process started", which drifts off the chosen
hour on every restart and cannot express "Sundays at 3am" at all. Re-computing each time is also what lets
`Reschedule()` apply a portal edit to *this* process; the timer used to be a local in `Program.cs` with no
handle in DI, which is why it couldn't be. `NextDue` is **strictly after** the moment passed in — the
scheduler re-arms from the instant it just fired, and an inclusive answer would hand back that same instant
and spin. Out-of-range values go through `Normalize()` in `NextDue` and `Describe()` both, so a hand-edited
hour of 99 cannot mean 23:00 to the timer and 03:00 to the portal. A pass also runs **~30 s after boot**
(free when nothing is enrolled), and every due time carries up to 5 minutes of jitter because a fixed hour
would otherwise sync a whole fleet onto one second. The portal form lives on the **Platform** tab, not
Marketplace: it is a form, and Platform is the only tab that never polls (Marketplace re-reads its catalog
whenever a job finishes, which would re-render it mid-edit).

`Admin/GameLifecycleGate.cs` holds the transient `Draining`/`Updating` states and implements
`IPlatformPolicy` by composing `AdminSettingsStore` and ANDing its own answer. They are **never
persisted**: lobbies are in-memory, so after a restart every game has zero lobbies and a persisted drain
would be stale by construction — a server killed mid-update would come back with a game permanently
unlaunchable. `IPlatformPolicy` gained `UnavailableReason` so a draining game stays *listed* and refuses
with something a player can act on; `WebSocketHandler` passes that string through exactly as it already
does `MaintenanceMessage`, and still knows nothing about packages.

**`GamePackageLocations.Find` replaced three copies of `Path.Combine(GamesRoot, id + ".kbg")`.** The
installer accepts any `*.kbg` name and takes the id from the header inside, so that derivation was already
wrong for a hand-named package: it reported `packageBacked: false`, left the bytes uncounted, and — worst —
`DeleteGame` removed the unpacked folder while leaving the archive, so the installer put the game straight
back. The marker inside the extracted folder (`Games/PackageMarker.cs`, 4-field; a 3-field legacy marker
reads as `games`, which is exactly what it meant) is the authority.

**The catalog's commit history is the trust root, not the release** — a release asset can be
re-uploaded in place, so what the catalog commits to is a **`sha256`, which is required** (schema and
server) and enforced on every download. URLs are **derived**
(`{DownloadBaseUrl}/{repo}/releases/download/{tag}/{asset}`), never carried in the schema, so a
tampered entry has nothing to point elsewhere; `repo`/`tag`/`asset` are pattern-checked *before* any
request leaves the process. No GitHub API is used — deriving the URL dodges the 60/hr unauthenticated
limit, at the cost of making a malformed `asset` a hard error (it named `GAME.json` once; the schema
now requires `.kbg`). A download must also re-pass **the same `GamePackageReader.Read`** the installer
uses, and match the entry's id *and* version — never a second, weaker copy of that validation.
Catalog DTOs are all-nullable like `GamePackageHeader` (untrusted input must never throw on parse);
checking splits three ways — `Parse` (is it a catalog?), `ValidateEntry` (is it safe to act on?),
`PluginUpdateEvaluator` (is it meaningful?).

`GameManifest.Version` (new, optional, never validated) is a game's self-declared build label and the
installed side of the update check; `SemVer` in `PluginVersion.cs` implements real semver 2.0.0
precedence because string comparison inverts both `0.9.0 < 0.10.0` and `1.0.0-rc.1 < 1.0.0`.
`Incompatible` (min/maxAppVersion vs `Hosting/KnockBoxVersion.cs`, read off the assembly so csproj
`<Version>` is the one source of truth) **outranks** `UpdateAvailable` — never offer an update that
can't run; and an unparseable bound counts as incompatible, since a constraint we can't read isn't no
constraint. An operator may still force one through ("Install Anyways"), and `InstallFromMarketplace`
then sets `GameAvailability.Staged` **before** starting the job: `GameManifest` carries no version
bounds, so nothing server-side can tell an extracted game was force-installed, and staged is the one
state that lets the operator try it without a player being able to start a lobby against it. Set at
request time rather than on completion because availability is keyed by id and persisted — so there is
no window in which the new game is listed. `InstalledVersionUnknown` is deliberately distinct from `UpdateAvailable`: every
hand-made game has no version, and nagging about all of them is noise.

### Serving game assets & pre-compression
Game builds are served with stock `UseStaticFiles` (ETag + `must-revalidate`, so unchanged
assets — esp. the large `.wasm` — return `304`). To avoid re-compressing the same static bytes
on every cold request, `Games/GameAssetPrecompressor.cs` keeps a derived cache of max-effort
(`CompressionLevel.SmallestSize`) `.br`/`.gz` variants under `GamesCompressedRoot`
(default sibling `games-compressed/`, **writable, outside the read-only `games/` mount**).
`GameAssetPrecompressor` is **root-agnostic** — `ReconcileAll` takes an id→manifest+directory map (from
`GameCatalog.GameLocations`), because a game's files may sit under either root. Both former uses of
`gamesRoot` had to change together: fixing the compression side alone would leave the prune side
deleting every package-backed game's cache each pass and recompressing it at max effort.
Reconciliation is driven by `GameCatalog.Discovered` plus a periodic timer
(`PrecompressReconcileSeconds`) — it (re)compresses changed files (mtime/length freshness),
prunes orphaned variants, and removes directories for deleted games. A negotiation step on the
game origin (`Program.cs`) rewrites a request to the `.br`/`.gz` variant when present and
accepted (`Accept-Encoding`), serving it with `Content-Encoding`/`Vary` and the **decompressed**
content-type; a miss falls through to the raw file. The on-the-fly `ResponseCompression`
(Brotli/Gzip at `Fastest`) stays as the fallback for not-yet-warmed assets and the shell origin —
it skips bodies that already carry `Content-Encoding`, so precompressed responses aren't
re-compressed. Disable the whole cache with `Precompress=false`.

### Lobbies & connections
- `Lobbies/Lobby.cs` / `LobbyManager.cs` — in-memory lobbies keyed by a 4-char human code;
  membership is lock-guarded and `Players` is returned as a snapshot so broadcasts can't race
  membership changes. Kicking bars rejoin for that lobby. A dropped **control** socket doesn't
  remove the player immediately: with `DisconnectGraceSeconds` (default 60) set they're flagged
  disconnected but kept in the lobby (so a tab refresh / blip survives — `PlayerDisconnected` /
  `PlayerConnected` events tell peers), and a periodic reaper in `Program.cs` calling
  `WebSocketHandler.ReapDisconnectedPlayers()` evicts those whose grace elapses. `0` = old
  immediate-leave behavior. A lobby with no connected members left (empty, or all disconnected) is
  closed immediately (`CloseLobbyIfDark`) rather than held — the grace only helps when someone's
  still there. Explicit leaves (`LeaveLobby`) are always immediate.
- `Networking/Connection.cs` — wraps one socket with a bounded single-reader outbound
  `Channel` drained by one writer task (preserves order). Overflow policy differs by role:
  control = `CloseOnFull` (events are precious), data = `DropOldest` (state is ephemeral).
- `Networking/ConnectionManager.cs` — separate registries for control vs. game sockets; one
  player may hold both during a session.

### Abuse protection (`Networking/ServerLimits.cs`, `TokenBucket.cs`, `IpConnectionGate.cs`)
Handshake timeout on `/ws`, per-connection token-bucket rate limits (separate for control vs.
data planes), per-IP connection cap, and a per-player lobby-create throttle. All are
configurable and disabled with `0`.

### Server-authoritative mode (`Games/ServerAuthority*.cs`, `Games/*AuthorityRuntime*.cs`)
Optional, per-game. A game opts in with `GAME.json` `serverAuthority: "authority.js"` (validated
like `entry`; the game origin never serves the module — `Hosting/GameOriginAssetGate.cs` → 404). That gate
compares **canonical** paths on both sides via `Hosting/GameAssetPath.cs` (the one parser for
`/games/{id}/{relative}`, also used by the thumbnail allowlist, the COOP/COEP header hook and the
pre-compressed negotiation): raw string equality denied `…/authority.js` but waved through
`…//authority.js`, which `PhysicalFileProvider` then resolved to the very same file.
On lobby creation `ServerAuthorityManager` loads the module into a per-lobby sandboxed **Jint**
engine (`JsAuthorityRuntime` behind `IAuthorityRuntime`; `Date` deleted, no CLR, memory/timeout/
statement/recursion budgets — AOT-clean via `TrimmerRootAssembly`) wrapped in a `ServerAuthority`
actor: one drain task over a bounded `Channel` (two-tier overflow — intents drop, ticks coalesce,
roster never dropped). `WebSocketHandler.HandleGameMessage` diverts `to:"host"` to the actor
instead of a client and enforces the `_kb` envelope both ways (§5d — clients can't publish
`delta`/`state`); the actor broadcasts state stamped `from:"server"`. The module ABI is the
`kb-authority.js` model contract as a superset (`init`/`applyIntent`/`snapshot` + roster hooks +
`tick`/`config`), runtime-agnostic (JSON strings in/out) so a WASM backend is additive (Phase 4,
not built). **Owner ≠ authority**: every client is `isHost:false`; `Lobby.HostId` (now mutable,
lock-guarded) is the owner holding kick/open powers, reassignable by the module via `kb.setOwner`
(`OwnerChanged`/`GameOwnerChanged` events, so the session survives the creator leaving). Errors:
a module throw is contained (drop + re-broadcast snapshot; 5 in a row → fatal), a constraint
violation is fatal (`LobbyClosed`, sockets aborted). See `docs/SERVER_AUTHORITY_DESIGN.md` and
GAME_DEVELOPER_GUIDE §5b. Knobs: `Authority*` (§Configuration).

**Shared word dictionaries (`kb.words`)**: a game declares immutable word lists in `GAME.json`
(`authorityWords: { "<key>": { file, caseInsensitive } }`, validated like `serverAuthority` + a size
cap, and requiring `serverAuthority`). `Games/Words/AuthorityWordService.cs` (DI singleton) loads each
file **once** into a shared, length-bucketed packed-ASCII structure (`WordPool`/`WordPoolSet`, adapted
from the sibling `KnockBox.WordService` repo) shared by every lobby engine of the game and deduped
across games by content hash (byte-identical files share one structure regardless of name/path) — so a large dictionary costs one copy, never a per-lobby copy or a
raised memory cap. The module queries it via `kb.words.has/count/pick/countOfLength/pickOfLength`
(`ClrFunction`s over the shared pool — the dictionary never enters the JS heap; guarded, so unknown
key / out-of-range → `false`/`0`/`null`). The word files are denied on the game origin
(`GameOriginAssetGate`, server-side/secret) and skipped by the precompressor; `knockbox-local.js`
emulates `kb.words` with server-identical `pick` ordering. GAME_DEVELOPER_GUIDE §5b walks a
worked example; the docker CI job synthesizes one to prove the files 404 on the game origin.

**Server authority + `.kbg` packages**: the two compose. An authority game packs like any other
(`knockbox-pack` ships `serverAuthority` / `authorityWords` files inside the archive) and installs
into `GamesUnpackedRoot`, so its files are **not** under `GamesRoot/<id>`. Everything server-side
therefore resolves a game's folder through the catalog — `GameCatalog.TryGetDirectory` /
`GameLocations`, never `gamesRoot/<id>`: `ServerAuthorityManager` takes a
`Func<string, string?> gameDirectory` resolver, and the `Discovered` event carries
`GameLocation(Manifest, Directory)` so the precompressor, word-pool prune and module-cache prune
all see both. The origin gate is path-based and so is unaffected by which root won; the installer
skips seeding compressed variants of the never-served files.

### Web SDK (`web/knockbox.js` + `web/kb-protocol.js`)
Games load `<script type="module" src="/knockbox.js">`, which ES-imports `./kb-protocol.js` — so the
game origin must serve **both** (it mounts the whole `web/` folder, and a CI smoke check asserts the
sibling, since a 200 on the SDK alone proves nothing about its imports). `kb-protocol.js` is the
game-facing protocol core — the 11 symbols a game needs — and `kb-core.js` **re-exports** all of them
so `shell.js`/`admin*` import from it unchanged. The split exists because `kb-core.js` is the *shell's*
module (favicons, colour math, launch-overlay geometry, play log, announcements) and `/knockbox.js`
used to drag all ~21 KB of it into every game to reach 9 symbols; it is also what makes the `web` addon
a real 2-file package. Add a game-facing helper to `kb-protocol.js`, a shell-only one to `kb-core.js` —
`web/__tests__/client-parity.test.js` pins that export list and compares it against the Phaser and
Godot ports (with a named allowlist for the Godot gaps, which fails if it goes stale either way).

Key API: properties `playerId`,
`players`, `isHost` (plus `authority`/`ownerId`/`isOwner` for server-authority mode, normalized via
`kb-protocol.js` `normalizeReady`); callbacks `onReady`, `onMessage`, `onPlayerJoined`, `onPlayerLeft`,
`onPlayerDisconnected`, `onPlayerConnected` (the last two: a peer's tab dropped but is held for the
reconnect grace window, then returned — they stay in `players` throughout), `onOwnerChanged`; send
methods `sendToHost`, `sendToAll`, `sendTo(playerId, …)`, host-only `setLobbyOpen`,
`log.{info,warn,error,debug,trace,critical}(message)` (console-like logging to the server, relayed
as a `LogMessage` and written under the `KnockBox.GameLog` category), and `logPlay(metadata)` (a
`<string,string>` bag sent as a `PlayLogMessage`; the server stamps gameId/timestamp/isHost and
forwards it to that player's **control** socket, where the shell persists the most-recent 50 in
`localStorage` (`kb.playLog`) and renders them in the home-page Play Log).
`web/shell.js` owns the control socket and lobby UI; `web/kb-core.js` holds pure, tested
shell helpers plus the `kb-protocol.js` re-exports (reconnect/backoff, fragment parsing, roster reducers, Play Log: `appendPlayLog`,
`partitionPlayLogMetadata`, `ordinal`; launch copy: `launchMessage`). Close code **1008** is
terminal (no reconnect); other closes back off exponentially.

**Launch overlay.** Clicking a game tile is covered by a "Starting {GameName}…" overlay
(`#launch-overlay`) from the click until the game iframe's `load` event — two socket round trips
plus, on a first play, a multi-megabyte asset download, all of which used to look like a frozen
page. Determinate progress is deliberately *not* attempted: the iframe is cross-origin (no
resource-timing) and `GameManifest` carries no asset sizes. It is raised in `createLobby`/
`joinByCode` (name known synchronously from the catalog) and in `enterGame` (which is the *only*
path on a rejoin — `tryRejoin` ignores its reply), and retired by the `load` handler,
`showError`, `showLobbyView`, or a hard `LAUNCH_MAX_MS` ceiling so a missed event can't hide a
running game. Two counters guard it: `launchSeq` drops a stale `load`, and `launchAbortSeq` makes a
deliberate bail-out reject the in-flight reply (and leave the lobby the server made anyway).

It is presented as a **continuity transition, not a card**: the clicked tile itself is flown from its
grid rect to the centre (a FLIP — `launchFlipFrom`/`rotationFromMatrix` in `kb-core.js` do the math,
`flyLaunchTile` sets one inline start transform and clears it after a forced reflow), straightening
out of its `nth-child` rotation, while `#lobby-view` dissolves and the dot ticker rises. Nothing large
arrives, so a warm launch reads as a lift rather than a flash. The destination size is derived from the
source (1.25×, viewport-capped) — a *fixed* size is a shrink on a wide window, since the grid columns
are elastic. Three consequences worth knowing before touching it:
- **`z-index: 100`, above** `.game-header`'s `99` (which competes in the root stacking context). The
  header must not slide in over the flying tile at a moment network timing decides, so the launch owns
  the screen and `#launch-cancel` is the only way out of a stall.
- **There is no scrim.** The overlay is transparent so `body::before/::after` keep running in phase —
  a scrim of our own could not be phase-matched to the scrolling stripes. That means the game view has
  to be revealed (it must be in the layout to download) but veiled: `#game-view.launch-veil`, plus
  `body.in-game` withheld, both released as the overlay fades. `visibility`, never `display:none`,
  which would entitle the iframe to defer loading.
- **Nothing of the launch is ever drawn over a running game.** On the iframe's `load` —
  `hideLaunchOverlay(true)`, the only caller that passes it — the tile *hands over*: `startGameMorph`
  drops the overlay outright and in the same frame plants `#game-view` in the exact rect the tile had
  reached (mid-flight or settled, rounded corners and all), then expands it to fullscreen like a video
  (`LAUNCH_MORPH_MS`/`LAUNCH_MORPH_EASING`). That expand is a **Web Animations call, not a CSS
  transition** — a transition has to be armed by writing a start value and clearing it, which made it
  a hostage of style-recalc ordering and was seen sticking at the start matrix with `playState`
  `running` and `transition-duration: 0s`, freezing the game at tile size. Its safety timer is held
  outside `launchTimers` so ending the morph cancels it; a stray one strips the class off whatever
  launch is running by then. The scale is deliberately
  **non-uniform** — matching the tile's rect on both axes is what sells it, and a uniform scale would
  start the game at nearly full height on a portrait phone. `body.in-game` is withheld until the morph
  ends, because that's the first moment the game covers the screen and the background can swap
  unseen. Every other ending (an error, a bail-out, the `LAUNCH_MAX_MS` ceiling, a launch that never
  had a tile) takes the `LAUNCH_EXIT_MS` fade instead. Fading a loading screen away over a game that
  has already arrived was tried and rejected as clunky — don't reintroduce it.
- Both durations mirror `home.css`; change them together. `clearGameMorph` runs from `showLobbyView`
  and `showLaunchOverlay` as well as on completion — a stranded inline transform on `#game-view`
  breaks the next session. And `#lobby-view.is-launching` is cleared in two places on purpose: leave
  it stuck and the home page renders at `opacity: 0`.

### Logging (server side)
Serilog is the host logger (`builder.Host.UseSerilog` in `Program.cs`): console + a **daily**
rolling file at `LogsRoot/knockbox-YYYYMMDD.log`, retained for `KnockBox:LogRetentionDays`
days (default 31). Game logs relayed via `LogMessage` land under the `KnockBox.GameLog`
category so they're filterable. Crucially, levels/sinks are configured **in code, not** via
`ReadFrom.Configuration`: that package's assembly scanning is not Native-AOT-safe and emits
IL2104/IL3002/IL3053 at publish — `ReadFrom.Services` (DI-only) is used instead. Touch this
setup carefully to keep the `aot` CI job green.

## Configuration

All knobs use the `KnockBox:` prefix (env: `KnockBox__Key`, `__` for nesting). Full reference
in `docs/INFRASTRUCTURE.md` §9. Frequently relevant: `GamesRoot`/`WebRoot`/`LogsRoot`,
`GamesPort`/`GamesHost`/`GamesOrigin` and `AdminPort`/`AdminHost`/`AdminOrigin` (origin routing),
`AdminPasswordPath`/`AdminSessionTtlHours`/`AdminSettingsPath` (admin portal; both paths must be writable
and, in a container, on a persisted volume outside the image — the settings file is the only operator state
that survives a restart), `AdminStaleLobbyMinutes`/`AdminLogBufferSize`/`AdminDiskUsageCacheSeconds`
(dashboard behaviour; see `docs/ADMIN.md`), `GamesPollSeconds` (hot-reload
fallback), `Precompress`/`GamesCompressedRoot`/`PrecompressGzip`/`PrecompressMinBytes`/`PrecompressReconcileSeconds`
(pre-compressed game-asset cache; **`PrecompressGzip` defaults to `false`** — a `.kbg`'s Brotli blobs are
copied verbatim for free, but a `.gz` has to be built here at max effort, and the ~3% of clients without
Brotli still get gzip on the fly), `Packages`/`GamesUnpackedRoot`/`MaxPackageBytes`/`MaxPackageEntries`/`MaxPackageRatio`
(`.kbg` install; the root must be writable and outside `games/`),
`MaxLobbies`/`MaxLobbiesPerGame` (capacity caps; also editable at runtime from the portal and persisted),
`MetricSampleSeconds`/`MetricHistoryPoints` (the dashboard's time series; `0` = off),
`WebhooksEnabled`/`MaxWebhooks`/`WebhookTimeoutSeconds`/`WebhookErrorsPerMinute`/`WebhookMemoryThresholdMb`/`WebhookCpuPercentThreshold`
(outbound webhooks; `Enabled=false` ⇒ no dispatcher and no HttpClient at all),
`GamesManagedRoot`/`ManagedPackages`/`PackageBackupCount`/`MaxConcurrentInstalls`/`PackageJobRetention`
(portal installs; the managed root must be writable, outside `games/`, and — unlike the caches — backed up),
`MarketplaceUpdate{Cadence,HourUtc,DayOfWeek}`/`MarketplaceMaxSources` (the *starting* update schedule —
the portal overrides it and persists — and extra catalogs),
`Marketplace{Enabled,CatalogUrl,DownloadBaseUrl,MaxCatalogBytes,MaxDownloadBytes,CatalogTimeoutSeconds,DownloadTimeoutSeconds}`
(official marketplace; `Enabled=false` ⇒ no outbound HttpClient at all), `LogRetentionDays` (daily log files kept under `LogsRoot`, default 31),
`ForwardedHeaders`/`KnownProxies`/`AllowedOrigins` (behind a reverse proxy),
`*TokenTtlHours`, `DisconnectGraceSeconds` (reconnect grace before a dropped member is removed,
default 60; `0` = immediate), the rate-limit knobs (`*MessagesPerSecond/Burst`,
`MaxConnectionsPerIp`, `LobbyCreatesPerMinute`, `AdminLoginAttemptsPerMinute`/`…Global`), and the server-authority knobs (`AuthorityEnabled`
master switch, `AuthorityMax{MemoryBytes,Statements,ScriptBytes,WordFileBytes,Lobbies}`,
`AuthorityCallTimeoutMs`, `AuthorityRecursionLimit`, `AuthorityTickHzMax`, `AuthorityQueueCapacity`).
