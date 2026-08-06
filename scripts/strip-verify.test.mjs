// Behavior coverage for the shared, catalog-driven strip-verify gate. Uses temp dirs with fake
// files so no real dotnet build is needed. Exercises the importable core (verifyPublishSet) for the
// pass/fail behaviors and the CLI entry point for exit-code + catalog-resolution behavior.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { verifyPublishSet } from "./strip-verify.mjs";

const scriptPath = fileURLToPath(new URL("./strip-verify.mjs", import.meta.url));

const ID = "com.example.fixture";
const VERSION = "1.2.3";

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "strip-verify-"));
}

function write(dir, name, content = "") {
  fs.writeFileSync(path.join(dir, name), content);
}

// The gate now runs on an ASSEMBLED package, so a manifest is part of the baseline every case
// starts from — only the case under test deviates from it.
function writeManifest(dir, overrides = {}) {
  const manifest = { id: ID, name: "Fixture", version: VERSION, entryDll: "Renamer.dll", ...overrides };
  for (const [key, value] of Object.entries(overrides)) {
    if (value === undefined) delete manifest[key];
  }
  write(dir, "extension.json", JSON.stringify(manifest, null, 2) + "\n");
}

function verify(dir, overrides = {}) {
  return verifyPublishSet({
    dir,
    entryName: "Renamer",
    expectedId: ID,
    expectedVersion: VERSION,
    requiredBundledDlls: [],
    denylist,
    ...overrides,
  });
}

// A denylisted host-provided assembly name used only by the tests.
const denylist = ["Cove.Core", "Microsoft.EntityFrameworkCore"];

test("passes: only the entry assembly, no denylisted dll, requiredBundledDlls empty", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir);
  const r = verify(dir);
  assert.equal(r.ok, true, r.failures.join("; "));
});

test("fails: entry <name>.dll absent with a MISSING message", () => {
  const dir = tmpDir();
  write(dir, "SomethingElse.dll");
  writeManifest(dir);
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISSING") && f.includes("Renamer.dll")));
});

test("fails: a denylisted host assembly present with a LEAK message", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  write(dir, "Cove.Core.dll");
  writeManifest(dir);
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("LEAK") && f.includes("Cove.Core.dll")));
});

test("requiredBundledDlls: fails when the required bundled dll is missing, passes when present", () => {
  const missing = tmpDir();
  write(missing, "Renamer.dll");
  writeManifest(missing);
  const rMissing = verify(missing, { requiredBundledDlls: ["System.IO.Hashing"] });
  assert.equal(rMissing.ok, false);
  assert.ok(rMissing.failures.some((f) => f.includes("MISSING") && f.includes("System.IO.Hashing.dll")));

  const present = tmpDir();
  write(present, "Renamer.dll");
  write(present, "System.IO.Hashing.dll");
  writeManifest(present);
  const rPresent = verify(present, { requiredBundledDlls: ["System.IO.Hashing"] });
  assert.equal(rPresent.ok, true, rPresent.failures.join("; "));
});

test("fails: a json with a Windows drive-root path marker leaks an absolute path", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir);
  // Construct the drive-root marker programmatically (drive letter + colon + a single backslash).
  const marker = "C:" + String.fromCharCode(92) + "Users" + String.fromCharCode(92) + "dev";
  write(dir, "config.json", `{ "path": "${marker.replace(/\\/g, "\\\\")}" }`);
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("LEAK") && f.includes("config.json")));
});

test("fails: a json with a unix home path prefix leaks an absolute path", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir);
  const unixHome = "/" + "home" + "/" + "dev/build";
  write(dir, "settings.json", `{ "out": "${unixHome}" }`);
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("LEAK") && f.includes("settings.json")));
});

// ── Packaged-manifest assertions ────────────────────────────────────────────────────────────────
// These replace the release-time version-parity gate: the packaged manifest is the artifact a host
// actually reads, so it — not a source declaration — is what has to name this release.

test("fails HARD: no extension.json in the package (never a skip)", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISSING") && f.includes("extension.json is absent")));
});

test("fails: packaged extension.json version disagrees with the expected release version", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir, { version: "9.9.9" });
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISMATCH") && f.includes("version") && f.includes("9.9.9")));
});

test("fails: packaged extension.json id disagrees with the catalog entry id", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir, { id: "com.example.other" });
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISMATCH") && f.includes("id") && f.includes("com.example.other")));
});

test("fails: packaged extension.json is not parseable JSON", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  write(dir, "extension.json", "{ not json");
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("INVALID") && f.includes("extension.json")));
});

test("fails: packaged extension.json declares no entryDll", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir, { entryDll: undefined });
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISSING") && f.includes("no entryDll")));
});

test("fails: a declared jsBundle is absent from the package; passes when it ships", () => {
  const absent = tmpDir();
  write(absent, "Renamer.dll");
  writeManifest(absent, { jsBundle: "index.mjs" });
  const rAbsent = verify(absent);
  assert.equal(rAbsent.ok, false);
  assert.ok(rAbsent.failures.some((f) => f.includes("MISSING") && f.includes("jsBundle") && f.includes("index.mjs")));

  const present = tmpDir();
  write(present, "Renamer.dll");
  write(present, "index.mjs");
  writeManifest(present, { jsBundle: "index.mjs" });
  const rPresent = verify(present);
  assert.equal(rPresent.ok, true, rPresent.failures.join("; "));
});

test("fails: a declared cssBundle is absent from the package", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  writeManifest(dir, { cssBundle: "style.css" });
  const r = verify(dir);
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISSING") && f.includes("cssBundle")));
});

test("fails: a bundle path that escapes the package, by absolute path or by a parent segment", () => {
  const escapes = [
    "/" + "etc/passwd",
    "C:" + String.fromCharCode(92) + "windows" + String.fromCharCode(92) + "evil.dll",
    ".." + "/outside.mjs",
    "nested/.." + "/.." + "/outside.mjs",
    String.fromCharCode(92) + "server" + String.fromCharCode(92) + "share.dll",
  ];

  for (const value of escapes) {
    const dir = tmpDir();
    write(dir, "Renamer.dll");
    writeManifest(dir, { jsBundle: value });
    const r = verify(dir);
    assert.equal(r.ok, false, "expected an escape failure for " + value);
    assert.ok(
      r.failures.some((f) => f.includes("ESCAPE") && f.includes("jsBundle")),
      "expected an ESCAPE failure for " + value + ", got: " + r.failures.join("; "),
    );
  }
});

test("passes: a bundle path nested in a real subdirectory of the package", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  fs.mkdirSync(path.join(dir, "ui"));
  write(dir, path.join("ui", "index.mjs"));
  writeManifest(dir, { jsBundle: "ui/index.mjs" });
  const r = verify(dir);
  assert.equal(r.ok, true, r.failures.join("; "));
});

test("CLI: exit 0 on pass, non-zero on failure, resolving the entry from the real catalog", () => {
  const catalog = JSON.parse(
    fs.readFileSync(fileURLToPath(new URL("../extensions/catalog.json", import.meta.url)), "utf8"),
  );
  const renamer = catalog.extensions.find((e) => e.name === "Renamer");
  const sourceManifest = JSON.parse(
    fs.readFileSync(fileURLToPath(new URL("../" + renamer.manifestPath, import.meta.url)), "utf8"),
  );

  const pass = tmpDir();
  write(pass, "Renamer.dll");
  // The catalog's Renamer entry declares requiredBundledDlls: ["System.IO.Hashing"], so a passing
  // package must carry it (this is the CLI's catalog-resolution path, unlike the unit tests above
  // that pass requiredBundledDlls explicitly).
  write(pass, "System.IO.Hashing.dll");
  // The CI step assembles the package by copying the real source manifest in and stamping the
  // release version onto it, so the fixture is built the same way rather than hand-written.
  write(pass, "index.mjs");
  write(pass, "extension.json", JSON.stringify({ ...sourceManifest, version: "1.2.3" }, null, 2) + "\n");

  const ok = spawnSync(process.execPath, [scriptPath, pass, "Renamer", "1.2.3"], { encoding: "utf8" });
  assert.equal(ok.status, 0, ok.stdout + ok.stderr);
  assert.ok(/PASS/i.test(ok.stdout));

  // A version the packaged manifest does not declare fails, so the argument is load-bearing.
  const wrongVersion = spawnSync(process.execPath, [scriptPath, pass, "Renamer", "9.9.9"], { encoding: "utf8" });
  assert.notEqual(wrongVersion.status, 0);
  assert.match(wrongVersion.stderr, /MISMATCH/);

  // A missing expectedVersion is a usage error, never a pass with the check silently skipped.
  const noVersion = spawnSync(process.execPath, [scriptPath, pass, "Renamer"], { encoding: "utf8" });
  assert.notEqual(noVersion.status, 0);
  assert.match(noVersion.stderr, /Usage:/);

  const empty = tmpDir();
  const bad = spawnSync(process.execPath, [scriptPath, empty, "Renamer", "1.2.3"], { encoding: "utf8" });
  assert.notEqual(bad.status, 0);
});
