# knockbox-cli — the KnockBox game developer CLI

Two jobs, one tool:

```sh
npx knockbox pack  --in dist --manifest GAME.json   # package a game into a .kbg
npx knockbox addon add godot                        # install / update / verify a client addon
```

`knockbox-pack` remains as an alias for the packer, and bare flags with no subcommand still run it,
so every existing `knockbox-pack --in …` invocation keeps working. The full addon reference — index
format, trust model, engine-specific install — is [`docs/ADDONS.md`](../../docs/ADDONS.md).

## addon — keeping the client libraries current

The **addons** are the client libraries your game embeds: the Godot 4 GDScript addon (`godot`), the
Phaser 3 client (`phaser`), and the vanilla JS SDK (`web`). They are *vendored* — they live in your
repo and ship inside your build — so unlike a server-side dependency they do not update themselves.
This is the loop for keeping them current.

> Plain HTML5 games usually need none of this: the platform serves the SDK at `/knockbox.js`, so you
> are always on the server's own version. The `web` addon is for vendoring a pinned copy instead
> (offline development, or an engine export that would rather bundle than depend on the platform).

### Install

```sh
npx knockbox addon add godot        # or phaser, or web
```

Two things land in your project, and **both belong in version control**:

```
addons/knockbox/…      the addon's files
knockbox.json         which version you installed, plus a sha256 per file
```

`knockbox.json` is what makes everything below possible: it is how `check` knows whether a file has
been altered, how repair knows what to put back, and what `knockbox pack` reads to stamp the SDK
version into your `.kbg`.

### The update loop

```sh
npx knockbox addon check            # anything to do?
npx knockbox addon update           # do it (no id = every addon installed)
# rebuild + repack your game so the new client code actually ships
```

`check` changes nothing and is safe to run anywhere, including CI:

```
  godot    1.0.0      update available: 1.1.0
  web      1.0.0      update available: 1.1.0
```

It exits non-zero for a **broken or incompatible** install, but **zero** when an update is merely
available — a newer version existing is not a build failure, and a check that failed on it could not
be left in CI.

`update` moves each addon to the newest published version:

```
✓ godot 1.0.0 -> 1.1.0
  updated   1 file
✓ web 1.0.0 -> 1.1.0
```

`updated` counts files that changed between the two versions. Files you edited yourself are reported
separately and by name — see below.

**Updating the addon does not update your build.** The addon's code is compiled into your Godot
export or bundled by Vite, so after updating you have to rebuild and repack (`knockbox pack`) for
players to get it. Nothing enforces that; the SDK stamp in the packaged `GAME.json` is what lets an
operator notice a game still running on an old client.

### When you have edited an addon file

`update` refuses rather than silently discarding the change, and names the file:

```
✗ godot: refusing to update 'godot': these files differ from the installed 1.0.0 and the change
  would be lost:
  addons/knockbox/kb_net.gd
Re-run with --force to discard them, or `knockbox addon add godot` to restore 1.0.0 first.
✓ web 1.0.0 -> 1.1.0
```

Note the other addon still updated. One addon with a local edit does not block the rest; the command
exits non-zero so you cannot miss it.

From there you have three options:

| You want to | Run |
| --- | --- |
| Throw the edit away and update | `npx knockbox addon update godot --force` |
| Throw the edit away, stay on this version | `npx knockbox addon add godot` |
| Keep the edit and update everything else | `npx knockbox addon update godot --force --keep-modified`† |

† `--keep-modified` leaves altered files untouched, which means you are now maintaining a fork of
those files. `check` will keep reporting them, deliberately.

The two commands take **opposite defaults**, and it is worth knowing why:

- **`add`** at the version you already have means "make this pristine" — overwriting is the entire
  request, so it overwrites and tells you what it replaced. That is also why there is no separate
  `reset` command.
- **`update`** changes to a *different* version, where losing your edit is a surprise you did not ask
  for — so it stops and makes you say `--force`.

### Repairing a broken install

`check` diagnoses, `add` repairs:

```
$ npx knockbox addon check
  godot    1.0.0      NEEDS REPAIR
    MODIFIED addons/knockbox/kb_core.gd
    MISSING  addons/knockbox/kb_net.gd

repair with `knockbox addon add <id>` — it reinstalls the recorded version.

$ npx knockbox addon add godot
✓ reinstalled godot 1.0.0
  installed 1 file
  restored  addons/knockbox/kb_core.gd (local changes discarded)
```

Repair only touches files recorded in `knockbox.json`. Your own scripts living in
`addons/knockbox/` are never removed, and neither is the directory holding them.

### Pinning and rollback

```sh
npx knockbox addon add godot --version 1.0.0     # install a specific version
npx knockbox addon update godot --to 1.0.0       # move back to one
```

Older versions are served from the published index's history, never from a guessed URL — so a pinned
version is one with a verified `sha256` behind it. A version the index does not publish is refused
rather than fetched unverified.

### Godot, without a terminal

Godot developers do not need this CLI at all. Install from **Project → AssetLib**, then use
**Project → Tools**:

- **KnockBox: check for addon updates** — the `update` equivalent, including the refusal on an edited
  file.
- **KnockBox: reinstall addon** — the `add` repair equivalent.

Same index, same `sha256` verification, same `knockbox.json`. The two paths are interchangeable: a
project set up in the editor can be updated by the CLI later, and vice versa.

### Command reference

| Command | Does |
| --- | --- |
| `addon add <id> [--version <v>]` | Install, or reinstall to repair. |
| `addon update [id] [--to <v>]` | Update; no id updates everything installed. |
| `addon check [--app-version <v>]` | Verify files, report updates. Changes nothing. |
| `addon list` | What is installed, from `knockbox.json`. |
| `addon remove <id>` | Uninstall exactly the files that were installed. |

Options: `--dir <dir>`, `--index <url|path>`, `--download-base <url>`, `--offline` (check),
`--keep-modified` (add), `--force` (update).

## pack — the game packer

An engine-agnostic CLI that packages any game into a single drop-in **`.kbg`** file for the KnockBox
platform. An administrator copies that one file into the server's games directory and the server
installs it — no unzipping, no CLI on the host, no restart. It validates your `GAME.json` against the
rules the server enforces (see `KnockBox.Server/Games/GameCatalog.cs`) — plus a couple of stricter
checks — so you catch mistakes before deploying instead of finding your game silently skipped at
runtime.

A game is just **a folder of static files plus a manifest**. This tool separates the two halves of
shipping one:

- **Build** (engine-specific, optional): produce a folder of static files.
- **Package** (what this tool does, universal): validate the manifest, then emit `<id>.kbg` —
  built files + `GAME.json` + thumbnail, with each asset Brotli-compressed at maximum effort.

The format is specified in [`docs/KBG_FORMAT.md`](../../docs/KBG_FORMAT.md). It is a plain ZIP, so
`7-Zip`/`unzip -l` can inspect it, but the payloads are pre-compressed: the server copies those
Brotli streams straight into its HTTP serving cache instead of re-compressing them on every boot.

## Usage

```sh
npx knockbox pack --in <built-dir> --manifest <GAME.json> [options]

# from a checkout, without installing:
node tools/pack-game/knockbox.mjs pack --in <built-dir> --manifest <GAME.json> [options]
```

| Option | Meaning |
| --- | --- |
| `--in <dir>` | Folder of built static files to package. **Required.** |
| `--manifest <file>` | Path to `GAME.json`; copied into the package (plus the SDK stamp below). **Required.** |
| `--out <file\|dir>` | Where to write the `.kbg`. Default: this platform's `games/<id>.kbg`. A directory gets `<dir>/<id>.kbg`. |
| `--dir <dir>` | Write the uncompressed `<dir>/<id>/` folder layout **instead of** a `.kbg`. |
| `--build "<cmd>"` | Optional command to run before assembling (in `--cwd`). |
| `--cwd <dir>` | Working directory for `--build`. Default: current directory. |
| `--thumbnail <file>` | Thumbnail source override; output name stays `manifest.thumbnail`. |
| `--version <s>` | Stamp a game version into the package; the server logs it on install. |
| `--quality <0-11>` | Brotli quality. Default `11` (max). Lower is dramatically faster to pack. |
| `--no-clean` | With `--dir` only: don't wipe the target `<id>/` folder first. |
| `--no-sdk-stamp` | Don't record the installed addon versions (from `knockbox.json`) in `GAME.json`. |
| `-h`, `--help` | Show help. |

With no `--out`, the packer writes `games/<id>.kbg` inside the **enclosing KnockBox checkout**, where
it installs and appears within ~1–2 seconds — no server restart. Pass `--out dist/` (or any path) to
build somewhere that doesn't touch the platform.

Run from outside a checkout (the normal case once installed from npm), there is no such directory to
default to, so `--out` becomes **required** — or set `KNOCKBOX_GAMES_DIR` to your server's games
directory and it is used instead. This used to be resolved blindly relative to the tool's own
location, which quietly created a `games/` folder inside the developer's own project.

### The SDK stamp

If your project has a `knockbox.json` (written by `knockbox addon`), the packer records the installed
addon versions in the packaged manifest:

```json
{ "id": "my-game", "…": "…", "sdk": { "phaser": "1.0.0" } }
```

Your `GAME.json` on disk is not modified — the stamp is generated into the package. The server never
validates it; the admin portal uses it to flag a game still built against an old addon. Most games
have no `knockbox.json` and are packed unchanged. `--no-sdk-stamp` opts out.

**Packing is slow on purpose.** Brotli at quality 11 takes ~50 seconds for a 38 MB WASM export. That
cost is paid once here instead of on every server cold start, which is the whole point of the
format. While iterating, use `--quality 4` (a second or two) and save the default for release builds.

`--dir` exists for inspecting exactly what got packaged, and because the server still supports plain
game folders. It is not the recommended way to ship.

## Examples by engine

**Vite / Phaser** — build, then package `dist/`:

```sh
node tools/pack-game/pack-game.mjs --build "npm run build" --in dist --manifest export/GAME.json
```

Set `base: "./"` in `vite.config.ts` so asset paths are relative and resolve under `/games/<id>/`.

**Godot / Unity** — export from the editor first, then package the export folder (no `--build`). For
threaded exports that need `SharedArrayBuffer`, set `"crossOriginIsolated": true` in `GAME.json`.

```sh
node tools/pack-game/pack-game.mjs --in build/web --manifest GAME.json --version 1.4.0
```

**Hand-written HTML5** — the files are already the build:

```sh
node tools/pack-game/pack-game.mjs --in . --manifest GAME.json
```

## The manifest

`GAME.json` matches the platform's `GameManifest` contract:

```json
{
  "id": "your-game-id",
  "name": "Your Game",
  "entry": "index.html",
  "thumbnail": "thumb.svg",
  "maxPlayers": 8,
  "crossOriginIsolated": false,
  "themeColor": "#1b1033",
  "themeTextColor": "#f4f1ff"
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | yes | Unique catalog key and URL segment. The installed folder is named this; must be a single path segment (no slashes or `..`). |
| `name` | yes | Display name in the lobby browser. |
| `entry` | yes | Entry HTML, relative to the built folder; must exist and stay inside it. |
| `thumbnail` | no | Lobby thumbnail, relative to the manifest's folder. |
| `maxPlayers` | yes | Integer > 0. |
| `crossOriginIsolated` | no | `true` only for threaded Godot/Unity web exports. |
| `themeColor` | no | CSS color the shell tints the in-game header with. Shell-validated; invalid values are ignored. |
| `themeTextColor` | no | CSS color for text on `themeColor`. Same validation. |

The manifest and thumbnail may live outside the build (e.g. an `export/` folder), since `--manifest`
and the declared `thumbnail` are resolved relative to the manifest's location — the build output
stays clean. An explicit `--manifest` also wins over a stale `GAME.json` left inside the build.

The archive filename is **not** significant: `--out my-build.kbg` still installs as `<id>/`. Only
`id` decides that.

## Validation

The packer covers `GameCatalog.Discover()`'s rules and fails fast with a clear message on: empty
`id`/`name`/`entry`, an `id` that isn't a safe single segment, a non-positive or non-integer
`maxPlayers`, a non-boolean `crossOriginIsolated`, an `entry` that is missing or escapes the built
folder, and a thumbnail that is missing or escapes the game folder. It is intentionally **stricter**
than the server in two places — the server leaves `name` and `maxPlayers` to deserialization, while
the packer rejects an empty `name` and a non-positive/non-integer `maxPlayers` so authors fail fast.

Packaging additionally enforces the format's path rules (no traversal, no absolute paths, no Windows
reserved device names or alternate-data-stream `:`, no names differing only by case) and re-reads the
finished `.kbg` to verify every CRC, size and SHA-256 before reporting success — a corrupt write
never ships.

> Keep this in sync with the server: if the `GameManifest` contract or `GameCatalog` rules change,
> update the validation in `pack-game.mjs` too. The compress-or-store decision in `kbg.mjs`
> likewise mirrors `GameAssetPrecompressor.ShouldCompress`.

## Releasing a new addon version (maintainers)

For anyone shipping the addons and this CLI — not needed to *use* either.

### One version number

`clients/addons.manifest.json` `sdkVersion` is the only real version number in the repo. It covers
all three addons **and** this CLI, and releasing means editing that one line. Everything else holds
the sentinel `0.0.0-dev` and is filled in by the build:

| Declaration | Filled in by |
| --- | --- |
| Godot `plugin.cfg` | `tools/build-addons.mjs`, into the release archive |
| `tools/pack-game/package.json` | CI, just before `npm publish` |
| `KnockBoxSdk` (the server) | reads the manifest, embedded into the assembly at build time |
| `clients/phaser/`, `web/` `package.json` | nothing — both are private and unpublished |

`AddonManifestTests` asserts each in-repo declaration is **still the sentinel**, rather than that it
equals `sdkVersion`. That is the point: checking equality still leaves several real numbers that have
to be edited together, which is the arrangement that had already drifted three ways. A stale version
now cannot exist, because nothing committed claims one.

### The release

```sh
# 1. Make the addon change.
$EDITOR clients/godot/addons/knockbox/kb_net.gd      # or clients/phaser/..., or web/...

# 2. Bump the version. One file, one line.
$EDITOR clients/addons.manifest.json                 # "sdkVersion": "1.1.0"

# 3. Verify.
dotnet test --solution KnockBox-Games.slnx             # version consistency + client parity
cd tools/pack-game && npm test

# 4. Optional: build the archives and inspect them (CI does this on every PR anyway).
node tools/build-addons.mjs

# 5. Merge to main, then tag that commit.
git tag addons-v1.1.0 && git push origin addons-v1.1.0
```

**Merge before tagging.** The `addons` CI job checks out `main`, not the tagged commit, so the bump
has to be on `main` or its tag-vs-manifest guard reads a stale value and fails the release.

### Two tag namespaces

| Tag | Releases | Version it names |
| --- | --- | --- |
| `v1.2.3` | the container image | `KnockBox.Server.csproj` `<Version>` |
| `addons-v1.2.3` | addon archives, `ADDONS.json`, this npm package | `addons.manifest.json` `sdkVersion` |

They are separate because the two version numbers are independent, and a tag has to name exactly one
of them. Sharing `v*` broke both directions: a server-only release failed the addon job's guard for no
real reason, and an addon-only release published an image *labelled* with a version its own assembly
did not report.

### What CI does on an `addons-v*` tag

1. Verifies the tag matches `sdkVersion`.
2. Builds one archive per addon, stamping the real version in, and generates `ADDONS.json` — whose
   `sha256` values are the trust root every install verifies against, which is why the index is
   generated and never hand-edited.
3. Uploads the archives to the GitHub release (creating it if it does not exist yet).
4. Commits the regenerated index to `main`. Previous versions are retained in the index's history, so
   `--version <old>` stays installable.
5. Publishes this package to npm.

### How it reaches users

| Path | When they get it |
| --- | --- |
| `npx knockbox addon update` | immediately — npx resolves the latest CLI, and the index is live on `main` |
| Godot **Project → Tools → check for addon updates** | immediately — reads the same index |
| Godot **AssetLib** | after you resubmit there (manual, with a review round) |

The index is the live channel; AssetLib is only the first-install channel for Godot. An existing
Godot project gets the update from the in-editor check without waiting on a resubmission.

### npm publishing

Publishing uses **trusted publishing (OIDC)** — there is no npm token in this repo. Two consequences:

- The trusted publisher matches on the workflow **filename**, so renaming `.github/workflows/ci.yml`
  silently breaks publishing until the publisher config on npmjs.com is updated to match.
- It needs npm ≥ 11.5.1 and Node ≥ 22.14.0. The job upgrades npm before publishing, because a
  too-old npm fails with an *auth* error rather than a version error — which reads as a broken
  publisher and sends you debugging the wrong thing.

Provenance attestations are generated automatically; no `--provenance` flag is needed.

## Tests

```sh
cd tools/pack-game && npm install && npm test
```
