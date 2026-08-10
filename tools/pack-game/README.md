# pack-game — KnockBox game packer

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
node tools/pack-game/pack-game.mjs --in <built-dir> --manifest <GAME.json> [options]
```

| Option | Meaning |
| --- | --- |
| `--in <dir>` | Folder of built static files to package. **Required.** |
| `--manifest <file>` | Path to `GAME.json`; copied verbatim into the package. **Required.** |
| `--out <file\|dir>` | Where to write the `.kbg`. Default: this platform's `games/<id>.kbg`. A directory gets `<dir>/<id>.kbg`. |
| `--dir <dir>` | Write the uncompressed `<dir>/<id>/` folder layout **instead of** a `.kbg`. |
| `--build "<cmd>"` | Optional command to run before assembling (in `--cwd`). |
| `--cwd <dir>` | Working directory for `--build`. Default: current directory. |
| `--thumbnail <file>` | Thumbnail source override; output name stays `manifest.thumbnail`. |
| `--version <s>` | Stamp a game version into the package; the server logs it on install. |
| `--quality <0-11>` | Brotli quality. Default `11` (max). Lower is dramatically faster to pack. |
| `--no-clean` | With `--dir` only: don't wipe the target `<id>/` folder first. |
| `-h`, `--help` | Show help. |

With no `--out`, the packer writes `games/<id>.kbg` inside this platform's checkout (resolved
relative to the tool's own location), where it installs and appears within ~1–2 seconds — no server
restart. Pass `--out dist/` (or any path) to build somewhere that doesn't touch the platform.

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

## Tests

```sh
cd tools/pack-game && npm install && npm test
```
