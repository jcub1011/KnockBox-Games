// Tests for the addon installer. No network: every fetch is injected.
//
// The behaviours worth the most attention here are the two OPPOSITE defaults — `add` overwrites a
// locally-modified file (the developer asked to make the addon pristine) while `update` refuses to
// (a version change discarding an edit is a surprise). That asymmetry is deliberate and is exactly
// the kind of thing a later refactor "tidies" into consistency, so both directions are pinned.

import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";

import {
  AddonError, MANIFEST_NAME, add, buildAddonArchive, buildRecord, check, downloadUrl,
  contentHash, incompatibility, inspectInstall, list, normalizeText, parseIndex, readArchive, remove,
  selectRelease, sha256,
  update, validateEntry,
} from "./addon.mjs";
import { writeStoredZip } from "./kbg.mjs";

// ── Fixtures ───────────────────────────────────────────────────────────────────

const INSTALL_TO = "addons/knockbox";

/** Build a stored-ZIP archive from { archivePath: contents }. */
function archiveOf(files) {
  const entries = Object.keys(files).sort().map((name) => ({ name, data: Buffer.from(files[name], "utf8") }));
  return writeStoredZip(entries);
}

/**
 * A published release of a fake addon: the archive plus an index entry whose sha256 actually matches
 * it. Written as one helper so a test can never accidentally assert against a hash it also chose.
 */
function publish({ id = "fake", version = "1.0.0", files, minAppVersion, maxAppVersion, extraVersions } = {}) {
  const contents = files ?? {
    [`${INSTALL_TO}/kb_core.gd`]: "# core v1\n",
    [`${INSTALL_TO}/plugin.cfg`]: `[plugin]\nversion="${version}"\n`,
    [`${INSTALL_TO}/LICENSE`]: "MIT\n",
  };
  const buffer = archiveOf(contents);
  const entry = {
    version,
    ...(minAppVersion ? { minAppVersion } : {}),
    ...(maxAppVersion ? { maxAppVersion } : {}),
    source: {
      type: "github-release",
      repo: "jcub1011/KnockBox-Games",
      tag: `v${version}`,
      asset: `knockbox-${id}-${version}.zip`,
      sha256: sha256(buffer),
    },
    ...(extraVersions ? { versions: extraVersions } : {}),
  };
  const index = { schemaVersion: "1.0", sdkVersion: version, addons: { [id]: entry } };
  return { id, version, buffer, entry, index, contents, opts: { index, fetchArchive: async () => buffer } };
}

let dir;
beforeEach(() => { dir = mkdtempSync(join(tmpdir(), "kb-addon-")); });
afterEach(() => { rmSync(dir, { recursive: true, force: true }); });

const read = (rel) => readFileSync(join(dir, rel), "utf8");
const manifest = () => JSON.parse(read(MANIFEST_NAME));

// ── Install ────────────────────────────────────────────────────────────────────

describe("add", () => {
  it("installs the files and records them", async () => {
    const p = publish();
    const result = await add(p.id, { dir, ...p.opts });

    expect(result.version).toBe("1.0.0");
    expect(result.previousVersion).toBeNull();
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# core v1\n");
    expect(read(`${INSTALL_TO}/LICENSE`)).toBe("MIT\n");

    const record = manifest().addons.fake;
    expect(record.version).toBe("1.0.0");
    expect(Object.keys(record.files).sort()).toEqual([
      `${INSTALL_TO}/LICENSE`, `${INSTALL_TO}/kb_core.gd`, `${INSTALL_TO}/plugin.cfg`,
    ]);
    expect(record.files[`${INSTALL_TO}/kb_core.gd`]).toBe(sha256(Buffer.from("# core v1\n")));
  });

  it("keeps the archive's own knockbox.json out of the install", async () => {
    // The archive ships a manifest so a hand-unzip is a complete install, but the project's manifest
    // is the MERGE of every installed addon — copying the archive's over it would erase the others.
    const p = publish({ files: {
      [`${INSTALL_TO}/kb_core.gd`]: "# core\n",
      [MANIFEST_NAME]: '{"addons":{"somethingElse":{}}}\n',
    } });
    await add(p.id, { dir, ...p.opts });
    expect(Object.keys(manifest().addons)).toEqual(["fake"]);
  });

  it("merges into an existing manifest instead of replacing it", async () => {
    const a = publish({ id: "one" });
    await add("one", { dir, ...a.opts });
    const b = publish({ id: "two", files: { [`${INSTALL_TO}/other.js`]: "// two\n" } });
    await add("two", { dir, ...b.opts });
    expect(Object.keys(manifest().addons).sort()).toEqual(["one", "two"]);
  });

  it("refuses an archive whose hash does not match the index", async () => {
    const p = publish();
    const tampered = archiveOf({ [`${INSTALL_TO}/kb_core.gd`]: "# EVIL\n" });
    await expect(add(p.id, { dir, index: p.index, fetchArchive: async () => tampered }))
      .rejects.toThrow(/sha256 mismatch/i);
    // Nothing written: the check happens before any file is touched.
    expect(existsSync(join(dir, INSTALL_TO))).toBe(false);
    expect(existsSync(join(dir, MANIFEST_NAME))).toBe(false);
  });

  it("refuses an archive entry that escapes the project directory", async () => {
    const evil = archiveOf({ "../../evil.gd": "# pwned\n" });
    const index = {
      schemaVersion: "1.0",
      addons: { fake: { version: "1.0.0", source: {
        type: "github-release", repo: "a/b", tag: "v1", asset: "x.zip", sha256: sha256(evil),
      } } },
    };
    await expect(add("fake", { dir, index, fetchArchive: async () => evil }))
      .rejects.toThrow(/must not contain "\." or "\.\." segments/);
    expect(existsSync(join(dirname(dirname(dir)), "evil.gd"))).toBe(false);
  });

  it("refuses an unknown addon id, naming what is on offer", async () => {
    const p = publish({ id: "godot" });
    await expect(add("gdscript", { dir, ...p.opts })).rejects.toThrow(/unknown addon 'gdscript'.*godot/s);
  });
});

// ── Repair (the reason `add` is idempotent) ────────────────────────────────────

describe("add as the repair path", () => {
  it("restores a locally modified file and names it", async () => {
    const p = publish();
    await add(p.id, { dir, ...p.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# I broke this\n");

    const result = await add(p.id, { dir, ...p.opts });
    expect(result.restored).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# core v1\n");
  });

  it("re-fetches a deleted file", async () => {
    const p = publish();
    await add(p.id, { dir, ...p.opts });
    rmSync(join(dir, `${INSTALL_TO}/kb_core.gd`));

    const result = await add(p.id, { dir, ...p.opts });
    expect(result.written).toContain(`${INSTALL_TO}/kb_core.gd`);
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# core v1\n");
  });

  it("reports nothing to do when the install is already pristine", async () => {
    const p = publish();
    await add(p.id, { dir, ...p.opts });
    const result = await add(p.id, { dir, ...p.opts });
    expect(result.restored).toEqual([]);
    expect(result.written).toEqual([]);
  });

  it("leaves a local edit alone with --keep-modified", async () => {
    const p = publish();
    await add(p.id, { dir, ...p.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# my patch\n");

    const result = await add(p.id, { dir, ...p.opts, keepModified: true });
    expect(result.skipped).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# my patch\n");
  });
});

// ── Pruning ────────────────────────────────────────────────────────────────────

describe("pruning is scoped to the recorded file list", () => {
  it("removes a file the previous version shipped and this one does not", async () => {
    const v1 = publish({ version: "1.0.0", files: {
      [`${INSTALL_TO}/kb_core.gd`]: "# core\n",
      [`${INSTALL_TO}/legacy.gd`]: "# going away\n",
    } });
    await add("fake", { dir, ...v1.opts });
    expect(existsSync(join(dir, `${INSTALL_TO}/legacy.gd`))).toBe(true);

    const v2 = publish({ version: "2.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# core v2\n" } });
    const result = await add("fake", { dir, ...v2.opts });

    expect(result.pruned).toEqual([`${INSTALL_TO}/legacy.gd`]);
    expect(existsSync(join(dir, `${INSTALL_TO}/legacy.gd`))).toBe(false);
    expect(Object.keys(manifest().addons.fake.files)).toEqual([`${INSTALL_TO}/kb_core.gd`]);
  });

  it("never deletes a file it did not install", async () => {
    // The whole reason pruning is scoped rather than "clear the directory": a developer's own script
    // living beside the addon is not ours to remove.
    const v1 = publish({ version: "1.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# core\n" } });
    await add("fake", { dir, ...v1.opts });

    mkdirSync(join(dir, INSTALL_TO), { recursive: true });
    writeFileSync(join(dir, `${INSTALL_TO}/my_helper.gd`), "# mine\n");

    const v2 = publish({ version: "2.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# core v2\n" } });
    const result = await add("fake", { dir, ...v2.opts });

    expect(result.pruned).toEqual([]);
    expect(read(`${INSTALL_TO}/my_helper.gd`)).toBe("# mine\n");
  });
});

// ── Update: the opposite default ───────────────────────────────────────────────

describe("update", () => {
  it("moves to the newer version", async () => {
    const v1 = publish({ version: "1.0.0" });
    await add("fake", { dir, ...v1.opts });

    const v2 = publish({ version: "2.0.0" });
    const result = await update("fake", { dir, ...v2.opts });
    expect(result.previousVersion).toBe("1.0.0");
    expect(result.version).toBe("2.0.0");
    expect(manifest().addons.fake.version).toBe("2.0.0");
  });

  it("reports up-to-date without rewriting when the version is unchanged", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    const result = await update("fake", { dir, ...p.opts });
    expect(result.upToDate).toBe(true);
  });

  it("REFUSES to discard a local edit, unlike add", async () => {
    const v1 = publish({ version: "1.0.0" });
    await add("fake", { dir, ...v1.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# my patch\n");

    const v2 = publish({ version: "2.0.0" });
    await expect(update("fake", { dir, ...v2.opts })).rejects.toThrow(/refusing to update.*kb_core\.gd/s);
    // Untouched, and still recorded at the old version.
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# my patch\n");
    expect(manifest().addons.fake.version).toBe("1.0.0");
  });

  it("proceeds with --force", async () => {
    const v1 = publish({ version: "1.0.0" });
    await add("fake", { dir, ...v1.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# my patch\n");

    const v2 = publish({ version: "2.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# core v2\n" } });
    const result = await update("fake", { dir, ...v2.opts, force: true });
    expect(result.version).toBe("2.0.0");
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# core v2\n");
  });

  it("calls a file that changed between versions 'updated', not a discarded local edit", async () => {
    // The distinction is the difference between an accurate report and one that cries wolf: during an
    // update most files legitimately differ, and labelling those "local changes discarded" would train
    // the reader to ignore the line that actually matters.
    const v1 = publish({ version: "1.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# v1\n" } });
    await add("fake", { dir, ...v1.opts });

    const v2 = publish({ version: "2.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# v2\n" } });
    const result = await update("fake", { dir, ...v2.opts });

    expect(result.updated).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(result.restored).toEqual([]);
  });

  it("still calls a genuine local edit discarded, under --force", async () => {
    const v1 = publish({ version: "1.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# v1\n" } });
    await add("fake", { dir, ...v1.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# mine\n");

    const v2 = publish({ version: "2.0.0", files: { [`${INSTALL_TO}/kb_core.gd`]: "# v2\n" } });
    const result = await update("fake", { dir, ...v2.opts, force: true });

    expect(result.restored).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(result.updated).toEqual([]);
  });

  it("leaves an already-correct file untouched rather than rewriting it", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    const before = statSync(join(dir, `${INSTALL_TO}/kb_core.gd`)).mtimeMs;
    const again = await add("fake", { dir, ...p.opts });
    expect(again.written).toEqual([]);
    expect(again.updated).toEqual([]);
    expect(statSync(join(dir, `${INSTALL_TO}/kb_core.gd`)).mtimeMs).toBe(before);
  });

  it("--force --keep-modified updates everything except the files you edited", async () => {
    // The fork-maintainer case: get the new version, but keep my one patched file. The record still
    // stores the PUBLISHED hash, so `check` keeps flagging it — which is the truth about that file.
    const v1 = publish({ version: "1.0.0", files: {
      [`${INSTALL_TO}/kb_core.gd`]: "# v1\n", [`${INSTALL_TO}/other.gd`]: "# other v1\n",
    } });
    await add("fake", { dir, ...v1.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# my fork\n");

    const v2 = publish({ version: "2.0.0", files: {
      [`${INSTALL_TO}/kb_core.gd`]: "# v2\n", [`${INSTALL_TO}/other.gd`]: "# other v2\n",
    } });
    const result = await update("fake", { dir, ...v2.opts, force: true, keepModified: true });

    expect(result.skipped).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(result.updated).toEqual([`${INSTALL_TO}/other.gd`]);
    expect(read(`${INSTALL_TO}/kb_core.gd`)).toBe("# my fork\n");
    expect(read(`${INSTALL_TO}/other.gd`)).toBe("# other v2\n");
    expect(manifest().addons.fake.version).toBe("2.0.0");

    const report = await check({ dir, index: v2.index });
    expect(report.addons[0].modified).toEqual([`${INSTALL_TO}/kb_core.gd`]);
  });

  it("refuses to update something that was never installed", async () => {
    const p = publish();
    await expect(update("fake", { dir, ...p.opts })).rejects.toThrow(/not installed/);
  });
});

// ── Check ──────────────────────────────────────────────────────────────────────

describe("check", () => {
  it("reports a clean install", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    const report = await check({ dir, index: p.index });
    expect(report.addons[0]).toMatchObject({ id: "fake", version: "1.0.0", clean: true, updateAvailable: false });
  });

  it("reports MODIFIED and MISSING", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# forked\n");
    rmSync(join(dir, `${INSTALL_TO}/plugin.cfg`));

    const report = await check({ dir, index: p.index });
    expect(report.addons[0].modified).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(report.addons[0].missing).toEqual([`${INSTALL_TO}/plugin.cfg`]);
    expect(report.addons[0].clean).toBe(false);
  });

  it("reports an available update", async () => {
    const v1 = publish({ version: "1.0.0" });
    await add("fake", { dir, ...v1.opts });
    const report = await check({ dir, index: publish({ version: "2.0.0" }).index });
    expect(report.addons[0]).toMatchObject({ version: "1.0.0", latest: "2.0.0", updateAvailable: true });
  });

  it("still verifies files when the index is unreachable", async () => {
    // The half a developer needs when something is already wrong must not depend on the network.
    const p = publish();
    await add("fake", { dir, ...p.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# forked\n");

    const report = await check({ dir, fetchIndex: async () => { throw new AddonError("offline"); } });
    expect(report.indexError).toMatch(/offline/);
    expect(report.addons[0].modified).toEqual([`${INSTALL_TO}/kb_core.gd`]);
  });

  it("is empty, not an error, in a project with no addons", async () => {
    const report = await check({ dir, offline: true });
    expect(report.empty).toBe(true);
  });
});

// ── Remove / list ──────────────────────────────────────────────────────────────

describe("remove", () => {
  it("removes exactly the recorded files and the record", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    const result = remove("fake", { dir });
    expect(result.removed.length).toBe(3);
    expect(existsSync(join(dir, INSTALL_TO))).toBe(false);
    expect(manifest().addons).toEqual({});
  });

  it("keeps a directory that still holds a file it did not install", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/my_helper.gd`), "# mine\n");
    remove("fake", { dir });
    expect(read(`${INSTALL_TO}/my_helper.gd`)).toBe("# mine\n");
  });
});

describe("list", () => {
  it("reads the manifest without touching the files", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    expect(list({ dir }).addons).toEqual([{ id: "fake", version: "1.0.0", fileCount: 3 }]);
  });
});

// ── Index parsing, entry validation, URL derivation ───────────────────────────

describe("the index", () => {
  it("refuses a newer schema major rather than half-reading it", () => {
    expect(() => parseIndex('{"schemaVersion":"2.0","addons":{}}')).toThrow(/newer than this tool understands/);
  });

  it("accepts a newer MINOR of the same major", () => {
    expect(parseIndex('{"schemaVersion":"1.7","addons":{}}').schemaVersion).toBe("1.7");
  });

  it.each([
    ["a missing sha256", { type: "github-release", repo: "a/b", tag: "v1", asset: "x.zip" }, /sha256 is required/],
    ["a short sha256", { type: "github-release", repo: "a/b", tag: "v1", asset: "x.zip", sha256: "abc" }, /sha256 is required/],
    ["a non-zip asset", { type: "github-release", repo: "a/b", tag: "v1", asset: "GAME.json", sha256: "a".repeat(64) }, /must be a \.zip/],
    ["a traversal in the repo", { type: "github-release", repo: "../../x", tag: "v1", asset: "x.zip", sha256: "a".repeat(64) }, /invalid source\.repo/],
    ["a traversal in the tag", { type: "github-release", repo: "a/b", tag: "../x", asset: "x.zip", sha256: "a".repeat(64) }, /invalid source\.tag/],
    ["an unsupported type", { type: "local-path", path: "/etc", sha256: "a".repeat(64) }, /unsupported source type/],
  ])("rejects %s", (_label, source, pattern) => {
    expect(() => validateEntry("fake", { version: "1.0.0", source })).toThrow(pattern);
  });

  it("derives the download URL rather than trusting one", () => {
    const entry = { version: "1.0.0", source: { repo: "jcub1011/KnockBox-Games", tag: "v1.0.0", asset: "knockbox-godot-1.0.0.zip" } };
    expect(downloadUrl(entry, "https://github.com"))
      .toBe("https://github.com/jcub1011/KnockBox-Games/releases/download/v1.0.0/knockbox-godot-1.0.0.zip");
  });

  it("serves a pinned older version out of the index, never a guessed URL", () => {
    const entry = {
      version: "2.0.0",
      source: { type: "github-release", repo: "a/b", tag: "v2.0.0", asset: "x-2.0.0.zip", sha256: "b".repeat(64) },
      versions: { "1.0.0": { source: { type: "github-release", repo: "a/b", tag: "v1.0.0", asset: "x-1.0.0.zip", sha256: "a".repeat(64) } } },
    };
    expect(selectRelease("fake", entry, "1.0.0").source.asset).toBe("x-1.0.0.zip");
    expect(selectRelease("fake", entry, undefined).version).toBe("2.0.0");
    // An unpublished pin has no hash, so it is refused rather than fetched unverified.
    expect(() => selectRelease("fake", entry, "1.5.0")).toThrow(/not published in this index.*2\.0\.0, 1\.0\.0/s);
  });

  it("inherits app-version bounds from the entry when a release does not restate them", () => {
    const entry = {
      version: "2.0.0", minAppVersion: "1.0.0",
      source: { type: "github-release", repo: "a/b", tag: "v2", asset: "x.zip", sha256: "b".repeat(64) },
      versions: { "1.0.0": { source: { type: "github-release", repo: "a/b", tag: "v1", asset: "y.zip", sha256: "a".repeat(64) } } },
    };
    expect(selectRelease("fake", entry, "1.0.0").minAppVersion).toBe("1.0.0");
  });
});

describe("archive validation", () => {
  it("rejects a deflated entry — every entry must be stored", () => {
    // Hand-built central directory claiming method 8. readStoredZip owns this rule; asserting it here
    // records that the addon path really does go through it.
    const stored = archiveOf({ "a.txt": "hi" });
    const tampered = Buffer.from(stored);
    const eocd = tampered.length - 22;
    const cd = tampered.readUInt32LE(eocd + 16);
    tampered.writeUInt16LE(8, cd + 10);
    expect(() => readArchive(tampered)).toThrow(/must be stored/);
  });

  it("rejects an empty archive", () => {
    expect(() => readArchive(archiveOf({}))).toThrow(/no files/);
  });
});

// ── App-version compatibility ─────────────────────────────────────────────────

describe("incompatibility", () => {
  it("is null with no bounds or no app version", () => {
    expect(incompatibility({ version: "1.0.0" }, "1.0.0")).toBeNull();
    expect(incompatibility({ version: "1.0.0", minAppVersion: "9.0.0" }, undefined)).toBeNull();
  });

  it("reports a server below the floor, and treats bounds as inclusive", () => {
    expect(incompatibility({ minAppVersion: "1.2.0" }, "1.1.0")).toMatch(/needs KnockBox >= 1\.2\.0/);
    expect(incompatibility({ minAppVersion: "1.2.0" }, "1.2.0")).toBeNull();
    expect(incompatibility({ maxAppVersion: "1.2.0" }, "1.2.0")).toBeNull();
    expect(incompatibility({ maxAppVersion: "1.2.0" }, "1.3.0")).toMatch(/supports KnockBox <= 1\.2\.0/);
  });

  it("treats an unreadable bound as incompatible, not as no bound", () => {
    expect(incompatibility({ minAppVersion: "not-a-version" }, "1.0.0")).toMatch(/unreadable minAppVersion/);
  });

  it("orders prereleases below their release, which string comparison inverts", () => {
    expect(incompatibility({ minAppVersion: "1.0.0" }, "1.0.0-rc.1")).toMatch(/needs KnockBox >= 1\.0\.0/);
    expect(incompatibility({ minAppVersion: "1.0.0-rc.1" }, "1.0.0")).toBeNull();
    // 0.10.0 > 0.9.0 numerically, though "0.10.0" < "0.9.0" as strings.
    expect(incompatibility({ minAppVersion: "0.9.0" }, "0.10.0")).toBeNull();
  });
});

// ── Tier 0: a hand-unzipped install must equal a CLI install ──────────────────

describe("the archive is a complete install on its own", () => {
  it("ships a knockbox.json byte-identical to the one the CLI writes", () => {
    // The property that keeps the no-tooling path first-class. A human unzipping the archive at
    // their project root gets the same recorded state as `knockbox addon add` — so `check` and the
    // repair path work afterwards either way. Nothing exercises this at runtime, hence the test.
    const files = new Map([
      [`${INSTALL_TO}/kb_core.gd`, Buffer.from("# core\n")],
      [`${INSTALL_TO}/LICENSE`, Buffer.from("MIT\n")],
    ]);
    const cliRecord = buildRecord({ version: "1.0.0", files, minAppVersion: "1.0.0" });

    const archive = readArchive(archiveOf({
      [`${INSTALL_TO}/kb_core.gd`]: "# core\n",
      [`${INSTALL_TO}/LICENSE`]: "MIT\n",
      [MANIFEST_NAME]: `${JSON.stringify({ addons: { fake: cliRecord } }, null, 2)}\n`,
    }));
    const shipped = JSON.parse(archive.get(MANIFEST_NAME).toString("utf8")).addons.fake;
    expect(shipped).toEqual(cliRecord);
  });

  it("builds a real addon archive from this repo's manifest", () => {
    // Guards the release job's input end to end: the manifest's file lists resolve, the layout is
    // project-relative, the LICENSE rides along, and the in-archive record matches buildRecord.
    const repoRoot = join(import.meta.dirname, "..", "..");
    const declared = JSON.parse(readFileSync(join(repoRoot, "clients", "addons.manifest.json"), "utf8"));

    for (const [id, addon] of Object.entries(declared.addons)) {
      const built = buildAddonArchive({
        repoRoot, id, addon,
        sdkVersion: declared.sdkVersion,
        minAppVersion: declared.minAppVersion,
        maxAppVersion: declared.maxAppVersion ?? undefined,
        license: readFileSync(join(repoRoot, "LICENSE"), "utf8"),
      });

      const files = readArchive(built.buffer, { expectedSha256: built.sha256 });
      expect(files.has(MANIFEST_NAME), `${id} archive must carry ${MANIFEST_NAME}`).toBe(true);
      expect(files.has(`${addon.installTo}/LICENSE`), `${id} archive must carry a LICENSE`).toBe(true);
      for (const name of files.keys()) {
        if (name === MANIFEST_NAME) continue;
        expect(name.startsWith(`${addon.installTo}/`), `${name} must be project-relative`).toBe(true);
      }
      const record = JSON.parse(files.get(MANIFEST_NAME).toString("utf8")).addons[id];
      expect(record.version).toBe(declared.sdkVersion);
      // contentHash, not sha256: the record is line-ending-insensitive on purpose, and this repo's
      // LICENSE is CRLF in a Windows working tree while its committed blob is LF.
      expect(record.files[`${addon.installTo}/LICENSE`]).toBe(contentHash(readFileSync(join(repoRoot, "LICENSE"))));
    }
  });

  it("installs that real archive into a project", async () => {
    const repoRoot = join(import.meta.dirname, "..", "..");
    const declared = JSON.parse(readFileSync(join(repoRoot, "clients", "addons.manifest.json"), "utf8"));
    const built = buildAddonArchive({
      repoRoot, id: "godot", addon: declared.addons.godot,
      sdkVersion: declared.sdkVersion, minAppVersion: declared.minAppVersion,
      license: readFileSync(join(repoRoot, "LICENSE"), "utf8"),
    });

    const index = {
      schemaVersion: "1.0",
      sdkVersion: declared.sdkVersion,
      addons: { godot: {
        version: declared.sdkVersion,
        minAppVersion: declared.minAppVersion,
        source: { type: "github-release", repo: "jcub1011/KnockBox-Games", tag: `v${declared.sdkVersion}`,
          asset: `knockbox-godot-${declared.sdkVersion}.zip`, sha256: built.sha256 },
      } },
    };

    await add("godot", { dir, index, fetchArchive: async () => built.buffer });
    expect(existsSync(join(dir, "addons/knockbox/kb_core.gd"))).toBe(true);
    expect(existsSync(join(dir, "addons/knockbox/plugin.cfg"))).toBe(true);
    expect(read("addons/knockbox/plugin.cfg")).toContain(`version="${declared.sdkVersion}"`);

    const report = await check({ dir, index });
    expect(report.addons[0]).toMatchObject({ id: "godot", clean: true, updateAvailable: false });
  });
});

// ── Line endings ──────────────────────────────────────────────────────────────

describe("line endings are not treated as edits", () => {
  // The failure this prevents is not hypothetical. A git checkout picks line endings from the
  // CONSUMER's platform and config (`core.autocrlf` on Windows, `* text=auto` in .gitattributes), so
  // one commit is legitimately CRLF in one working tree and LF in another. With raw byte hashes,
  // `check` reported every file MODIFIED on any machine other than the one that built the archive,
  // and `add` "repaired" it by flipping line endings back — forever.
  it("hashes content independent of CRLF vs LF", () => {
    expect(contentHash(Buffer.from("a\r\nb\r\n"))).toBe(contentHash(Buffer.from("a\nb\n")));
    // The raw hash still differs — that distinction is what the archive's own sha256 relies on.
    expect(sha256(Buffer.from("a\r\nb\r\n"))).not.toBe(sha256(Buffer.from("a\nb\n")));
  });

  it("never rewrites bytes that contain a NUL (binary)", () => {
    const binary = Buffer.from([0x41, 0x0d, 0x0a, 0x00, 0x42]);
    expect(normalizeText(binary)).toEqual(binary);
  });

  it("reports a CRLF checkout of an unmodified install as clean", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });

    // Simulate what git does to a text file on a Windows checkout.
    const target = join(dir, `${INSTALL_TO}/kb_core.gd`);
    writeFileSync(target, readFileSync(target, "utf8").replaceAll("\n", "\r\n"));

    const report = await check({ dir, index: p.index });
    expect(report.addons[0].modified).toEqual([]);
    expect(report.addons[0].clean).toBe(true);
  });

  it("does not rewrite a CRLF file on reinstall", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    const target = join(dir, `${INSTALL_TO}/kb_core.gd`);
    writeFileSync(target, readFileSync(target, "utf8").replaceAll("\n", "\r\n"));
    const crlf = readFileSync(target);

    const result = await add("fake", { dir, ...p.opts });
    expect(result.restored).toEqual([]);
    expect(result.updated).toEqual([]);
    expect(readFileSync(target)).toEqual(crlf);   // left exactly as the consumer's git wrote it
  });

  it("still catches a real edit in a CRLF working tree", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    const target = join(dir, `${INSTALL_TO}/kb_core.gd`);
    writeFileSync(target, "# my edit\r\nand more\r\n");

    const report = await check({ dir, index: p.index });
    expect(report.addons[0].modified).toEqual([`${INSTALL_TO}/kb_core.gd`]);
  });

  it("builds byte-identical archives from CRLF and LF sources", () => {
    // Reproducibility of the published sha256: it must not depend on which machine ran the release.
    const lf = archiveOf({ [`${INSTALL_TO}/a.gd`]: "x\ny\n" });
    const crlfFiles = readArchive(archiveOf({ [`${INSTALL_TO}/a.gd`]: "x\r\ny\r\n" }));
    const normalized = new Map([...crlfFiles].map(([k, v]) => [k, normalizeText(v)]));
    const rebuilt = writeStoredZip([...normalized].map(([name, data]) => ({ name, data })));
    expect(sha256(rebuilt)).toBe(sha256(lf));
  });
});

// ── inspectInstall ────────────────────────────────────────────────────────────

describe("inspectInstall", () => {
  it("classifies each recorded file", async () => {
    const p = publish();
    await add("fake", { dir, ...p.opts });
    writeFileSync(join(dir, `${INSTALL_TO}/kb_core.gd`), "# changed\n");
    rmSync(join(dir, `${INSTALL_TO}/LICENSE`));

    const state = inspectInstall(dir, manifest().addons.fake);
    expect(state.clean).toBe(false);
    expect(state.modified).toEqual([`${INSTALL_TO}/kb_core.gd`]);
    expect(state.missing).toEqual([`${INSTALL_TO}/LICENSE`]);
    expect(state.files.find((f) => f.path === `${INSTALL_TO}/plugin.cfg`).status).toBe("ok");
  });
});
