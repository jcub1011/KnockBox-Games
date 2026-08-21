# The `.kbg` file format (KnockBox Game package), version 1

A `.kbg` file is **one KnockBox game, packaged as a single file**. Copy it into a KnockBox server's
games directory and the server installs it: no CLI, no restart, no unzipping by hand.

This document is the normative specification. It is written so that an independent implementation
can produce and consume `.kbg` files correctly. The reference implementations live in
[`tools/pack-game/kbg.mjs`](../tools/pack-game/kbg.mjs) (writer) and
`KnockBox.Server/Games/GamePackageInstaller.cs` (reader).

- **Extension:** `.kbg`
- **Container:** ZIP (PKZIP / APPNOTE 6.3.x), all entries **stored** (method 0)
- **Version:** `formatVersion` 1
- **Media type:** `application/vnd.knockbox.game+zip` (unregistered)

## Design goals, and one explicit non-goal

1. **Unambiguous.** The extension plus the `KBG.json` header make "is this a KnockBox game?" a
   question you can answer without guessing at folder layouts.
2. **Backwards compatible, forever.** A `formatVersion: 1` file must remain readable by every future
   reader. New revisions add optional fields or bump `formatVersion`; they never redefine v1.
3. **Forwards compatible — explicitly NOT a goal.** A v1 reader that meets `formatVersion: 2` must
   fail loudly with an actionable message, not guess.
4. **Inspectable.** It is a real ZIP. 7-Zip, `unzip -l`, Explorer, and Finder all open it.
5. **Cheap to install.** Payloads are pre-compressed with Brotli, which lets a server populate its
   HTTP serving cache directly from the archive instead of re-compressing (see
   [Why Brotli](#why-brotli-and-why-stored-entries)).

## Layout

```
drawn-to-dress.kbg
├── KBG.json          stored, uncompressed JSON   ← the header
├── GAME.json         stored, uncompressed JSON   ← the standard KnockBox manifest
├── thumb.svg         stored, uncompressed        ← encoding "identity"
├── index.html.br     stored, Brotli stream       ← encoding "br", logical path index.html
├── index.wasm.br     stored, Brotli stream
└── assets/data.bin   stored, uncompressed        ← encoding "identity", nested path
```

There is **no wrapper directory**. The logical paths in the archive are exactly the paths inside the
installed game folder — the file list *is* the game folder. A reader that extracts every entry to its
logical path produces a directory that is byte-for-byte a valid `games/<id>/` folder.

The archive filename is **not** significant. `my-build-final.kbg` may contain `id: "tictactoe"`; the
installed folder is named from `id`, never from the filename.

## `KBG.json`

Required. UTF-8 JSON object, stored uncompressed.

```json
{
  "formatVersion": 1,
  "id": "drawn-to-dress",
  "name": "Drawn To Dress",
  "version": "1.4.0",
  "packedBy": "knockbox-pack 0.2.0",
  "packedAt": "2026-08-09T00:00:00Z",
  "files": [
    { "path": "GAME.json",  "encoding": "identity", "size": 148,      "sha256": "e3b0c442…" },
    { "path": "thumb.svg",  "encoding": "identity", "size": 612,      "sha256": "5f2c9a10…" },
    { "path": "index.html", "encoding": "br",       "size": 2841,     "sha256": "9b1de0a3…" },
    { "path": "index.wasm", "encoding": "br",       "size": 37700666, "sha256": "c14aa8f7…" }
  ]
}
```

| Field | Required | Type | Meaning |
|---|---|---|---|
| `formatVersion` | ✅ | integer | Format revision. `1` for this document. |
| `id` | ✅ | string | The game id. Must equal `GAME.json`'s `id`. Names the installed folder. |
| `name` | ✅ | string | Display name. Should equal `GAME.json`'s `name`; informational only. |
| `files` | ✅ | array | The authoritative content list. See below. |
| `version` | — | string | Build/version label for the *game*, e.g. `"1.4.0"`. Informational to the reader — nothing is rejected on it — but writers should set it, and `knockbox-pack` now defaults it to `GAME.json`'s own `version` so the header and the manifest cannot disagree. The **manifest's** copy is the one a marketplace compares against a catalog entry (see [MARKETPLACE.md](./MARKETPLACE.md)); this one is what shows up in server logs at install time. |
| `packedBy` | — | string | Tool name and version that produced the file. |
| `packedAt` | — | string | RFC 3339 / ISO 8601 UTC timestamp of packing. |

Unknown fields **must be ignored** by readers. This is what keeps v1 files readable as the format
grows.

### `files[]`

| Field | Required | Type | Meaning |
|---|---|---|---|
| `path` | ✅ | string | Logical path inside the game folder, `/`-separated. |
| `encoding` | ✅ | string | `"identity"` or `"br"`. |
| `size` | ✅ | integer | **Uncompressed** size in bytes. |
| `sha256` | — | string | Lowercase hex SHA-256 of the **uncompressed** bytes. |

The ZIP entry name is **derived** from these two fields, so there is no separate entry-name field:

| `encoding` | ZIP entry name | Entry contents |
|---|---|---|
| `identity` | `path` | the file's bytes verbatim |
| `br` | `path` + `".br"` | a raw Brotli stream of the file's bytes |

**The list is closed in both directions.** Every row in `files` must have a matching ZIP entry, and
every ZIP entry other than `KBG.json` must correspond to exactly one row. A reader must reject an
archive that violates either direction — this is what prevents entries being smuggled past the
header.

`files` must not contain two rows with the same `path`, nor two rows whose paths differ only by
letter case (they would collide on Windows and macOS).

Because entry names are derived, a game containing a literal file `foo.br` collides with the
compressed form of a file `foo`. **Writers must reject this** with a clear error rather than emit an
ambiguous archive. (In practice this never happens; games do not ship `.br` files.)

## Path rules

Every `path` in `files` must be a relative, `/`-separated path that stays inside the game folder.
Readers must reject an archive if any path:

- is empty, absolute (leading `/`), or carries a drive (`C:`) or UNC (`\\`) prefix;
- contains a `\`, or a `:` anywhere (`:` would create a Windows alternate data stream);
- has any empty segment (`a//b`), or a segment that is `.` or `..`;
- has a segment ending in `.` or a space (Windows silently trims these, so `a. ` and `a` collide);
- has a segment that is a Windows reserved device name — `CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`,
  `LPT1`–`LPT9` — with or without an extension;
- contains a character from the platform's invalid-filename set;
- resolves, after joining to the destination directory, to anything outside that directory.

The last check must be performed **in addition to** the syntactic ones, not instead of them.

`id` must additionally be a single safe path segment: it must satisfy all of the above and contain no
`/`, and must not be `.` or `..`.

## Reader requirements

A conforming reader, given a candidate `.kbg`, must in this order:

1. Open it as a ZIP. Failure (including a truncated file, whose central directory sits at the end)
   means "not a valid `.kbg`" — not a fatal error. Readers that poll a directory should retry later.
2. Read the `KBG.json` entry by name. Its absence means the file is not a `.kbg`.
3. Reject `formatVersion` greater than the highest version it knows, with a message telling the
   operator to upgrade. Reject a missing or non-integer `formatVersion`.
4. Validate `id`, then every `files` row's `path` against [Path rules](#path-rules), and confirm the
   list is closed in both directions.
5. Reject any entry that is encrypted, or whose external attributes mark it a symbolic link. Readers
   must **never** apply an entry's stored file mode or attributes to the extracted file.
6. Extract to a staging location, then move into place, so a reader is never observed reading a
   half-written game folder.
7. Verify each file's decompressed byte count against `size`, and its `sha256` when present.
   Byte counts must be measured **while copying**; the sizes declared in ZIP headers are
   attacker-controlled and must not be trusted for allocation or limit checks.
8. Confirm a root `GAME.json` exists, parses, and that its `id` equals `KBG.json`'s `id`. A mismatch
   is fatal: the installed folder is named from `id`, and KnockBox skips a game whose folder name
   does not match its manifest.

Readers should also impose resource ceilings — a total uncompressed-byte cap, an entry-count cap, and
a compression-ratio cap — and reject archives that exceed them. The KnockBox server's defaults are
512 MiB, 20 000 entries, and 200:1; they are configurable (see `docs/INFRASTRUCTURE.md` §9).

## Writer requirements

- Every ZIP entry is stored (method 0). Do not deflate: `br` payloads are already compressed, and
  `identity` payloads were deliberately judged not worth compressing.
- Write `KBG.json` as the **first** entry. Readers must not rely on this — ZIP central-directory
  order is only conventionally local-header order — but it lets `head -c` sniff a file's identity
  without a ZIP parser.
- Choose `br` only when it actually pays: skip files below a size floor (KnockBox uses 1024 bytes),
  skip already-compressed types (`.png`, `.jpg`, `.webp`, `.woff2`, `.br`, `.gz`, `.zip`, …), and
  fall back to `identity` when the Brotli output is not smaller than the input. Use maximum Brotli
  quality (11) for release builds; the cost is paid once, at packing time.
- Zip64 is **not** part of v1. Writers must fail with a clear error above 4 GiB total size, 4 GiB for
  any single entry, or 65 535 entries, rather than emit an archive a v1 reader cannot read.
- Output should be **deterministic**: fixed entry timestamps and a stable entry order, so packing
  identical input twice yields identical bytes and operators can diff or checksum builds.

## Why Brotli, and why stored entries

Measured on this repository's largest bundled game (a 38.4 MB Godot web export):

| | size | vs deflate |
|---|---|---|
| ZIP + deflate | 9.86 MB | — |
| **per-file Brotli‑11 (this format)** | **6.99 MB** | −29% |
| tar + xz/LZMA‑9e (solid) | 6.64 MB | −33% |

LZMA is 3.4% smaller on this game and *larger* on small ones, but it is absent from both the .NET
and Node standard libraries, so it would cost a third-party decoder in the server and a native addon
in the packer. Brotli is built into both, so `.kbg` needs zero dependencies on either side.

The deciding factor is not size but CPU. Brotli‑11 **encode** of that 38 MB `.wasm` takes ~49
seconds; **decode** takes ~0.1 seconds. A KnockBox server already builds maximum-effort Brotli
variants of every game asset to serve over HTTP, and pays that encode cost on every cold start.
Because a `.kbg` stores those Brotli streams as separate per-file entries, the server can copy them
straight into its serving cache at install time and skip the re-compression entirely. A solid
archive (tar + Brotli/xz) compresses marginally better but produces one opaque stream, so it cannot
be reused this way.

### Why the server still extracts (`games-unpacked/`)

The saving above is real but narrower than "the server never compresses", and it is worth being precise
about, because the obvious next question is why a package is extracted at all rather than served from the
archive. Six reasons, in rough order of how hard each is to work around:

1. **Only part of a package is Brotli.** The packer stores `.png/.jpg/.webp/.mp3/.mp4/.woff2` and friends
   as `identity` — usually the majority of a game's bytes. None of those has a compressed variant to
   serve, by design.
2. **Integrity is verified during extraction, once.** The declared size and SHA-256 of every file are
   checked against the *decompressed* bytes as they are written, with the byte cap enforced while copying
   because the declared sizes are attacker-controlled. Serving from the archive would move that work to
   request time or drop it.
3. **Every negotiation miss falls through to the raw file**, which is where the content type, `ETag`,
   `Content-Length` and range support come from: identity clients, files under `PrecompressMinBytes`,
   incompressible extensions, `Precompress=false`, and every thumbnail.
4. **Some files are read as files, not served.** `GAME.json`, a `serverAuthority` module and the
   `authorityWords` dictionaries are opened from disk (and cached by mtime and length) — and they are
   deliberately excluded from the compressed cache, so a variant-only store would hold nothing for them.
5. **Discovery is directory-shaped**: the catalog enumerates directories under each root and requires the
   folder name to equal the manifest `id`.
6. **`games/` is mounted read-only in production**, so nothing can be expanded in place. A separate
   writable root is what makes hot-dropping a `.kbg` possible there at all.

So `games-unpacked/` is not a staging area for re-compression: it is the filesystem the rest of the server
assumes. What the format removes is the ~49-second-per-asset maximum-effort Brotli pass, which is the
expensive part. (Gzip variants are the one thing still built locally, which is why they are off by
default — see `KnockBox:PrecompressGzip` in [`INFRASTRUCTURE.md`](./INFRASTRUCTURE.md) §9.)

## Relationship to plain game folders

A KnockBox server supports both. A plain `games/<id>/` folder and a `games/<id>.kbg` archive are
equally valid ways to install a game, and a folder takes precedence if both provide the same `id`.
The archive exists for *distribution*; the folder remains the simplest thing to author and edit.

### Server-only files

A package carries whatever the game folder carries, including files the server executes or reads but
**never serves**: a server-authoritative game's `serverAuthority` module and its `authorityWords`
dictionaries (see [`SERVER_AUTHORITY_DESIGN.md`](./SERVER_AUTHORITY_DESIGN.md) §11). The format gives
them no special treatment — they are ordinary `files[]` entries — because the secrecy guarantee lives
at the serving layer, which matches request paths against the installed manifest and 404s them
wherever the game was installed from. Two consequences worth knowing: a `.kbg` is *not* a
confidentiality boundary (anyone holding the archive can read its contents), and the installer skips
seeding pre-compressed variants of those files, so they never reach the served asset cache either.

See [`GAME_DEVELOPER_GUIDE.md`](./GAME_DEVELOPER_GUIDE.md) for authoring a game and packing it, and
[`HOSTING.md`](./HOSTING.md) for how a server stores and serves installed packages.
