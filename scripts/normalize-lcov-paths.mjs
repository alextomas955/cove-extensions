// Rewrites each extension's lcov record paths to be relative to the repository root.
//
// Vitest writes an `SF:` path relative to the directory its config sits in, so a UI package's own
// file is recorded as `src/settings/Foo.tsx` and the shared package it pulls in as
// `..\..\..\..\shared\ui-shared\src\overlay.ts`. SonarQube resolves an lcov path against the
// repository root, where the first form matches by luck and the second matches nothing: those files
// then report nought per cent rather than as unmeasured, and the number looks like a coverage gap
// instead of a wiring fault. Separators are normalised in the same pass, because a report written on
// Windows carries backslashes that no POSIX scanner resolves.
//
// Catalog-driven, so an extension declaring a uiPath is covered here with no edit.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const root = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(root, "extensions", "catalog.json");
const REPORT_SUBPATH = "coverage/lcov.info";

export function normalizeLcov(text, uiPath) {
  let rewritten = 0;
  const lines = text.split(/\r?\n/).map((line) => {
    if (!line.startsWith("SF:")) return line;
    rewritten += 1;
    const recorded = line.slice(3).trim().split("\\").join("/");
    const absolute = path.posix.resolve("/", uiPath.split(path.sep).join("/"), recorded);
    return `SF:${absolute.slice(1)}`;
  });
  return { text: lines.join("\n"), rewritten };
}

export function readUiPaths(catalogText) {
  return JSON.parse(catalogText)
    .extensions.filter((entry) => entry.uiPath)
    .map((entry) => entry.uiPath);
}

function main() {
  const uiPaths = readUiPaths(fs.readFileSync(catalogPath, "utf8"));
  let reports = 0;

  for (const uiPath of uiPaths) {
    const reportPath = path.join(root, uiPath, ...REPORT_SUBPATH.split("/"));
    if (!fs.existsSync(reportPath)) {
      console.error(`ERROR: ${uiPath} declares a frontend but wrote no ${REPORT_SUBPATH}.`);
      process.exit(1);
    }
    const { text, rewritten } = normalizeLcov(fs.readFileSync(reportPath, "utf8"), uiPath);
    if (rewritten === 0) {
      console.error(`ERROR: ${reportPath} holds no SF records, so it measured nothing.`);
      process.exit(1);
    }
    fs.writeFileSync(reportPath, text);
    console.log(`${uiPath}: rewrote ${rewritten} record path(s) in ${REPORT_SUBPATH}`);
    reports += 1;
  }

  // A silent nought here would hand the scanner no coverage at all and still exit clean.
  if (reports === 0) {
    console.error("ERROR: no catalog entry declares a uiPath, so no coverage report was found.");
    process.exit(1);
  }
}

// `import.meta.main`, matching assemble-package.mjs: the hand-rolled comparison against
// process.argv[1] answers "no" when the script is reached through a junction, and this script would
// then rewrite nothing while exiting 0, handing the scanner unresolvable paths.
if (typeof import.meta.main !== "boolean") {
  console.error(
    `normalize-lcov-paths: this Node (${process.version}) does not implement import.meta.main, so this script cannot tell it was run rather than imported and would rewrite nothing while exiting 0. Node 22.18 or newer is required to run it.`,
  );
  process.exit(1);
}

if (import.meta.main) main();
