#!/usr/bin/env node
// Per-assembly line-rate floor over an emitted cobertura report. The coverage collector this repo
// already references supports no threshold mechanism of its own — its own documentation states that
// threshold validation is unavailable through the test-platform integration — so enforcement is a
// separate step over the report by construction rather than by preference. Both CI and a local run
// read the floors from one committed file (.github/COVERAGE_FLOORS.json), the same relationship
// strip-verify.mjs has to .github/DLL_DENYLIST.json, so the two callers cannot disagree.
//
// ONLY the per-assembly <package> rate is read. The report-level rate on the root <coverage> element
// is deliberately never touched: on the leg where the host is a project reference, tens of thousands
// of lines of host code are instrumented and that figure describes code these tests were never meant
// to cover, so it is not a number about this repo in either direction.
//
// A missing report, a report with no assembly entries, an expected assembly absent from the report,
// and two entries for one assembly are each a distinct FAILURE. A gate that passes when nothing was
// measured is worse than no gate: it reports coverage it never read.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";

const root = path.resolve(import.meta.dirname, "..");

const FLOORS_FILE = path.join(root, ".github", "COVERAGE_FLOORS.json");
const REPORT_NAME = "coverage.cobertura.xml";
const RESULTS_DIR = "TestResults";

// Attributes are read out of a matched <package> tag independently of their order, so a collector
// that reorders them does not silently stop matching.
const PACKAGE_TAG = /<package\b[^>]*>/g;
const NAME_ATTR = /\bname="([^"]*)"/;
const LINE_RATE_ATTR = /\bline-rate="([^"]*)"/;

/**
 * Checks one emitted coverage report against per-assembly line-rate floors.
 *
 * @param {object} opts
 * @param {string} opts.reportPath - the cobertura report to read; its absence is a failure.
 * @param {{assembly: string, floor: number}[]} opts.floors - floors in declaration order, as
 *   percentages of lines.
 * @returns {{ ok: boolean, failures: string[], measured: {assembly: string, lineRate: number}[] }}
 * @remarks Reads and writes nothing, so two runs over the same report report identically and an
 *   interrupted run leaves no partial state. The boundary is stated rather than left to a comparison
 *   operator: a rate EQUAL to its floor passes and anything below it fails. The comparison happens in
 *   the report's own fractional domain (`rate >= floor / 100`) rather than by scaling the rate up,
 *   because dividing the percentage by one hundred reproduces exactly the double the report's decimal
 *   literal parses to, while multiplying the rate by one hundred can land a hundredth below an
 *   exactly-equal floor. There is no rounding step anywhere, so a rate under its floor can never round
 *   up to meet it.
 *   Failures are ordered report-shape problems first (by assembly name), then floor shortfalls in the
 *   floors' declaration order, so two runs over one report print identically.
 */
export function checkCoverageFloors({ reportPath, floors }) {
  const failures = [];
  /** @type {{assembly: string, lineRate: number}[]} */
  const measured = [];

  if (!fs.existsSync(reportPath)) {
    failures.push(
      "NO REPORT: " +
        reportPath +
        " is absent, so no coverage was read at all — collect with --collect:\"XPlat Code Coverage\" before this gate runs.",
    );
    return { ok: false, failures, measured };
  }

  const xml = fs.readFileSync(reportPath, "utf8");
  const counts = new Map();
  for (const [tag] of xml.matchAll(PACKAGE_TAG)) {
    const name = NAME_ATTR.exec(tag)?.[1];
    const rate = LINE_RATE_ATTR.exec(tag)?.[1];
    if (name === undefined || rate === undefined) {
      failures.push("MALFORMED ENTRY: an assembly entry carries no name or no line rate: " + tag);
      continue;
    }
    const lineRate = Number(rate);
    if (!Number.isFinite(lineRate)) {
      failures.push("MALFORMED ENTRY: " + name + " carries a line rate that is not a number: " + rate);
      continue;
    }
    counts.set(name, (counts.get(name) ?? 0) + 1);
    measured.push({ assembly: name, lineRate });
  }

  if (measured.length === 0) {
    failures.push(
      "NOTHING MEASURED: " +
        reportPath +
        " holds no assembly entries, so this report is evidence of nothing — an unmeasured assembly is not a covered assembly.",
    );
    return { ok: false, failures, measured };
  }

  for (const name of [...counts.keys()].sort()) {
    const count = counts.get(name);
    if (count > 1) {
      failures.push(
        "DUPLICATE ENTRY: " +
          name +
          " appears " +
          String(count) +
          " times in " +
          reportPath +
          " — which rate applies is undecidable, so this is a failure rather than the last one silently winning.",
      );
    }
  }

  const rates = new Map(measured.map((entry) => [entry.assembly, entry.lineRate]));
  for (const { assembly, floor } of floors) {
    if (typeof floor !== "number" || !Number.isFinite(floor)) {
      failures.push(
        "UNSET FLOOR: " +
          assembly +
          " declares no numeric floor (" +
          JSON.stringify(floor) +
          ") — a floor is set from a measurement, never left as a placeholder.",
      );
      continue;
    }
    if (!rates.has(assembly)) {
      failures.push(
        "NOT MEASURED: " +
          assembly +
          " carries a floor of " +
          formatPercent(floor) +
          " but does not appear in " +
          reportPath +
          " — an unmeasured assembly is not a covered assembly.",
      );
      continue;
    }
    const lineRate = rates.get(assembly);
    if (lineRate < floor / 100) {
      failures.push(
        "BELOW FLOOR: " +
          assembly +
          " line rate " +
          formatPercent(lineRate * 100) +
          " is under its floor of " +
          formatPercent(floor) +
          " — the number is wrong, not the floor.",
      );
    }
  }

  return { ok: failures.length === 0, failures, measured };
}

// Percentages are printed with two decimals so a shortfall of a single hundredth of a percent is
// legible in the failure message rather than rounding away in the reader's copy of the number.
function formatPercent(value) {
  return value.toFixed(2) + "%";
}

// The committed file is the default and is what CI reads, so CI and a local run cannot be looking at
// different numbers. An explicit path exists so the gate's own tests can drive floors that do not
// change when the committed numbers are re-measured.
function loadFloors(file) {
  const data = JSON.parse(fs.readFileSync(file, "utf8"));
  if (!Array.isArray(data.lineRateFloors)) {
    throw new Error(file + " must declare a lineRateFloors array");
  }
  return data.lineRateFloors;
}

function catalogEntries() {
  const catalog = JSON.parse(fs.readFileSync(path.join(root, "extensions", "catalog.json"), "utf8"));
  return catalog.extensions;
}

// Two reports under one results directory mean one of them is stale, and reading either is a coin
// flip — so the ambiguity is reported instead of resolved by taking the newest. Clearing the results
// directory before collecting is the remedy, and the directory is version-control-ignored so a stale
// one can never reach a commit.
function findReports(dir) {
  if (!fs.existsSync(dir)) return [];
  const found = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) found.push(...findReports(full));
    else if (entry.name === REPORT_NAME) found.push(full);
  }
  return found.sort();
}

function resolveReport(entry) {
  if (!entry.testProjectPath) {
    return { error: entry.name + " declares no testProjectPath, so it emits no report to check." };
  }
  const dir = path.join(root, path.dirname(entry.testProjectPath), RESULTS_DIR);
  const reports = findReports(dir);
  if (reports.length === 0) return { path: path.join(dir, REPORT_NAME) };
  if (reports.length > 1) {
    return {
      error:
        entry.name +
        ": " +
        String(reports.length) +
        " reports under " +
        path.relative(root, dir) +
        " — one is stale and reading either would be a guess. Remove that directory and collect again:\n  " +
        reports.map((report) => path.relative(root, report)).join("\n  "),
    };
  }
  return { path: reports[0] };
}

function flagValue(argv, flag) {
  const index = argv.indexOf(flag);
  return index === -1 ? null : (argv[index + 1] ?? null);
}

function main(argv) {
  const only = flagValue(argv, "--extension");
  const explicitReport = flagValue(argv, "--report");
  const floors = loadFloors(flagValue(argv, "--floors") ?? FLOORS_FILE);
  const entries = catalogEntries();

  const names = [];
  for (const floor of floors) {
    if (only !== null && floor.extension !== only) continue;
    if (!names.includes(floor.extension)) names.push(floor.extension);
  }

  if (names.length === 0) {
    console.error(
      "coverage-floor: NO INPUT — no floor is declared" +
        (only === null ? "" : " for " + only) +
        ", so nothing was checked. A green here is not evidence that any assembly was measured.",
    );
    return 1;
  }

  let status = 0;
  for (const name of names) {
    const entry = entries.find((candidate) => candidate.name === name);
    if (entry === undefined) {
      console.error("coverage-floor: " + name + " names no catalog entry.");
      status = 1;
      continue;
    }
    const resolved = explicitReport === null ? resolveReport(entry) : { path: path.resolve(explicitReport) };
    if (resolved.error !== undefined) {
      console.error("coverage-floor: FAILED — " + resolved.error);
      status = 1;
      continue;
    }
    const applicable = floors.filter((floor) => floor.extension === name);
    const result = checkCoverageFloors({ reportPath: resolved.path, floors: applicable });
    const declared = new Set(applicable.map((floor) => floor.assembly));
    for (const { assembly, lineRate } of result.measured) {
      const label = declared.has(assembly) ? "floor" : "no floor declared";
      console.log(
        "coverage-floor: " + name + ": " + assembly + " " + formatPercent(lineRate * 100) + " (" + label + ")",
      );
    }
    if (!result.ok) {
      for (const failure of result.failures) console.error("coverage-floor: " + failure);
      status = 1;
      continue;
    }
    console.log("coverage-floor: " + name + ": OK — every declared floor met.");
  }
  return status;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(main(process.argv.slice(2)));
}
