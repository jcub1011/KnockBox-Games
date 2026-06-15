# pack-game — KnockBox game packer

An engine-agnostic CLI that packages any game into a drop-in `games/<id>/` folder for
the KnockBox platform. It validates your `GAME.json` against the rules the server
enforces (see `KnockBox.Server/Games/GameCatalog.cs`) — plus a couple of stricter checks
— so you catch mistakes before deploying instead of finding your game silently skipped at
runtime.

A game is just **a folder of static files plus a manifest**. This tool separates the
two halves of producing one:

- **Build** (engine-specific, optional): produce a folder of static files.
- **Assemble** (what this tool does, universal): validate the manifest and lay down
  `<out>/<id>/` = built files + `GAME.json` + thumbnail, with the folder named `<id>`
  (the platform serves assets at `/games/{id}/…` and requires the folder name to match).

## Usage

```sh
node tools/pack-game/pack-game.mjs --in <built-dir> --manifest <GAME.json> [options]
```

| Option | Meaning |
| --- | --- |
| `--in <dir>` | Folder of built static files to package. **Required.** |
| `--manifest <file>` | Path to `GAME.json`; copied verbatim into the output. **Required.** |
| `--out <dir>` | Target games directory. Default: this platform's `games/`. |
| `--build "<cmd>"` | Optional command to run before assembling (in `--cwd`). |
| `--cwd <dir>` | Working directory for `--build`. Default: current directory. |
| `--thumbnail <file>` | Thumbnail source override; output name stays `manifest.thumbnail`. |
| `--no-clean` | Don't wipe the target `<id>/` folder first (default: wipe). |
| `-h`, `--help` | Show help. |

With no `--out`, the packer writes straight into this platform's `games/` folder
(resolved relative to the tool's own location), and the catalog hot-reloads within
~1–2 seconds — no server restart. Use `--out dist-game` (or any path) for a local
inspect build that doesn't touch the platform.

## Examples by engine

**Vite / Phaser** — build, then package `dist/`:

```sh
node tools/pack-game/pack-game.mjs --build "npm run build" --in dist --manifest export/GAME.json
```

Set `base: "./"` in `vite.config.ts` so asset paths are relative and resolve under
`/games/<id>/`.

**Godot / Unity** — export from the editor first, then package the export folder (no
`--build`). For threaded exports that need `SharedArrayBuffer`, set
`"crossOriginIsolated": true` in `GAME.json`.

```sh
node tools/pack-game/pack-game.mjs --in build/web --manifest GAME.json
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
  "crossOriginIsolated": false
}
```

| Field | Required | Notes |
| --- | --- | --- |
| `id` | yes | Unique catalog key and URL segment. Output folder is named this; must be a single path segment (no slashes or `..`). |
| `name` | yes | Display name in the lobby browser. |
| `entry` | yes | Entry HTML, relative to the built folder; must exist and stay inside it. |
| `thumbnail` | no | Lobby thumbnail, relative to the manifest's folder. |
| `maxPlayers` | yes | Integer > 0. |
| `crossOriginIsolated` | no | `true` only for threaded Godot/Unity web exports. |

The manifest and thumbnail may live outside the build (e.g. an `export/` folder), since
`--manifest` and the declared `thumbnail` are resolved relative to the manifest's
location — the build output stays clean.

## Validation

The packer covers `GameCatalog.Discover()`'s rules and fails fast with a clear message on:
empty `id`/`name`/`entry`, an `id` that isn't a safe single segment, a non-positive or
non-integer `maxPlayers`, a non-boolean `crossOriginIsolated`, an `entry` that is
missing or escapes the built folder, and a thumbnail that is missing or escapes the game
folder. It is intentionally **stricter** than the server in two places — the server
leaves `name` and `maxPlayers` to deserialization, while the packer rejects an empty
`name` and a non-positive/non-integer `maxPlayers` so authors fail fast.

> Keep this in sync with the server: if the `GameManifest` contract or
> `GameCatalog` rules change, update the validation in `pack-game.mjs` too.

## Tests

```sh
cd tools/pack-game && npm install && npm test
```
