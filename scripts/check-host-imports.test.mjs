// Subprocess-fixture test suite for scripts/check-host-imports.mjs.
//
// The subject exports nothing — it is top-level straight-line code ending in process.exit — so it
// can only be driven as a child process, asserting exit code plus output text per case. It resolves
// the repo root from its OWN file location rather than process.cwd(), so each fixture gets a copy of
// the real script bytes (copied at run time, never hand-written) inside a `<fixture>/scripts/`
// subfolder, and that copy is spawned so its scan roots land in the fixture tree. The oracle is
// pointed at a second fixture tree through COVE_REPO.
//
// Every case is fixture-driven, including the happy path. Running the happy path against the real
// repository would make it pass or fail on whether a Cove host checkout happens to sit beside this
// one, which is exactly the kind of environment-dependent green this gate exists to stop producing.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, copyFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const realScriptPath = path.join(here, "check-host-imports.mjs");

// The shim's shape as the host generates it: one `export const <Name> = ...` line per icon. The
// script's parser keys on exactly that form, so a fixture that drifted from it would test the
// fixture rather than the host contract.
function shimSource(names) {
  return names.map((n) => `export const ${n} = /*#__PURE__*/ createIcon("${n}");\n`).join("");
}

// Builds the two trees a run needs and returns both roots:
//   <repo>/scripts/check-host-imports.mjs                            (real script bytes)
//   <repo>/extensions/<ext>/src/<ext>.Ui/src/<file>                  (the scanned sources)
//   <cove>/ui/src/generated/extensions/runtime/v1/lucide-react.ts    (the oracle)
// `shim: null` omits the shim file entirely, which is the only way to reach the skip branch.
function makeFixture({ sources = {}, shim = ["Check", "Pencil"], extName = "Foo" } = {}) {
  const repo = mkdtempSync(path.join(tmpdir(), "host-imports-repo-"));
  const cove = mkdtempSync(path.join(tmpdir(), "host-imports-cove-"));

  mkdirSync(path.join(repo, "scripts"), { recursive: true });
  copyFileSync(realScriptPath, path.join(repo, "scripts", "check-host-imports.mjs"));

  const uiSrc = path.join(repo, "extensions", extName, "src", `${extName}.Ui`, "src");
  mkdirSync(uiSrc, { recursive: true });
  for (const [relPath, content] of Object.entries(sources)) {
    const full = path.join(uiSrc, relPath);
    mkdirSync(path.dirname(full), { recursive: true });
    writeFileSync(full, content);
  }

  if (shim !== null) {
    const shimDir = path.join(cove, "ui", "src", "generated", "extensions", "runtime", "v1");
    mkdirSync(shimDir, { recursive: true });
    writeFileSync(path.join(shimDir, "lucide-react.ts"), shimSource(shim));
  }

  return { repo, cove };
}

function runGate({ repo, cove }) {
  const result = spawnSync(process.execPath, [path.join(repo, "scripts", "check-host-imports.mjs")], {
    encoding: "utf8",
    env: { ...process.env, COVE_REPO: cove },
  });
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}

function cleanup({ repo, cove }) {
  rmSync(repo, { recursive: true, force: true });
  rmSync(cove, { recursive: true, force: true });
}

test("a UI whose lucide imports all exist in the shim exits 0 and reports what it inspected", () => {
  const fixture = makeFixture({
    shim: ["Check", "Pencil", "Trash2"],
    sources: {
      "Icons.tsx": 'import { Check, Pencil as Edit } from "lucide-react";\nexport const a = [Check, Edit];\n',
    },
  });
  try {
    const { status, stdout, stderr } = runGate(fixture);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    // The count must be asserted as a value, not as "some number appeared" — a report line that
    // tolerated 0 would re-open the exact hole this gate spent its whole life inside.
    const inspected = stdout.match(/OK \((\d+) lucide-react imports/);
    assert.ok(inspected, "expected the OK line to report an inspected count, got: " + stdout);
    assert.strictEqual(inspected[1], "2");
    // The verdict is only as good as the oracle, so the oracle must be named in the output.
    assert.match(stdout, /lucide-react\.ts/);
    assert.match(stdout, /3 host exports/);
  } finally {
    cleanup(fixture);
  }
});

test("a resolvable shim with zero imports to inspect fails rather than reporting a zero-count pass", () => {
  const fixture = makeFixture({
    sources: {
      "Plain.tsx": 'export const Plain = () => null;\n',
    },
  });
  try {
    const { status, stdout, stderr } = runGate(fixture);
    assert.notEqual(status, 0);
    assert.match(stderr, /no lucide-react imports were inspected/);
    assert.doesNotMatch(stdout, /OK \(/);
  } finally {
    cleanup(fixture);
  }
});

test("a shim that parses to no exports fails, and says so distinctly from an empty inspection", () => {
  const fixture = makeFixture({
    shim: [],
    sources: {
      "Icons.tsx": 'import { Check } from "lucide-react";\nexport const a = Check;\n',
    },
  });
  try {
    const { status, stderr } = runGate(fixture);
    assert.notEqual(status, 0);
    assert.match(stderr, /parsed to 0 exports/);
    // The two empty-input defects have different causes and different fixes, so an operator must
    // be able to tell them apart from the message alone.
    assert.doesNotMatch(stderr, /no lucide-react imports were inspected/);
  } finally {
    cleanup(fixture);
  }
});

test("an imported name absent from the shim fails, naming the file, the symbol and the blast radius", () => {
  const fixture = makeFixture({
    shim: ["Check", "Pencil"],
    sources: {
      "Icons.tsx": 'import { Check, Sparkles } from "lucide-react";\nexport const a = [Check, Sparkles];\n',
    },
  });
  try {
    const { status, stderr } = runGate(fixture);
    assert.notEqual(status, 0);
    assert.match(stderr, /Icons\.tsx/);
    assert.match(stderr, /"Sparkles" is absent from the host runtime shim/);
    // Without this sentence the finding reads as one broken icon rather than a dead extension.
    assert.match(stderr, /whole bundle at load/);
  } finally {
    cleanup(fixture);
  }
});

test("a type-only lucide import is not inspected, because it never reaches the host module", () => {
  // Erased before the module is fetched, so it cannot fail at load — in either the whole-clause or
  // the inline-specifier form. Both forms are exercised here against names the shim does not have.
  const fixture = makeFixture({
    shim: ["Check"],
    sources: {
      "Types.tsx": [
        'import type { LucideIcon } from "lucide-react";',
        'import { Check, type LucideProps } from "lucide-react";',
        "export const a: LucideIcon = Check;",
        "export type B = LucideProps;",
        "",
      ].join("\n"),
    },
  });
  try {
    const { status, stdout, stderr } = runGate(fixture);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    const inspected = stdout.match(/OK \((\d+) lucide-react imports/);
    assert.ok(inspected, "expected the OK line to report an inspected count, got: " + stdout);
    assert.strictEqual(inspected[1], "1");
  } finally {
    cleanup(fixture);
  }
});

test("an absent host checkout skips at exit 0 with a notice naming the gate local-only", () => {
  const fixture = makeFixture({
    shim: null,
    sources: {
      "Icons.tsx": 'import { Check } from "lucide-react";\nexport const a = Check;\n',
    },
  });
  try {
    const { status, stdout, stderr } = runGate(fixture);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    assert.match(stdout, /SKIPPED/);
    assert.match(stdout, /local-only/);
    assert.match(stdout, /CI/);
    // The old notice told the reader to add a sibling checkout, and printed that advice while the
    // sibling was sitting right there — which is how a gate that had never inspected anything went
    // unnoticed. The skip branch cannot issue advice about a condition it has not established.
    assert.doesNotMatch(stdout, /add a \.\.\/cove sibling/);
  } finally {
    cleanup(fixture);
  }
});
