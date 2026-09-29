// Drives the real check-host-externals.mjs as a child process against a fixture externals list and a
// fixture Cove runtime contract. The script resolves the repository from its own location, so each
// fixture gets a copy of it under its own `scripts/`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const SCRIPT = "check-host-externals.mjs";
const CONTRACT = "ui/scripts/extension-runtime-contract.ts";

// The shape Cove's contract file has: a type declaration naming the fields, then one entry per module.
function contractSource(modules) {
  return [
    "type Entry = { specifier: string; legacySpecifiers: string[] };",
    "export const modules: Entry[] = [",
    ...modules.map(
      ({ specifier, aliases }) =>
        `  { specifier: "${specifier}", legacySpecifiers: [${aliases.map((a) => `"${a}"`).join(", ")}] },`,
    ),
    "];",
    "",
  ].join("\n");
}

const MODULES = [
  { specifier: "@cove/runtime/react", aliases: ["react"] },
  { specifier: "@cove/runtime/api", aliases: [] },
];

function fixture({ externals, contract = contractSource(MODULES) }) {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), "check-host-externals-"));
  const repo = path.join(parent, "repo");
  fs.mkdirSync(path.join(repo, "scripts"), { recursive: true });
  fs.copyFileSync(path.join(import.meta.dirname, SCRIPT), path.join(repo, "scripts", SCRIPT));
  const listPath = path.join(repo, "shared", "ui-shared", "vite", "host-externals.json");
  fs.mkdirSync(path.dirname(listPath), { recursive: true });
  fs.writeFileSync(listPath, JSON.stringify(externals));
  const cove = path.join(parent, "cove");
  if (contract !== null) {
    fs.mkdirSync(path.dirname(path.join(cove, CONTRACT)), { recursive: true });
    fs.writeFileSync(path.join(cove, CONTRACT), contract);
  }
  return { parent, repo, cove };
}

function run({ repo, cove }) {
  return spawnSync(process.execPath, [path.join(repo, "scripts", SCRIPT)], {
    encoding: "utf8",
    env: { ...process.env, COVE_REPO: cove },
  });
}

function check(options, assertions) {
  const f = fixture(options);
  try {
    assertions(run(f));
  } finally {
    fs.rmSync(f.parent, { recursive: true, force: true });
  }
}

test("a list naming every canonical specifier and alias the host serves passes, counting them", () => {
  check({ externals: ["@cove/runtime/react", "react", "@cove/runtime/api"] }, (result) => {
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, /OK \(3 host import-map specifiers, all externalized\)/);
  });
});

test("a served alias missing from the list fails, naming it", () => {
  check({ externals: ["@cove/runtime/react", "@cove/runtime/api"] }, (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /would carry its own copy of each:\n {2}react$/m);
  });
});

test("an externalized specifier the host does not serve fails, naming it", () => {
  check(
    { externals: ["@cove/runtime/react", "react", "@cove/runtime/api", "@cove/runtime/gone"] },
    (result) => {
      assert.equal(result.status, 1, result.stdout + result.stderr);
      assert.match(
        result.stderr,
        /absent from the host import map[^\n]*\n {2}@cove\/runtime\/gone$/m,
      );
    },
  );
});

test("a contract the parse reads nothing out of fails rather than comparing against an empty map", () => {
  check({ externals: [], contract: "export const modules = [];\n" }, (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /parsed no specifiers/);
  });
});

test("a checkout named through COVE_REPO without the contract fails", () => {
  check({ externals: [], contract: null }, (result) => {
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /holds no runtime contract/);
  });
});
