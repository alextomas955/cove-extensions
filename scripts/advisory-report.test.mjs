// Behavior coverage for the advisory report group: how each ecosystem's output is classified, which
// roots the npm side resolves, and the exit contract that makes the group advisory rather than silent.
//
// The classifiers exist because neither command's exit code is its finding. `dotnet list package
// --vulnerable` exits 0 whether or not it names an advisory — measured on this repo, against the real
// listing that named a high-severity transitive — so reading the code alone would report every listing
// as clean. And an empty reading means the command broke, which must never resolve to zero advisories.
//
// The .NET fixtures below are verbatim captures of `dotnet list package --vulnerable
// --include-transitive` over this solution, both shapes. They are strings rather than a live spawn on
// purpose: the root test glob runs in a job with no .NET setup, so a live case would skip there, and a
// skip that reads as coverage is the exact defect this group of checks exists to remove. The live
// reading is taken by the gate itself, whose classification is what the CI step prints.
import { test } from "node:test";
import assert from "node:assert/strict";

import {
  GATES,
  classifyAuditOutput,
  classifyVulnerableListing,
  npmAuditRoots,
  runGates,
} from "./verify-all.mjs";

const CLEAN_LISTING = `  Determining projects to restore...
  All projects are up-to-date for restore.

The following sources were used:
   https://api.nuget.org/v3/index.json

The given project \`Renamer.Tests\` has no vulnerable packages given the current sources.
The given project \`Renamer\` has no vulnerable packages given the current sources.
The given project \`WhisparrSync.Tests\` has no vulnerable packages given the current sources.
The given project \`WhisparrSync\` has no vulnerable packages given the current sources.
The given project \`Cove.Extensions.Shared.Testing\` has no vulnerable packages given the current sources.
The given project \`Cove.Extensions.Shared\` has no vulnerable packages given the current sources.
`;

const VULNERABLE_LISTING = `  Determining projects to restore...
  All projects are up-to-date for restore.

Project \`Renamer.Tests\` has the following vulnerable packages
   [net10.0]:
   Transitive Package                Resolved   Severity   Advisory URL
   > SQLitePCLRaw.lib.e_sqlite3      2.1.11     High       https://github.com/advisories/GHSA-2m69-gcr7-jv3q

The given project \`Renamer\` has no vulnerable packages given the current sources.
`;

// The third measured shape: with no audit suppression left on any project, restore fails on the
// advisory as an error and the listing is never reached, so the finding has to be recognised here or a
// real advisory reads as an unparseable command. Captured with the affected transitive pinned back to
// the version the advisory covers.
const RESTORE_REFUSED = `  Determining projects to restore...
/repo/extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj : error NU1903: Warning As Error: Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.11 has a known high severity vulnerability, https://github.com/advisories/GHSA-2m69-gcr7-jv3q [/repo/CoveExtensions.slnx]
  Failed to restore /repo/extensions/Renamer/src/Renamer.Tests/Renamer.Tests.csproj (in 93 ms).
`;

// The fixture gates below echo their output into the run log, and this file executes inside the root
// test glob that CI runs. So the text says what it is: a line reading like a real advisory in an
// unrelated job's log is the same misleading signal these checks exist to remove.
const FIXTURE_AUDIT_OUTPUT = "fixture output standing in for an npm audit report; not a real audit\n";

function reportGate(id, exitCode, classifier, output = "") {
  // A node -e one-liner stands in for the real command so no toolchain, network or advisory database
  // is involved: what is under test is the classification and the exit contract, not dotnet or npm.
  const script = output === "" ? "process.exit(" + String(exitCode) + ")" : "process.stdout.write(" + JSON.stringify(output) + ");process.exit(" + String(exitCode) + ")";
  return {
    id,
    scope: "repo",
    needs: [],
    group: "report",
    command: { bin: "node", args: ["-e", script], classifier },
    flipCondition: "a fixture record; the real ruling lives on the registry's report records",
  };
}

test("the clean listing parses as clean and the vulnerable one as a finding", () => {
  assert.equal(classifyVulnerableListing(CLEAN_LISTING).state, "clean");

  const found = classifyVulnerableListing(VULNERABLE_LISTING);
  assert.equal(found.state, "finding");
  assert.match(found.reason, /vulnerable package/);
});

test("a restore refused over the advisory is a finding, not an unparseable reading", () => {
  const verdict = classifyVulnerableListing(RESTORE_REFUSED);
  assert.equal(verdict.state, "finding");
  assert.match(verdict.reason, /no listing was produced/);

  // The moderate-severity variant of the same diagnostic must classify identically: the wording, not
  // the diagnostic code, is what is matched.
  const moderate = classifyVulnerableListing(
    "error : Package 'somepkg' 1.0.0 has a known moderate severity vulnerability, https://example.invalid",
  );
  assert.equal(moderate.state, "finding");
});

test("an empty listing is an error rather than a clean result", () => {
  for (const empty of ["", "   ", "\n\n"]) {
    const verdict = classifyVulnerableListing(empty);
    assert.equal(verdict.state, "error", JSON.stringify(empty));
    assert.match(verdict.reason, /printed nothing/);
  }
});

test("a listing matching neither documented shape is an error, not a pass", () => {
  const verdict = classifyVulnerableListing("MSBUILD : error MSB1003: Specify a project or solution file.");
  assert.equal(verdict.state, "error");
  assert.match(verdict.reason, /neither the clean nor the vulnerable shape/);
});

test("npm audit is classified on its exit code, except that empty output is an error", () => {
  assert.equal(classifyAuditOutput("found 0 vulnerabilities\n", 0).state, "clean");
  assert.equal(classifyAuditOutput("# npm audit report\n\n2 vulnerabilities\n", 1).state, "finding");

  // Both codes, because a broken command can exit either way and neither reading inspected anything.
  for (const code of [0, 1]) {
    const verdict = classifyAuditOutput("", code);
    assert.equal(verdict.state, "error", String(code));
    assert.match(verdict.reason, /printed nothing/);
  }
});

test("the npm roots are the four graphs that have dependencies, and exclude the dependency-less shared package", () => {
  const roots = npmAuditRoots();
  assert.equal(roots.length, 4);
  assert.ok(roots.includes("."));
  assert.ok(roots.includes("website"));
  assert.equal(roots.includes("shared/cove-extensions-ui"), false);
  assert.deepEqual(roots, npmAuditRoots());
});

test("both ecosystems are registered in the report group, each carrying the ruling and its flip condition", () => {
  const reports = GATES.filter((gate) => gate.group === "report");
  assert.ok(reports.length >= 2, "the report group holds fewer than two records");

  const ecosystems = reports.map((gate) => gate.command.bin);
  assert.ok(ecosystems.includes("dotnet"));
  assert.ok(ecosystems.includes("npm"));

  for (const gate of reports) {
    assert.ok(gate.flipCondition, gate.id + " carries no flip condition");
    assert.match(gate.flipCondition, /FLIP CONDITION/);
    assert.match(gate.flipCondition, /npm audit --audit-level=high/);
    // The condition must not read as a promise to gate once the audit is clean — that is the reading
    // the second, design-change half of it exists to rule out.
    assert.match(gate.flipCondition, /a clean audit alone does not make this group blocking/);
    assert.ok(gate.command.classifier, gate.id + " would then be judged by its exit code alone");
  }
});

test("a report-group finding is reported and does not move the exit code", () => {
  // The group has to be named: it is excluded from the default selection, which the last case here
  // asserts against the real registry.
  const result = runGates({
    gates: [reportGate("advisory-found", 1, "npmAudit", FIXTURE_AUDIT_OUTPUT)],
    group: ["report"],
  });
  assert.equal(result.fail, 1);
  assert.equal(result.exitCode, 0);
  assert.equal(result.results[0].gating, false);
  assert.match(result.results[0].reason, /reported advisories/);
});

test("a report record that printed nothing fails as a broken step rather than passing", () => {
  const result = runGates({
    gates: [reportGate("silent-report", 0, "vulnerableListing")],
    group: ["report"],
  });
  assert.equal(result.results[0].state, "FAIL");
  assert.match(result.results[0].reason, /printed nothing/);
  assert.equal(result.exitCode, 0);
});

test("a gating failure alongside a report finding still exits non-zero", () => {
  const gating = {
    id: "blocks",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["-e", "process.exit(1)"] },
  };
  const result = runGates({
    gates: [gating, reportGate("advisory-found", 1, "npmAudit", FIXTURE_AUDIT_OUTPUT)],
    group: ["fast", "report"],
  });
  assert.equal(result.fail, 2);
  assert.notEqual(result.exitCode, 0);
});

test("neither report record is reachable from a gating selection", () => {
  const reportIds = GATES.filter((gate) => gate.group === "report").map((gate) => gate.id);
  for (const group of [[], ["fast"], ["fast", "slow"]]) {
    const selected = runGates({
      gates: GATES.filter((gate) => reportIds.includes(gate.id)),
      group,
    });
    // Selecting only the report records under a gating selection must resolve to nothing at all, which
    // the runner reports as NO INPUT rather than as a clean run.
    assert.equal(selected.results.length, 0, group.join(",") || "the default selection");
    assert.ok(selected.noInput);
  }
});
