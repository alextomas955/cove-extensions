// Drives the real check-host-imports.mjs as a child process against a fixture repository and a
// fixture Cove checkout. The script resolves the repository from its own location, so each fixture
// gets a copy of it under its own `scripts/`.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const SCRIPT = "check-host-imports.mjs";
const SHIM = "ui/src/generated/extensions/runtime/v1/lucide-react.ts";

function write(root, relative, content) {
  fs.mkdirSync(path.dirname(path.join(root, relative)), { recursive: true });
  fs.writeFileSync(path.join(root, relative), content);
}

/**
 * A parent directory holding `repo/` and, unless `hostExports` is null, `cove/` with a shim exporting
 * those names. The repository sits one level down so its `../cove` default lands inside the fixture.
 */
function fixture({ source, hostExports = ["Check", "Plus"] }) {
  const parent = fs.mkdtempSync(path.join(os.tmpdir(), "check-host-imports-"));
  const repo = path.join(parent, "repo");
  fs.mkdirSync(path.join(repo, "scripts"), { recursive: true });
  fs.copyFileSync(path.join(import.meta.dirname, SCRIPT), path.join(repo, "scripts", SCRIPT));
  write(repo, "extensions/Foo/src/Foo.Ui/src/view.tsx", source);
  write(repo, "extensions/Foo/src/Foo/Foo.cs", "");
  const cove = path.join(parent, "cove");
  if (hostExports !== null) {
    write(
      cove,
      SHIM,
      hostExports.map((name) => `export const ${name} = Icons.${name};\n`).join(""),
    );
  }
  return { parent, repo, cove };
}

function run({ repo }, coveRepo) {
  const env = { ...process.env };
  delete env.COVE_REPO;
  if (coveRepo !== undefined) env.COVE_REPO = coveRepo;
  return spawnSync(process.execPath, [path.join(repo, "scripts", SCRIPT)], {
    encoding: "utf8",
    env,
  });
}

test("a value import the host shim does not export fails, naming the file and the icon", () => {
  const f = fixture({ source: 'import { Check, Sparkles } from "lucide-react";\n' });
  try {
    const result = run(f, f.cove);
    assert.equal(result.status, 1, result.stdout + result.stderr);
    assert.match(result.stderr, /extensions[/\\]Foo[/\\]src[/\\]Foo\.Ui[/\\]src[/\\]view\.tsx/);
    assert.match(result.stderr, /"Sparkles" is absent from the host runtime shim/);
    assert.doesNotMatch(result.stderr, /"Check"/);
  } finally {
    fs.rmSync(f.parent, { recursive: true, force: true });
  }
});

test("type-only imports are not checked, and an aliased import is checked by its exported name", () => {
  const f = fixture({
    source: [
      'import type { Missing } from "lucide-react";',
      'import { type AlsoMissing, Plus as AddIcon } from "lucide-react";',
      "",
    ].join("\n"),
  });
  try {
    const result = run(f, f.cove);
    assert.equal(result.status, 0, result.stdout + result.stderr);
    // One value import checked: the alias resolved to Plus, and both type-only names were skipped.
    assert.match(
      result.stdout,
      /OK \(1 lucide-react imports across 1 UI bundles, 2 host exports\)/,
    );
  } finally {
    fs.rmSync(f.parent, { recursive: true, force: true });
  }
});

test("a checkout named through COVE_REPO without the shim fails, while no checkout at all skips", () => {
  const f = fixture({ source: 'import { Check } from "lucide-react";\n', hostExports: null });
  try {
    const named = run(f, f.cove);
    assert.equal(named.status, 1, named.stdout + named.stderr);
    assert.match(named.stderr, /holds no host shim/);

    const absent = run(f);
    assert.equal(absent.status, 0, absent.stdout + absent.stderr);
    assert.match(absent.stdout, /SKIPPED - no host shim/);
  } finally {
    fs.rmSync(f.parent, { recursive: true, force: true });
  }
});
