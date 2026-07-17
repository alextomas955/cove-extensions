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

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "strip-verify-"));
}

function write(dir, name, content = "") {
  fs.writeFileSync(path.join(dir, name), content);
}

// A denylisted host-provided assembly name used only by the tests.
const denylist = ["Cove.Core", "Microsoft.EntityFrameworkCore"];

test("passes: only the entry assembly, no denylisted dll, requiredBundledDlls empty", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  const r = verifyPublishSet({ dir, entryName: "Renamer", requiredBundledDlls: [], denylist });
  assert.equal(r.ok, true, r.failures.join("; "));
});

test("fails: entry <name>.dll absent with a MISSING message", () => {
  const dir = tmpDir();
  write(dir, "SomethingElse.dll");
  const r = verifyPublishSet({ dir, entryName: "Renamer", requiredBundledDlls: [], denylist });
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("MISSING") && f.includes("Renamer.dll")));
});

test("fails: a denylisted host assembly present with a LEAK message", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  write(dir, "Cove.Core.dll");
  const r = verifyPublishSet({ dir, entryName: "Renamer", requiredBundledDlls: [], denylist });
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("LEAK") && f.includes("Cove.Core.dll")));
});

test("requiredBundledDlls: fails when the required bundled dll is missing, passes when present", () => {
  const missing = tmpDir();
  write(missing, "Renamer.dll");
  const rMissing = verifyPublishSet({
    dir: missing,
    entryName: "Renamer",
    requiredBundledDlls: ["System.IO.Hashing"],
    denylist,
  });
  assert.equal(rMissing.ok, false);
  assert.ok(rMissing.failures.some((f) => f.includes("MISSING") && f.includes("System.IO.Hashing.dll")));

  const present = tmpDir();
  write(present, "Renamer.dll");
  write(present, "System.IO.Hashing.dll");
  const rPresent = verifyPublishSet({
    dir: present,
    entryName: "Renamer",
    requiredBundledDlls: ["System.IO.Hashing"],
    denylist,
  });
  assert.equal(rPresent.ok, true, rPresent.failures.join("; "));
});

test("fails: a json with a Windows drive-root path marker leaks an absolute path", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  // Construct the drive-root marker programmatically (drive letter + colon + a single backslash).
  const marker = "C:" + String.fromCharCode(92) + "Users" + String.fromCharCode(92) + "dev";
  write(dir, "config.json", `{ "path": "${marker.replace(/\\/g, "\\\\")}" }`);
  const r = verifyPublishSet({ dir, entryName: "Renamer", requiredBundledDlls: [], denylist });
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("LEAK") && f.includes("config.json")));
});

test("fails: a json with a unix home path prefix leaks an absolute path", () => {
  const dir = tmpDir();
  write(dir, "Renamer.dll");
  const unixHome = "/" + "home" + "/" + "dev/build";
  write(dir, "settings.json", `{ "out": "${unixHome}" }`);
  const r = verifyPublishSet({ dir, entryName: "Renamer", requiredBundledDlls: [], denylist });
  assert.equal(r.ok, false);
  assert.ok(r.failures.some((f) => f.includes("LEAK") && f.includes("settings.json")));
});

test("CLI: exit 0 on pass, non-zero on failure, resolving the entry from the real catalog", () => {
  const pass = tmpDir();
  write(pass, "Renamer.dll");
  // The catalog's Renamer entry declares requiredBundledDlls: ["System.IO.Hashing"], so a passing
  // publish set must carry it (this is the CLI's catalog-resolution path, unlike the unit tests above
  // that pass requiredBundledDlls explicitly).
  write(pass, "System.IO.Hashing.dll");
  const ok = spawnSync(process.execPath, [scriptPath, pass, "Renamer"], { encoding: "utf8" });
  assert.equal(ok.status, 0, ok.stdout + ok.stderr);
  assert.ok(/PASS/i.test(ok.stdout));

  const empty = tmpDir();
  const bad = spawnSync(process.execPath, [scriptPath, empty, "Renamer"], { encoding: "utf8" });
  assert.notEqual(bad.status, 0);
});
