// Subprocess-fixture test suite for scripts/check-version-parity.mjs.
//
// The subject exports nothing — it is top-level straight-line code that reads its path arguments
// and calls process.exit — so it can only be driven as a child process, asserting exit code plus
// stderr/stdout text per case. How the fixture is reached diverges from
// validate-extension-repo.test.mjs: that validator resolves its root from its own file location, so
// each fixture there needs a copy of the validator bytes. This subject resolves every argument
// against process.cwd(), so the real script is spawned in place with cwd set to the fixture root —
// which is also the only way the read guard's own root is exercised rather than a copy's.
//
// Every argument a fixture case passes is repo-root-RELATIVE. An absolute fixture path would defeat
// the containment guard's own premise on macOS, where mkdtempSync hands back a /var/folders path
// while the spawned child reports the resolved /private/var/folders cwd.
import { test } from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const scriptPath = path.join(here, "check-version-parity.mjs");
const repoRoot = path.resolve(here, "..");

function validManifest(overrides = {}) {
  return { version: "0.3.0", minCoveVersion: "1.0.0", ...overrides };
}

// check-version-parity.mjs:62 matches the two C# overrides by PROPERTY NAME, so a fixture source
// only reads back if it carries both arrow-property forms spelled exactly as the real source does.
function validCsharp(overrides = {}) {
  const { version = "0.3.0", minCoveVersion = "1.0.0" } = overrides;
  return [
    "public sealed class Fixture",
    "{",
    `    public override string Version => "${version}";`,
    `    public override string? MinCoveVersion => "${minCoveVersion}";`,
    "}",
    "",
  ].join("\n");
}

function validUiPkg(overrides = {}) {
  return { name: "fixture-ui", version: "0.3.0", ...overrides };
}

function validCatalogManifest(overrides = {}) {
  return {
    id: "com.example.fixture",
    versions: [{ version: "0.3.0", minCoveVersion: "1.0.0", ...overrides }],
  };
}

// Builds a temp tree holding all four sources the gate can read, and returns the four
// repo-root-relative arguments in the order check-version-parity.mjs:34 destructures them. Callers
// override one source at a time so each case carries exactly one disagreement.
function makeFixture({
  manifest = validManifest(),
  csharp = validCsharp(),
  uiPkg = validUiPkg(),
  catalogManifest = validCatalogManifest(),
} = {}) {
  const root = mkdtempSync(path.join(tmpdir(), "version-parity-fixture-"));
  const args = ["src/extension.json", "src/Fixture.cs", "ui/package.json", "registry/com.example.fixture.json"];
  for (const rel of args) {
    mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
  }
  const [manifestArg, csharpArg, uiPkgArg, catalogManifestArg] = args;
  writeFileSync(path.join(root, manifestArg), JSON.stringify(manifest, null, 2));
  writeFileSync(path.join(root, csharpArg), csharp);
  writeFileSync(path.join(root, uiPkgArg), JSON.stringify(uiPkg, null, 2));
  writeFileSync(path.join(root, catalogManifestArg), JSON.stringify(catalogManifest, null, 2));
  return { root, args };
}

function runParity(cwd, args) {
  const result = spawnSync(process.execPath, [scriptPath, ...args], { cwd, encoding: "utf8" });
  return { status: result.status, stderr: result.stderr, stdout: result.stdout };
}

test("four agreeing arguments exit 0 and print the OK line", () => {
  const { root, args } = makeFixture();
  try {
    const { status, stdout, stderr } = runParity(root, args);
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
    // The success line at check-version-parity.mjs:131-133.
    assert.match(stdout, /check-version-parity: OK/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the three-argument shape, with the catalog registry manifest omitted, exits 0", () => {
  // The optional fourth source is gated on `if (catalogManifestArg)` at check-version-parity.mjs:87,
  // so omitting it must leave the two axes reconciled from three sources rather than fail arity.
  const { root, args } = makeFixture();
  try {
    const { status, stderr } = runParity(root, args.slice(0, 3));
    assert.equal(status, 0, "expected exit 0, stderr: " + stderr);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("an argument resolving to the filesystem root is refused with exit 1 naming the argument", () => {
  // An absolute argument wins over the cwd in path.resolve, so the read at check-version-parity.mjs:49-56
  // would otherwise reach outside the checkout entirely. This is the shape a catalog entry with an
  // empty uiPath produces: "" + "/package.json".
  const { root, args } = makeFixture();
  try {
    const { status, stderr } = runParity(root, [args[0], args[1], "/package.json"]);
    assert.equal(status, 1, "expected exit 1, stderr: " + stderr);
    assert.match(stderr, /refusing path outside the repository root: \/package\.json/);
    // The guard must fire ahead of the read, so no filesystem error escapes.
    assert.doesNotMatch(stderr, /ENOENT/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a parent-directory traversal argument is refused with exit 1", () => {
  // The other half of the guard at check-version-parity.mjs:49-56: a relative argument that climbs out
  // of the root resolves inside no checkout the invoker controls.
  const { root, args } = makeFixture();
  try {
    const { status, stderr } = runParity(root, [args[0], args[1], "../../../etc/passwd"]);
    assert.equal(status, 1, "expected exit 1, stderr: " + stderr);
    assert.match(stderr, /refusing path outside the repository root/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a UI package.json version that disagrees still reports FAILED with its drift detail", () => {
  // The gate the guard sits in front of: the banner at check-version-parity.mjs:123-129 and the drift
  // wording at :114 must both survive, or a guard could shadow the check it protects.
  const { root, args } = makeFixture({ uiPkg: validUiPkg({ version: "0.4.0" }) });
  try {
    const { status, stderr } = runParity(root, args);
    assert.equal(status, 1, "expected exit 1, stderr: " + stderr);
    assert.match(stderr, /check-version-parity: FAILED/);
    assert.match(stderr, /artifact version drift/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the real committed call shape, built from extensions/catalog.json, exits 0", () => {
  // Reproduces the argv the release workflow assembles per catalog entry: manifestPath,
  // versionSourcePath, "<uiPath>/package.json", and the registry manifest at
  // "<path>/extensions/<id>.json" appended only when that file exists.
  const catalog = JSON.parse(readFileSync(path.join(repoRoot, "extensions", "catalog.json"), "utf8"));
  let checked = 0;
  for (const entry of catalog.extensions) {
    if (!entry.versionSourcePath) {
      continue;
    }
    const args = [entry.manifestPath, entry.versionSourcePath, `${entry.uiPath}/package.json`];
    const registry = `${entry.path}/extensions/${entry.id}.json`;
    if (existsSync(path.join(repoRoot, registry))) {
      args.push(registry);
    }
    const { status, stdout, stderr } = runParity(repoRoot, args);
    assert.equal(status, 0, `${entry.id}: expected exit 0, stderr: ${stderr}`);
    assert.match(stdout, /check-version-parity: OK/);
    checked += 1;
  }
  // A catalog that exercised nothing would leave this case green on an empty loop.
  assert.ok(checked > 0, "expected at least one catalog entry with a versionSourcePath");
});
