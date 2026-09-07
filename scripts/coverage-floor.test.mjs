// Behavior coverage for the per-assembly coverage floor gate. Every fixture report is written to a
// temp directory, so the tracked tree is never modified and two concurrent runs cannot observe each
// other's fixtures. The boundary cases are separate named cases because the boundary is the contract
// most likely to be silently wrong.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

import { checkCoverageFloors } from "./coverage-floor.mjs";

const scriptPath = fileURLToPath(new URL("./coverage-floor.mjs", import.meta.url));

function tmpDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "coverage-floor-"));
}

// The rates are written as strings so a case can pin an exact decimal literal rather than whatever a
// float formatter produces. The root element carries its own line-rate precisely so a case can prove
// the gate never reads it.
function writeReport(dir, packages, { rootLineRate = "0.0118" } = {}) {
  const entries = packages
    .map((entry) => `  <package name="${entry.name}" line-rate="${entry.lineRate}" branch-rate="0.1">
    <classes />
  </package>`)
    .join("\n");
  const file = path.join(dir, "coverage.cobertura.xml");
  fs.writeFileSync(
    file,
    `<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="${rootLineRate}" branch-rate="0.2" version="1.9" lines-covered="1" lines-valid="2">
  <sources />
  <packages>
${entries}
  </packages>
</coverage>
`,
  );
  return file;
}

function writeFloorsFile(dir, lineRateFloors) {
  const file = path.join(dir, "floors.json");
  fs.writeFileSync(file, JSON.stringify({ lineRateFloors }, null, 2));
  return file;
}

test("an assembly above its floor passes", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.4209" }]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, true, result.failures.join("; "));
  assert.deepEqual(result.measured, [{ assembly: "Renamer", lineRate: 0.4209 }]);
});

test("an assembly below its floor fails naming the assembly, its rate and its floor", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "WhisparrSync", lineRate: "0.5" }]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "WhisparrSync", floor: 63 }] });
  assert.equal(result.ok, false);
  const [failure] = result.failures;
  assert.match(failure, /BELOW FLOOR/);
  assert.match(failure, /WhisparrSync/);
  assert.match(failure, /50\.00%/);
  assert.match(failure, /63\.00%/);
});

test("a rate exactly equal to its floor passes", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.4" }]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, true, result.failures.join("; "));
});

test("a rate one hundredth of a percent below its floor fails", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.3999" }]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, false);
  assert.match(result.failures[0], /BELOW FLOOR/);
  assert.match(result.failures[0], /39\.99%/);
});

// Scaling the rate up by one hundred puts 0.29 at 28.999999999999996, so an exactly-equal floor of 29
// would fail. Comparing in the report's own fractional domain is what keeps the equal case exact, and
// this case is here so a later simplification to `rate * 100 >= floor` goes red.
test("an exactly-equal floor whose scaled form loses precision still passes", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Shared", lineRate: "0.29" }]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Shared", floor: 29 }] });
  assert.equal(result.ok, true, result.failures.join("; "));
});

test("a missing report fails saying the report was absent", () => {
  const dir = tmpDir();
  const reportPath = path.join(dir, "coverage.cobertura.xml");
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, false);
  assert.match(result.failures[0], /NO REPORT/);
  assert.match(result.failures[0], /absent/);
  assert.deepEqual(result.measured, []);
});

test("a report with no assembly entries fails saying nothing was measured", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, []);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, false);
  assert.match(result.failures[0], /NOTHING MEASURED/);
  assert.equal(result.failures.length, 1, "an empty report is one finding, not one per floor");
});

test("an assembly named in the floors but absent from the report fails", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.9" }]);
  const result = checkCoverageFloors({
    reportPath,
    floors: [{ assembly: "WhisparrSync", floor: 63 }],
  });
  assert.equal(result.ok, false);
  assert.match(result.failures[0], /NOT MEASURED/);
  assert.match(result.failures[0], /WhisparrSync/);
});

test("two entries for the same assembly fail rather than the last one winning", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [
    { name: "Renamer", lineRate: "0.1" },
    { name: "Renamer", lineRate: "0.99" },
  ]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, false);
  assert.ok(result.failures.some((failure) => /DUPLICATE ENTRY/.test(failure)));
});

test("a placeholder floor fails rather than being treated as met", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.9" }]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: null }] });
  assert.equal(result.ok, false);
  assert.match(result.failures[0], /UNSET FLOOR/);
});

test("an assembly with no declared floor is reported without failing", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [
    { name: "Renamer", lineRate: "0.5" },
    { name: "Cove.Extensions.Shared", lineRate: "0.01" },
  ]);
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, true, result.failures.join("; "));
  assert.ok(result.measured.some((entry) => entry.assembly === "Cove.Extensions.Shared"));
});

test("the report-level rate is never the number checked", () => {
  const dir = tmpDir();
  // A root rate far below every floor beside per-assembly rates far above them: the gate must read the
  // assemblies. This is the leg where host assemblies are instrumented, in miniature.
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.9" }], { rootLineRate: "0.1185" });
  const result = checkCoverageFloors({ reportPath, floors: [{ assembly: "Renamer", floor: 40 }] });
  assert.equal(result.ok, true, result.failures.join("; "));
});

test("every failing assembly is reported in the floors' declaration order, identically on a second run", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [
    { name: "Alpha", lineRate: "0.1" },
    { name: "Beta", lineRate: "0.1" },
    { name: "Gamma", lineRate: "0.1" },
  ]);
  const floors = [
    { assembly: "Gamma", floor: 90 },
    { assembly: "Alpha", floor: 80 },
    { assembly: "Beta", floor: 70 },
  ];
  const first = checkCoverageFloors({ reportPath, floors });
  const second = checkCoverageFloors({ reportPath, floors });
  assert.equal(first.failures.length, 3);
  assert.deepEqual(
    first.failures.map((failure) => failure.split(" ")[2]),
    ["Gamma", "Alpha", "Beta"],
  );
  assert.deepEqual(second.failures, first.failures);
});

test("CLI: exit 0 when every declared floor is met, non-zero when one is not", () => {
  const dir = tmpDir();
  const reportPath = writeReport(dir, [{ name: "Renamer", lineRate: "0.42" }]);
  const met = writeFloorsFile(dir, [{ extension: "Renamer", assembly: "Renamer", floor: 40 }]);
  const ok = spawnSync(
    process.execPath,
    [scriptPath, "--extension", "Renamer", "--report", reportPath, "--floors", met],
    { encoding: "utf8" },
  );
  assert.equal(ok.status, 0, ok.stdout + ok.stderr);
  assert.match(ok.stdout, /42\.00%/);

  const unmet = writeFloorsFile(tmpDir(), [{ extension: "Renamer", assembly: "Renamer", floor: 43 }]);
  const bad = spawnSync(
    process.execPath,
    [scriptPath, "--extension", "Renamer", "--report", reportPath, "--floors", unmet],
    { encoding: "utf8" },
  );
  assert.equal(bad.status, 1);
  assert.match(bad.stderr, /BELOW FLOOR/);
});

test("CLI: a selection matching no declared floor is a failure, not a pass", () => {
  const floorsFile = writeFloorsFile(tmpDir(), [
    { extension: "Renamer", assembly: "Renamer", floor: 40 },
  ]);
  const result = spawnSync(
    process.execPath,
    [scriptPath, "--extension", "WhisparrSync", "--floors", floorsFile],
    { encoding: "utf8" },
  );
  assert.equal(result.status, 1);
  assert.match(result.stderr, /NO INPUT/);
});

test("the committed floors file names both first-party assemblies and no shared library", () => {
  const file = path.resolve(path.dirname(scriptPath), "..", ".github", "COVERAGE_FLOORS.json");
  const data = JSON.parse(fs.readFileSync(file, "utf8"));
  const assemblies = data.lineRateFloors.map((floor) => floor.assembly);
  assert.deepEqual([...assemblies].sort(), ["Renamer", "WhisparrSync"]);
  assert.ok(
    !JSON.stringify(data).includes("Cove.Extensions.Shared"),
    "the shared library assembly is deliberately unfloored and is not named",
  );
  assert.ok(
    data.lineRateFloors.every((floor) => !("branchRate" in floor) && !("branch" in floor)),
    "line rate only",
  );
});
