#!/usr/bin/env node
/**
 * XSS gate for the panel sources.
 *
 * Fails (exit 1) if any panel .tsx uses the raw-HTML React prop (dangerouslySetInnerHTML):
 * filenames/diff/flags must render as escaped text nodes only, never as raw HTML, to avoid an
 * injection vector.
 */
const fs = require("node:fs");
const path = require("node:path");

const SRC_DIR = path.resolve(__dirname, "..", "src");

// Scan ALL .tsx sources (not a hardcoded list — so new components like RenamePage.tsx are covered too).
function tsxFiles(dir) {
  return fs
    .readdirSync(dir, { withFileTypes: true })
    .flatMap((e) =>
      e.isDirectory()
        ? tsxFiles(path.join(dir, e.name))
        : e.name.endsWith(".tsx")
          ? [path.join(dir, e.name)]
          : [],
    );
}
const PANEL_FILES = tsxFiles(SRC_DIR);

// The raw-HTML React prop — banned as actual JSX usage (`dangerouslySetInnerHTML=` or `:`), but NOT when
// it merely appears in a comment/doc string (e.g. "NO dangerouslySetInnerHTML"). Render escaped only.
const RAW_HTML_RE = /dangerouslySetInnerHTML\s*[:=]/;

let failed = false;

for (const full of PANEL_FILES) {
  const file = path.relative(SRC_DIR, full);
  const text = fs.readFileSync(full, "utf8");
  if (RAW_HTML_RE.test(text)) {
    console.error(`FORBIDDEN raw-HTML prop (dangerouslySetInnerHTML) used in src/${file}`);
    failed = true;
  }
}

if (failed) {
  console.error("check-classes: FAILED");
  process.exit(1);
}
console.log("check-classes: OK (no raw-HTML rendering)");
