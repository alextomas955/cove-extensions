// The failure mode the action-pinning rule otherwise lacks. Pinning is a convention with no runtime
// consequence: a correctly-pinned tree looks exactly like an unchecked one, so the rule can only be
// verified by something that goes red when a pin is loosened.
//
// The rule: a third-party action must be referenced by a full-length, forty-character commit SHA.
// Forty characters exactly, because an abbreviated SHA is resolvable to more than one object as a
// repository grows, and because an ANNOTATED tag dereferences to a tag object whose own SHA is a
// different object from the commit it points at — pasting that object's SHA looks like a pin and
// pins nothing. A reference under the `actions` or `github` owner is GitHub first-party and is
// pinned by tag as the accepted convention here; a local composite action carries no external code
// and is out of scope.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

const root = path.resolve(import.meta.dirname, "..");
const workflowDir = path.join(root, ".github", "workflows");
const firstPartyOwners = new Set(["actions", "github"]);
const fullShaPattern = /^[0-9a-f]{40}$/;

function parseUses(text, file) {
  const found = [];
  const lines = text.split("\n");
  for (let i = 0; i < lines.length; i += 1) {
    const match = /^\s*(?:-\s*)?uses:\s*(\S+)/.exec(lines[i]);
    if (match) found.push({ file, line: i + 1, uses: match[1] });
  }
  return found;
}

// A reference is one of four kinds; only `third-party` carries a pinning obligation.
function classify(uses) {
  if (uses.startsWith("./") || uses.startsWith("../")) return "local";
  if (uses.startsWith("docker://")) return "docker";
  const owner = uses.split("/")[0];
  return firstPartyOwners.has(owner) ? "first-party" : "third-party";
}

function refOf(uses) {
  const at = uses.indexOf("@");
  return at === -1 ? "" : uses.slice(at + 1);
}

// Violations accumulate and are reported together, sorted by file then line, so two runs over the
// same tree print the same list in the same order and a reviewer sees the whole set rather than
// whichever one the scan reached first.
function findViolations(entries) {
  const found = [];
  for (const entry of entries) {
    const kind = classify(entry.uses);
    if (kind === "local" || kind === "docker") continue;
    const ref = refOf(entry.uses);
    if (ref === "") {
      found.push({ ...entry, reason: "carries no ref at all" });
      continue;
    }
    if (kind === "first-party") continue;
    if (!fullShaPattern.test(ref)) {
      found.push({ ...entry, reason: "is a third-party action not pinned to a full 40-character commit SHA" });
    }
  }
  found.sort((a, b) => (a.file === b.file ? a.line - b.line : a.file < b.file ? -1 : 1));
  return found.map((v) => `action-pinning: ${v.file}:${String(v.line)} — ${v.uses} ${v.reason}`);
}

// A scan that matched no reference has proven nothing about pinning, so it reports NO INPUT rather
// than an empty violation list: the workflows always carry references, and a zero match means the
// scan broke.
function scanWorkflows(dir) {
  const failures = [];
  if (!fs.existsSync(dir)) {
    return { entries: [], violations: [], failures: [`action-pinning: NO INPUT — ${dir} does not exist`] };
  }
  const files = fs
    .readdirSync(dir)
    .filter((name) => name.endsWith(".yml") || name.endsWith(".yaml"))
    .sort();
  const entries = [];
  for (const name of files) {
    entries.push(...parseUses(fs.readFileSync(path.join(dir, name), "utf8"), name));
  }
  if (entries.length === 0) {
    failures.push(`action-pinning: NO INPUT — no uses: reference found in ${String(files.length)} workflow file(s)`);
  }
  return { entries, violations: findViolations(entries), failures };
}

test("every third-party action in the real workflows is pinned to a full commit SHA", () => {
  const { entries, violations, failures } = scanWorkflows(workflowDir);
  assert.deepEqual(failures, [], failures.join("\n"));
  assert.ok(entries.length > 0, "the scan must inspect at least one uses: reference");
  assert.deepEqual(violations, [], violations.join("\n"));

  const thirdParty = entries.filter((e) => classify(e.uses) === "third-party");
  assert.ok(thirdParty.length > 0, "the scan must reach at least one third-party reference to mean anything");
});

test("a scan that matched zero uses: references fails instead of reporting a clean tree", () => {
  const empty = scanWorkflows(path.join(root, "scripts"));
  assert.equal(empty.failures.length, 1);
  assert.ok(empty.failures[0].includes("NO INPUT"), empty.failures[0]);
  assert.deepEqual(empty.violations, []);

  const missing = scanWorkflows(path.join(root, "no-such-directory"));
  assert.ok(missing.failures[0].includes("NO INPUT"), missing.failures[0]);
});

test("a tag-pinned third-party reference in a fixture is reported with its file and line", () => {
  const fixture = [
    "jobs:",
    "  probe:",
    "    steps:",
    "      - uses: actions/checkout@v7",
    "      - uses: DavidAnson/markdownlint-cli2-action@v24",
    "        with:",
    "          globs: 'README.md'",
  ].join("\n");
  const violations = findViolations(parseUses(fixture, "fixture.yml"));
  assert.equal(violations.length, 1);
  assert.ok(violations[0].includes("fixture.yml:5"), violations[0]);
  assert.ok(violations[0].includes("DavidAnson/markdownlint-cli2-action@v24"), violations[0]);
  assert.ok(violations[0].includes("full 40-character commit SHA"), violations[0]);
});

test("the pin predicate: a full SHA passes, a tag and an abbreviated SHA do not, first-party tags are exempt", () => {
  const accepted = findViolations([
    { file: "a.yml", line: 1, uses: "softprops/action-gh-release@3d0d9888cb7fd7b750713d6e236d1fcb99157228" },
    { file: "a.yml", line: 2, uses: "actions/checkout@v7" },
    { file: "a.yml", line: 3, uses: "github/codeql-action/init@v4" },
    { file: "a.yml", line: 4, uses: "./.github/actions/local-composite" },
  ]);
  assert.deepEqual(accepted, [], accepted.join("\n"));

  const rejected = findViolations([
    { file: "b.yml", line: 1, uses: "softprops/action-gh-release@v3" },
    { file: "b.yml", line: 2, uses: "softprops/action-gh-release@3d0d988" },
    // The v24 ANNOTATED tag's own object SHA, which is not the commit it points at. It has the
    // shape of a pin, so only the length rule plus a human reading the version comment separates
    // it from the commit SHA; the check cannot tell them apart and does not claim to.
    { file: "b.yml", line: 3, uses: "third/party@29e1992fd532969762382607bd9acb1a05aa4dc" },
    { file: "b.yml", line: 4, uses: "third/party" },
  ]);
  assert.equal(rejected.length, 4, rejected.join("\n"));
  assert.ok(rejected.some((v) => v.includes("b.yml:4") && v.includes("no ref at all")), rejected.join("\n"));
});

test("every violation is reported, in file-then-line order, identically across runs", () => {
  const entries = [
    { file: "z.yml", line: 9, uses: "third/party@v1" },
    { file: "a.yml", line: 30, uses: "other/party@v2" },
    { file: "a.yml", line: 4, uses: "another/party@abc1234" },
  ];
  const first = findViolations(entries);
  const second = findViolations(entries);
  assert.equal(first.length, 3, "all three violations are reported, not only the first");
  assert.deepEqual(first, second);
  assert.ok(first[0].includes("a.yml:4"), first.join("\n"));
  assert.ok(first[1].includes("a.yml:30"), first.join("\n"));
  assert.ok(first[2].includes("z.yml:9"), first.join("\n"));
});
