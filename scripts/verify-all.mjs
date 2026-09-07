#!/usr/bin/env node
// The one gate runner both CI and a local terminal invoke. Every merge gate is declared once in the
// GATES registry below as a record carrying its own scope, its own prerequisites and its own command,
// and each CI job selects ids from that registry instead of restating a command list — there is no
// second list, so CI and a local run cannot drift. This is the same relationship strip-verify.mjs has
// to build.yml and prove-release-path.mjs: one implementation, two callers.
//
// A gate that CANNOT run reports SKIPPED naming the missing prerequisite, and is counted apart from
// PASS. That distinction is the reason this runner exists: check-host-imports reads a shim that only
// exists beside a ../cove checkout, so on a bare runner it exits 0 with a SKIPPED log that is
// indistinguishable on the wire from a gate that ran and found nothing. Here a skip never enters the
// pass count and never absorbs a failure — a run holding both a FAIL and a SKIPPED exits non-zero.
// --require-no-skips turns a skip into a failure for the local pre-merge run, where the prerequisite
// IS present and a skip therefore means something is broken rather than absent.
//
// An empty selection is a failure, not a pass, for the same reason: a run that inspected nothing is
// not evidence that anything is clean. A record naming an output classifier extends the same rule to
// its own output: for those, the finding is in what the command PRINTED rather than in its exit code
// (`dotnet list package --vulnerable` exits 0 whether or not it names an advisory), and no output at
// all is reported as a broken step rather than as zero findings.
//
// A FAIL in a non-gating group is reported and does not move the exit code — which is the whole
// meaning of that side of the boundary. It is the group's declaration, not the command's exit code,
// that decides this, so a group cannot become silently gating or silently advisory.
//
// Cove SDK source: the sibling-dependent gates resolve it with the same precedence the repo's
// Directory.Build wiring uses — an explicit flag, then COVE_REPO, then a ../cove sibling, else the
// pinned NuGet packages — and the resolved source is printed once per run, so a reader can tell which
// leg produced the summary.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
import { pathToFileURL } from "node:url";

const root = path.resolve(import.meta.dirname, "..");

// The three states are named literals rather than a boolean because the third one carries the whole
// contract: a gate that could not run is neither a pass nor a finding, and folding it into either
// loses the only distinction a reader of the summary needs.
const PASS = "PASS";
const FAIL = "FAIL";
const SKIPPED = "SKIPPED";

const EXIT_OK = 0;
const EXIT_FINDING = 1;
// A broken registry or an unrecognised selector is a malformed invocation, not a gate finding.
// Keeping the codes apart lets a caller tell "a gate found something" from "you asked for a gate that
// does not exist".
const EXIT_MALFORMED = 2;

const AllowedCommands = new Set(["dotnet", "node", "npm", "npx", "actionlint"]);

// The token a per-extension command's args carry where the catalog field named by `command.pathField`
// is substituted in. The value always comes from committed catalog data, never from argv.
const PATH_TOKEN = "{{path}}";

// A registry or selector problem throws rather than exiting, because the self-tests import this
// module directly: a process.exit() in the imported core would take the test runner down with it, and
// the "a SKIPPED never masks a FAIL" contract could then only be checked by spawning a subprocess.
// The CLI converts the throw into one stderr line plus EXIT_MALFORMED.
class GateConfigError extends Error {}

function fail(message) {
  throw new GateConfigError(message);
}

// The command name is gated on the allowlist above, and every argument is either a literal from the
// registry in this file or a path field read from the committed catalog — never argv. Callers pass an
// args ARRAY with no shell, Node's own recommended-safe form, so there is no shell-quoting or
// command-name injection surface for SonarCloud's agentic argument-injection rule (S8705) to find.
function spawnAllowed(command, args, options) {
  if (!AllowedCommands.has(command)) fail("refusing to run non-allowlisted command: " + command);
  return spawnSync(command, args, options); // NOSONAR
}

// Child output is forwarded rather than captured so a failing gate's own diagnostics reach the log
// exactly as they would when the gate is invoked on its own. A signal-terminated child counts as a
// failure, never as a pass.
function runCommand(command, args, cwd) {
  const result = spawnAllowed(command, args, { stdio: "inherit", cwd });
  return result.status ?? 1;
}

// Output is captured and then echoed rather than inherited, because a classified record's verdict is
// read from the text. A failure to spawn at all yields no output, which every classifier treats as an
// error — so a missing binary cannot read as a clean report.
function runCapturing(command, args, cwd) {
  const result = spawnAllowed(command, args, { encoding: "utf8", cwd });
  const output = (result.stdout ?? "") + (result.stderr ?? "");
  process.stdout.write(output);
  return { code: result.status ?? 1, output };
}

/**
 * Classifies the output of the .NET vulnerable-package listing.
 *
 * @param {string} output - the listing's combined stdout and stderr.
 * @returns {{state: "clean"|"finding"|"error", reason: string|null}} `error` covers both an empty
 *   reading and output matching neither documented shape, because in each case nothing was read.
 */
export function classifyVulnerableListing(output) {
  if (output.trim() === "") {
    return {
      state: "error",
      reason: "the listing printed nothing, so no project was read — a broken command is not zero advisories",
    };
  }
  if (output.includes("has the following vulnerable packages")) {
    return { state: "finding", reason: "the listing names at least one vulnerable package" };
  }
  // With no audit suppression left on any project, restore itself fails on an advisory before a
  // listing exists, so the finding arrives as a restore diagnostic instead of a table. Matched on the
  // diagnostic's own wording rather than on its code, which keeps the suppressed token out of the tree
  // and covers the moderate-severity variant too.
  if (/has a known .*vulnerability/.test(output)) {
    return {
      state: "finding",
      reason: "restore reported a known vulnerability, so no listing was produced",
    };
  }
  if (!output.includes("has no vulnerable packages")) {
    return {
      state: "error",
      reason: "the listing matched neither the clean nor the vulnerable shape, so it was not understood",
    };
  }
  return { state: "clean", reason: null };
}

/**
 * Classifies one npm audit run.
 *
 * @param {string} output - the audit's combined stdout and stderr.
 * @param {number} code - the audit's exit code, non-zero when it found something at its threshold.
 * @returns {{state: "clean"|"finding"|"error", reason: string|null}}
 */
export function classifyAuditOutput(output, code) {
  if (output.trim() === "") {
    return {
      state: "error",
      reason: "npm audit printed nothing, so no dependency graph was read — a broken command is not zero advisories",
    };
  }
  return code === 0
    ? { state: "clean", reason: null }
    : { state: "finding", reason: "npm audit reported advisories" };
}

const OUTPUT_CLASSIFIERS = {
  vulnerableListing: classifyVulnerableListing,
  npmAudit: classifyAuditOutput,
};

/**
 * Resolves whether the sibling-dependent gates have a local Cove source to read.
 *
 * @param {string[]} [argv] - argument list to read the explicit override from.
 * @returns {boolean} true when a local ../cove source is reachable, false for the pinned-NuGet leg.
 */
export function resolveUseLocalCove(argv = process.argv.slice(2)) {
  const flag = argv.find((a) => a === "--local-cove" || a === "--nuget-cove");
  if (flag === "--local-cove") return true;
  if (flag === "--nuget-cove") return false;
  if (process.env.COVE_REPO && fs.existsSync(process.env.COVE_REPO)) return true;
  if (fs.existsSync(path.join(root, "..", "cove"))) return true;
  return false;
}

// The build matrix, the release harness and this runner all read the same committed registry, so
// "per extension" never means a hardcoded pair of names. manifestOnly entries carry no project or UI
// to gate, which is the same opt-out the workflows apply.
function catalogEntries() {
  const catalog = JSON.parse(fs.readFileSync(path.join(root, "extensions", "catalog.json"), "utf8"));
  return catalog.extensions.filter((entry) => entry.manifestOnly !== true);
}

/**
 * Resolves every npm root that has a dependency graph worth auditing.
 *
 * @returns {string[]} repo-relative directories: the workspace root, the docs site, and each catalog
 *   entry's UI package.
 * @remarks `shared/cove-extensions-ui` is deliberately absent. It declares no dependency at all — the
 *   extensions resolve it from source through a Vite alias — so an audit there reports a clean result
 *   having inspected nothing, which would read as coverage it does not have.
 */
export function npmAuditRoots() {
  return [".", "website", ...catalogEntries().filter((entry) => entry.uiPath).map((entry) => entry.uiPath)];
}

const ROOT_SETS = { npmAudit: npmAuditRoots };

function onPath(binary) {
  const dirs = (process.env.PATH ?? "").split(path.delimiter).filter(Boolean);
  const names = process.platform === "win32" ? [binary + ".exe", binary + ".cmd", binary] : [binary];
  return dirs.some((dir) => names.some((name) => fs.existsSync(path.join(dir, name))));
}

// A prerequisite is a named capability rather than an inline existence check so two gates needing the
// same input report the same reason and the same remedy. An unsatisfied capability yields SKIPPED — a
// structurally unavailable input is not a finding, and reporting it as one would train a reader to
// ignore the state that matters. Each probe runs only when a selected gate names it, which is why the
// docker probe never fires in a default run: the container group is not selected there.
const CAPABILITIES = {
  coveSibling: () =>
    resolveUseLocalCove()
      ? { ok: true }
      : { ok: false, reason: "no ../cove sibling (set COVE_REPO or add a ../cove checkout)" },
  uiNodeModules: () => {
    const missing = catalogEntries()
      .filter((entry) => entry.uiPath)
      .filter((entry) => !fs.existsSync(path.join(root, entry.uiPath, "node_modules")))
      .map((entry) => entry.uiPath);
    return missing.length === 0
      ? { ok: true }
      : { ok: false, reason: "no node_modules in " + missing.join(", ") + " (run npm run provision:ui)" };
  },
  docker: () =>
    onPath("docker")
      ? { ok: true }
      : { ok: false, reason: "no docker on PATH (the containerized tier needs a running daemon)" },
  actionlint: () =>
    onPath("actionlint")
      ? { ok: true }
      : { ok: false, reason: "no actionlint on PATH (install the binary to run the workflow linter)" },
  // A coverage report is an emitted artifact rather than an installed tool, so its absence is
  // structural in exactly the same way a missing sibling checkout is. Reporting a pass with no report
  // present would be the vacuous green a floor exists to remove; --require-no-skips is what turns the
  // skip into a failure where the report should be there.
  coverageReport: () => {
    const missing = catalogEntries()
      .filter((entry) => entry.testProjectPath)
      .filter((entry) => !hasCoverageReport(entry))
      .map((entry) => entry.name);
    return missing.length === 0
      ? { ok: true }
      : {
          ok: false,
          reason:
            "no coverage report for " +
            missing.join(", ") +
            ' (collect with: dotnet test <project> --collect:"XPlat Code Coverage")',
        };
  },
};

// Mirrors the discovery the floor gate itself does, so the capability and the gate agree about what
// counts as a present report rather than each deciding separately.
function hasCoverageReport(entry) {
  const dir = path.join(root, path.dirname(entry.testProjectPath), "TestResults");
  if (!fs.existsSync(dir)) return false;
  const stack = [dir];
  while (stack.length > 0) {
    const current = stack.pop();
    for (const item of fs.readdirSync(current, { withFileTypes: true })) {
      if (item.isDirectory()) stack.push(path.join(current, item.name));
      else if (item.name === "coverage.cobertura.xml") return true;
    }
  }
  return false;
}

// Whether a group is SELECTED by a given run and whether it GATES are independent axes, and
// conflating them is the mistake this pair of sets exists to prevent. `container` sits out of the
// default selection because it needs Docker and dominates runtime, and it gates fully whenever it IS
// selected — which is why neither e2e record carries a flip condition and why one must not be added.
// `local-warn` is the opposite case: reachable in a local run yet contributing nothing to the exit
// status, which is precisely why it owes one. Being excluded from a selection never excuses a group
// from gating. Every group is listed in exactly one of these sets so a new group cannot be introduced
// without someone deciding its side; a group in neither is rejected when the registry loads. These
// sets are what the runner reads to decide the exit contribution, so a non-gating record's command is
// free to exit non-zero — `npm audit` does exactly that whenever it finds anything — without the
// group's advisory status depending on a --warn flag someone could drop.
export const GATING_GROUPS = new Set(["fast", "slow", "container", "coverage"]);
export const NON_GATING_GROUPS = new Set(["local-warn", "report"]);

// Docker and minutes of runtime for `container`; a NuGet restore, four npm graphs and a live advisory
// database for `report` — neither exclusion is a judgement about whether the tier gates. Reach them
// with --group container / --group report, or npm run verify:e2e.
const DEFAULT_EXCLUDED_GROUPS = new Set(["container", "report"]);

const REPORT_GROUP_RULING =
  "Group ruling, shared by every record in the report group: an advisory is published asynchronously " +
  "by a third party, so a blocking audit turns an external event into a red build on an unrelated " +
  "change. The group therefore reports and never gates. FLIP CONDITION — two facts must hold " +
  "together, and only the first is a reading: (1) `npm audit --audit-level=high` exits 0 in every " +
  "root npmAuditRoots() returns, on a clean checkout; while it exits non-zero anywhere the ruling " +
  "stands on evidence rather than on preference. (2) The step reads from a committed advisory " +
  "snapshot instead of live data — a design change, NOT a threshold being crossed, and without it the " +
  "architectural objection survives a clean audit because a newly published advisory would redden an " +
  "unrelated change the next morning. LAST READING, 2026-08-03, npm 11.17.0, all four roots: fact (1) " +
  "IS satisfied — every root exits 0. It took a lockfile bump in the repo root (brace-expansion, " +
  "undici) and in website (brace-expansion, fast-uri, undici); both extension UI roots were already " +
  "clean, carrying one moderate each and no high. The clause this text used to carry — that the docs " +
  "site holds a high npm can only resolve by downgrading Docusaurus — no longer describes anything: " +
  "every high in that reading reported an in-range fix, and taking them added and removed nothing. " +
  "Fact (2) has NOT happened, so a clean audit alone does not make this group blocking — that is what " +
  "the second half of the condition exists to rule out, and a reading is stale the moment it is " +
  "written. ";

const LOCAL_ONLY_RULING =
  "Records what would reopen the local-only ruling; it is NOT a schedule, and neither heuristic gate " +
  "is planned to become blocking — the recorded ruling is that pre-commit is the right home for a " +
  "style nudge, because findings a reviewer has to triage in CI train people to ignore CI output. ";

// Each record declares what the call site otherwise cannot: which gates are repo-wide, which cover a
// single extension, which run once per extension, and what each one needs before it can run at all. A
// workflow job names ids from here rather than restating a command, which is what makes adding a gate
// one edit in one file. A repo-wide gate runs ONCE from the root; declaring that here is what stops it
// being invoked per extension with its real scope invisible at the call site.
export const GATES = [
  {
    id: "csharp-format",
    scope: "repo",
    needs: [],
    group: "fast",
    // Scoped to this repo's own sources. On a checkout with a ../cove sibling the solution
    // project-references that repo, so an unscoped run formats ITS files too and reports another
    // repo's findings as this one's — unfixable here, and invisible in CI, which has no sibling and
    // so stays green. dotnet format takes no -p: passthrough, so the scope is a path filter.
    command: {
      bin: "dotnet",
      args: [
        "format",
        "CoveExtensions.slnx",
        "--verify-no-changes",
        "--severity",
        "warn",
        "--include",
        "extensions/",
        "shared/",
      ],
    },
  },
  {
    id: "csharp-analyzers",
    scope: "repo",
    needs: [],
    group: "slow",
    command: {
      bin: "dotnet",
      args: ["build", "CoveExtensions.slnx", "-c", "Release", "-p:UseLocalCoveSource=false"],
    },
  },
  {
    id: "validate-catalog",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["scripts/validate-extension-repo.mjs"] },
  },
  // The root test glob. Every scripts/*.test.mjs runs here, including strip-verify.test.mjs — the
  // release-safety gate's own tests, which no workflow reached before this gate existed.
  {
    id: "root-self-tests",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "npm", args: ["test"] },
  },
  {
    id: "syncpack",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "npm", args: ["run", "syncpack"] },
  },
  {
    id: "jscpd",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "npm", args: ["run", "jscpd"] },
  },
  // The config's own globs array selects the file set; a CLI glob would merge with it rather than
  // replace it, so none is passed here. Routed through the root script rather than npx, which resolves
  // the pinned devDependency from node_modules with no registry or npx-cache path at all — the linted
  // version is then a committed fact rather than whatever is newest when the gate runs, and the
  // dependency's use is declared where a dead-dependency check can see it.
  {
    id: "markdownlint",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "npm", args: ["run", "markdownlint"] },
  },
  {
    id: "knip",
    scope: "repo",
    needs: ["uiNodeModules"],
    group: "fast",
    command: { bin: "npm", args: ["run", "knip"] },
  },
  {
    id: "eslint",
    scope: "repo",
    needs: ["uiNodeModules"],
    group: "slow",
    command: { bin: "npm", args: ["run", "lint"] },
  },
  // The workflow linter, which reached no gate before this record existed — roughly a thousand lines of
  // workflow YAML with embedded node one-liners and shell heredocs were the least-checked code here. It
  // runs through scripts/check-workflows.mjs rather than as a bare binary because two properties cannot
  // be asserted from the binary's own exit code: the linter reports zero findings rather than failing
  // when its shell checker is absent, and every finding these workflows have comes from that half; and a
  // scan that matched no workflow file must fail rather than read as clean. The binary itself is the
  // declared prerequisite, so an absent linter reports SKIPPED with its remedy instead of either state.
  {
    id: "actionlint",
    scope: "repo",
    needs: ["actionlint"],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-workflows.mjs"] },
  },
  {
    id: "host-imports",
    scope: "repo",
    needs: ["coveSibling"],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-host-imports.mjs"] },
  },
  {
    id: "sdk-drift",
    scope: "repo",
    needs: ["coveSibling"],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-sdk-drift.mjs"] },
  },

  // The three gates below hold WhisparrSync in their own sources: check-response-casing's POLICY list
  // records that Renamer's wire is PascalCase on purpose, and the other two resolve a WhisparrSync UI
  // path directly. Run for Renamer they would re-analyse WhisparrSync's files and report them as
  // Renamer coverage, so the scope is declared here rather than widened.
  {
    id: "response-casing",
    scope: "extension:WhisparrSync",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-response-casing.mjs"] },
  },
  {
    id: "refusal-handled",
    scope: "extension:WhisparrSync",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-refusal-handled.mjs"] },
  },
  {
    id: "guarded-controls",
    scope: "extension:WhisparrSync",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-guarded-controls.mjs"] },
  },

  {
    id: "typecheck",
    scope: "each-extension",
    needs: ["uiNodeModules"],
    group: "fast",
    command: { bin: "npm", args: ["run", "typecheck"], cwdField: "uiPath" },
  },
  {
    id: "format-check",
    scope: "each-extension",
    needs: ["uiNodeModules"],
    group: "fast",
    command: { bin: "npm", args: ["run", "format:check"], cwdField: "uiPath" },
  },
  {
    id: "check-classes",
    scope: "each-extension",
    needs: [],
    group: "fast",
    command: { bin: "npm", args: ["run", "check-classes"], cwdField: "uiPath" },
  },
  {
    id: "logic-gates",
    scope: "each-extension",
    needs: ["uiNodeModules"],
    group: "slow",
    command: { bin: "npm", args: ["run", "test"], cwdField: "uiPath" },
  },
  {
    id: "ui-build",
    scope: "each-extension",
    needs: ["uiNodeModules"],
    group: "fast",
    command: { bin: "npm", args: ["run", "build"], cwdField: "uiPath" },
  },
  // Pinned to the pinned-NuGet leg, which is the leg CI resolves; the cove-present leg is a separate
  // local pre-merge run and would compile a different source set.
  {
    id: "dotnet-test",
    scope: "each-extension",
    needs: [],
    group: "slow",
    command: {
      bin: "dotnet",
      args: ["test", PATH_TOKEN, "-c", "Release", "-p:UseLocalCoveSource=false"],
      pathField: "testProjectPath",
    },
  },

  // The coverage collector enforces no threshold of its own, so the floor is this separate step over
  // the report the collection emits. `coverage` is a GATING group declared before this record existed,
  // so the record carries no flipCondition and the load path would reject one on it. Its prerequisite
  // is the report: with none collected there is nothing to read, and a pass there would be the vacuous
  // green this runner exists to remove.
  {
    id: "coverage-floor",
    scope: "repo",
    needs: ["coverageReport"],
    group: "coverage",
    command: { bin: "node", args: ["scripts/coverage-floor.mjs"] },
  },

  {
    id: "e2e",
    scope: "each-extension",
    needs: ["docker"],
    group: "container",
    command: {
      bin: "npx",
      args: ["playwright", "test", "--project=" + PATH_TOKEN],
      cwd: "tests/e2e",
      pathField: "e2eProject",
    },
  },
  // The glob is required: node --test reads a bare directory as a module entry point and fails, so the
  // spec files must be enumerated.
  {
    id: "e2e-node-tests",
    scope: "each-extension",
    needs: ["docker"],
    group: "container",
    command: {
      bin: "node",
      args: ["--test", "--test-concurrency=1", PATH_TOKEN + "/*.test.mjs"],
      pathField: "e2eNodeTestsPath",
    },
  },

  {
    id: "wire-usage",
    scope: "repo",
    needs: [],
    group: "local-warn",
    command: { bin: "node", args: ["scripts/check-wire-usage.mjs", "--warn"] },
    flipCondition:
      LOCAL_ONLY_RULING +
      "While `node scripts/check-wire-usage.mjs` without --warn over the committed tree reports a " +
      "non-zero candidate count, the local-only ruling stands. The condition is met only when that " +
      "same invocation reports zero candidates over the committed tree on two consecutive commits, at " +
      "which point the heuristic is no longer noisy and CI presence becomes a question worth reopening.",
  },
  // Blocking, and audits HISTORY rather than the staged diff. The staged-diff default is what let the
  // rule rot: with nothing staged the run printed NO INPUT and went green, so a violation was only ever
  // caught in the commit that introduced it, and never afterwards — 163 planning ids accumulated across
  // 122 files under a gate that was, on paper, already watching for them. The tells were also loose by
  // design, which is why this could not block before: they flagged "so the …" and "rather than …", the
  // canonical shape of a why-comment. They are precise now, so every finding is real and the gate can
  // refuse a merge without pushing anyone to delete a good comment to get past it.
  {
    id: "comment-hygiene",
    scope: "repo",
    needs: [],
    group: "fast",
    command: { bin: "node", args: ["scripts/check-comment-hygiene.mjs", "--range", "main..HEAD"] },
  },

  // Neither ecosystem's advisory command ran in CI before these two records existed, so the repo's
  // only supply-chain signal was Dependabot's pull requests. Both read and report only: no package is
  // resolved and no lockfile is written.
  {
    id: "dotnet-advisories",
    scope: "repo",
    needs: [],
    group: "report",
    command: {
      bin: "dotnet",
      args: ["list", "package", "--vulnerable", "--include-transitive"],
      classifier: "vulnerableListing",
    },
    flipCondition: REPORT_GROUP_RULING,
  },
  {
    id: "npm-advisories",
    scope: "repo",
    needs: [],
    group: "report",
    command: { bin: "npm", args: ["audit"], rootSet: "npmAudit", classifier: "npmAudit" },
    flipCondition: REPORT_GROUP_RULING,
  },
];

const SCOPE = /^(repo|each-extension|extension:[A-Za-z0-9_.-]+)$/;

/**
 * Validates a gate registry, throwing on the first structural problem.
 *
 * @param {object[]} gates - registry records to check.
 * @throws {Error} when an id repeats, a scope or capability is unrecognised, a command is not an args
 *   array, a classifier or root set is unrecognised, a group is not declared on exactly one side of
 *   the gating boundary, or a record's flipCondition disagrees with its group's gating status.
 */
export function assertRegistry(gates) {
  if (!Array.isArray(gates)) fail("the gate registry must be an array");
  const seen = new Set();
  for (const gate of gates) {
    if (!gate.id) fail("every gate record needs a non-empty id");
    if (seen.has(gate.id)) fail("duplicate gate id in the registry: " + gate.id);
    seen.add(gate.id);
    if (!SCOPE.test(gate.scope ?? "")) {
      fail(gate.id + ": scope must be repo, each-extension, or extension:<Name>, got " + gate.scope);
    }
    if (!Array.isArray(gate.needs)) fail(gate.id + ": needs must be an array");
    if (!gate.group) fail(gate.id + ": needs a non-empty group");
    if (!Array.isArray(gate.command?.args)) fail(gate.id + ": command.args must be an array");
    if (!AllowedCommands.has(gate.command?.bin)) {
      fail(gate.id + ": command.bin is not allowlisted: " + gate.command?.bin);
    }
    for (const need of gate.needs) {
      if (!Object.hasOwn(CAPABILITIES, need)) fail(gate.id + ": unknown capability in needs: " + need);
    }
    const { classifier, rootSet } = gate.command;
    if (classifier !== undefined && !Object.hasOwn(OUTPUT_CLASSIFIERS, classifier)) {
      fail(gate.id + ": unknown output classifier: " + classifier);
    }
    if (rootSet !== undefined && !Object.hasOwn(ROOT_SETS, rootSet)) {
      fail(gate.id + ": unknown root set: " + rootSet);
    }
    const gating = GATING_GROUPS.has(gate.group);
    if (gating === NON_GATING_GROUPS.has(gate.group)) {
      fail(
        gate.id +
          ": group " +
          gate.group +
          " must be declared in exactly one of GATING_GROUPS or NON_GATING_GROUPS",
      );
    }
    if (!gating && !gate.flipCondition) {
      fail(
        gate.id +
          ": group " +
          gate.group +
          " does not contribute to the exit status, so the record must carry a flipCondition",
      );
    }
    if (gating && gate.flipCondition) {
      fail(
        gate.id +
          ": group " +
          gate.group +
          " contributes to the exit status, so the record must not carry a flipCondition",
      );
    }
  }
}

/**
 * Resolves the gate ids one extension's own `verify` script covers.
 *
 * @param {object[]} gates - registry records to select from.
 * @param {string} name - a catalog entry name.
 * @returns {object[]} the records that run inside that extension's UI package plus those scoped to it
 *   by name, in registry declaration order.
 */
export function gatesForExtension(gates, name) {
  return gates.filter(
    (gate) =>
      gate.scope === "extension:" + name ||
      (gate.scope === "each-extension" && gate.command.cwdField === "uiPath"),
  );
}

// Filtering the registry array in place is what keeps the summary in declaration order, so two runs
// over the same selection print the same order.
function selectGates(gates, only, group, extension) {
  let selected = gates;
  if (extension !== null) {
    const names = catalogEntries().map((entry) => entry.name);
    if (!names.includes(extension)) fail("unknown extension: " + extension);
    selected = gatesForExtension(selected, extension);
  }
  if (only.length > 0) {
    const known = new Set(gates.map((g) => g.id));
    for (const id of only) {
      if (!known.has(id)) fail("unknown gate id: " + id);
    }
    selected = selected.filter((g) => only.includes(g.id));
  }
  if (group.length > 0) {
    selected = selected.filter((g) => group.includes(g.group));
  } else if (only.length === 0) {
    selected = selected.filter((g) => !DEFAULT_EXCLUDED_GROUPS.has(g.group));
  }
  return selected;
}

function unmetCapability(gate) {
  for (const need of gate.needs) {
    const state = CAPABILITIES[need]();
    if (!state.ok) return need + ": " + state.reason;
  }
  return null;
}

// A scope with no runner is a registry error rather than a silent no-op: a gate nobody executes is
// exactly the shape of pass this runner exists to remove.
function invocationsFor(gate, extension) {
  const { command } = gate;
  const at = (relative) => path.join(root, relative ?? ".");
  // A root set is one repo-wide gate run once per directory it names, so its coverage is a resolved
  // list rather than a glob the reader has to trust.
  if (command.rootSet) return ROOT_SETS[command.rootSet]().map((rel) => ({ cwd: at(rel), args: command.args }));
  if (gate.scope === "repo") return [{ cwd: at(command.cwd), args: command.args }];

  let entries = catalogEntries();
  if (gate.scope !== "each-extension") {
    const name = gate.scope.slice("extension:".length);
    entries = entries.filter((entry) => entry.name === name);
  }
  if (extension !== null) entries = entries.filter((entry) => entry.name === extension);

  const invocations = [];
  for (const entry of entries) {
    // An entry that declares neither the working directory nor the path this gate acts on has nothing
    // for it to read — the same optional opt-out the build matrix applies to a missing testProjectPath.
    if (command.cwdField && !entry[command.cwdField]) continue;
    if (command.pathField && !entry[command.pathField]) continue;
    const cwd = command.cwdField ? at(entry[command.cwdField]) : at(command.cwd);
    const args = command.pathField
      ? command.args.map((arg) => arg.replaceAll(PATH_TOKEN, entry[command.pathField]))
      : command.args;
    invocations.push({ cwd, args });
  }
  return invocations;
}

function describeSelector(only, group, extension) {
  const parts = [];
  if (extension !== null) parts.push("--extension " + extension);
  if (only.length > 0) parts.push("--only " + only.join(","));
  if (group.length > 0) parts.push("--group " + group.join(","));
  return parts.length > 0 ? parts.join(" ") : "the whole registry";
}

/**
 * Installs each catalog UI package's dependencies.
 *
 * @returns {number} 0 when every install succeeded, otherwise a failing install's exit code.
 */
export function provisionUiNodeModules() {
  let status = 0;
  for (const entry of catalogEntries()) {
    if (!entry.uiPath) continue;
    console.log("verify-all: provisioning " + entry.uiPath);
    // `npm ci`, not `npm install`: these UI packages commit a lockfile, and installing from it is
    // what keeps a CI run reproducible and stops a transitive advisory being silently re-resolved.
    const code = runCommand("npm", ["ci"], path.join(root, entry.uiPath));
    if (code !== 0) status = code;
  }
  return status;
}

/**
 * Runs a gate selection and reports each gate as PASS, FAIL, or SKIPPED.
 *
 * @param {object} [opts]
 * @param {object[]} [opts.gates] - registry to run; defaults to GATES.
 * @param {string[]} [opts.only] - gate ids to select; every id must exist in the registry.
 * @param {string[]} [opts.group] - group names to select.
 * @param {string|null} [opts.extension] - restrict to one catalog entry's own gate set.
 * @param {boolean} [opts.requireNoSkips] - make a skip contribute to a non-zero exit.
 * @returns {{ pass: number, fail: number, skipped: number, results: {id: string, state: string, gating: boolean, reason: string|null}[], noInput: string|null, exitCode: number }}
 *   `results` is in registry declaration order; `exitCode` is the run's exit contribution, to which a
 *   FAIL in a non-gating group deliberately contributes nothing.
 * @throws {Error} on a malformed registry, an unknown gate id, or an unknown extension name.
 */
export function runGates({
  gates = GATES,
  only = [],
  group = [],
  extension = null,
  requireNoSkips = false,
} = {}) {
  assertRegistry(gates);
  const selected = selectGates(gates, only, group, extension);
  const results = [];
  let noInput = null;

  console.log("Cove SDK source: " + (resolveUseLocalCove() ? "local ../cove sibling" : "pinned NuGet Cove.Sdk"));

  if (selected.length === 0) {
    noInput = describeSelector(only, group, extension) + " matched no gate, so nothing was inspected";
    console.error("verify-all: NO INPUT — " + noInput + ".");
    console.error("A green here is not evidence that any gate ran. Name a registered gate id or group.");
  }

  for (const gate of selected) {
    const gating = GATING_GROUPS.has(gate.group);
    const unmet = unmetCapability(gate);
    if (unmet) {
      console.log("verify-all: " + gate.id + ": " + SKIPPED + " — " + unmet);
      results.push({ id: gate.id, state: SKIPPED, gating, reason: unmet });
      continue;
    }
    const invocations = invocationsFor(gate, extension);
    if (invocations.length === 0) {
      const field = gate.command.cwdField ?? gate.command.pathField ?? "a path";
      const reason = "no catalog entry declares " + field + ", so this gate read nothing";
      console.log("verify-all: " + gate.id + ": " + SKIPPED + " — " + reason);
      results.push({ id: gate.id, state: SKIPPED, gating, reason });
      continue;
    }
    const classifier = gate.command.classifier ? OUTPUT_CLASSIFIERS[gate.command.classifier] : null;
    let status = 0;
    const findings = [];
    for (const invocation of invocations) {
      if (classifier === null) {
        const code = runCommand(gate.command.bin, invocation.args, invocation.cwd);
        if (code !== 0) status = code;
        continue;
      }
      const { code, output } = runCapturing(gate.command.bin, invocation.args, invocation.cwd);
      const verdict = classifier(output, code);
      if (verdict.state !== "clean") findings.push((path.relative(root, invocation.cwd) || ".") + ": " + verdict.reason);
    }
    const found = findings.length > 0 || status !== 0;
    results.push({
      id: gate.id,
      state: found ? FAIL : PASS,
      gating,
      reason: !found ? null : findings.length > 0 ? findings.join("; ") : gate.command.bin + " exited " + status,
    });
  }

  const pass = results.filter((r) => r.state === PASS).length;
  const failed = results.filter((r) => r.state === FAIL).length;
  const skipped = results.filter((r) => r.state === SKIPPED).length;
  const gatingFailures = results.filter((r) => r.state === FAIL && r.gating).length;
  const exitCode =
    gatingFailures > 0 || noInput !== null || (requireNoSkips && skipped > 0) ? EXIT_FINDING : EXIT_OK;

  console.log("");
  console.log("VERIFY-ALL: " + pass + " passed, " + failed + " failed, " + skipped + " skipped");
  for (const result of results) {
    if (result.state === PASS) continue;
    const aside = result.gating ? "" : " (reported, not gating)";
    console.log("  " + result.state + " " + result.id + (result.reason ? " — " + result.reason : "") + aside);
  }
  if (failed > gatingFailures) {
    console.log(
      "  " +
        String(failed - gatingFailures) +
        " of those sit in a non-gating group and do not move the exit code — read the output above rather than the code.",
    );
  }
  if (requireNoSkips && skipped > 0) {
    console.log("  --require-no-skips: a skip is a failure here because the input should be present.");
  }

  return { pass, fail: failed, skipped, results, noInput, exitCode };
}

function flagValue(argv, flag) {
  const index = argv.indexOf(flag);
  if (index === -1) return null;
  const value = argv[index + 1];
  if (!value || value.startsWith("--")) fail(flag + " needs a value");
  return value;
}

function commaList(argv, flag) {
  const value = flagValue(argv, flag);
  return value === null ? [] : value.split(",").filter(Boolean);
}

function main(argv) {
  try {
    const provision = flagValue(argv, "--provision");
    if (provision !== null) {
      if (provision !== "uiNodeModules") fail("--provision knows only uiNodeModules, got " + provision);
      return provisionUiNodeModules();
    }
    return runGates({
      only: commaList(argv, "--only"),
      group: commaList(argv, "--group"),
      extension: flagValue(argv, "--extension"),
      requireNoSkips: argv.includes("--require-no-skips"),
    }).exitCode;
  } catch (error) {
    if (!(error instanceof GateConfigError)) throw error;
    console.error("verify-all: " + error.message);
    return EXIT_MALFORMED;
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(main(process.argv.slice(2)));
}
