#!/usr/bin/env node
/**
 * Class-discipline + XSS gate for the panel sources.
 *
 * Fails (exit 1) if any panel .tsx contains:
 *   1. A Tailwind utility class the host does NOT emit — those would silently render unstyled,
 *      because the host's Tailwind JIT never scans this bundle (it only generates classes it sees
 *      in its own source). Arbitrary-value classes (e.g. `w-[123px]`) are the common trap.
 *   2. The raw-HTML React prop (dangerouslySetInnerHTML) — filenames/diff/flags must render as
 *      escaped text nodes only, never as raw HTML, to avoid an injection vector.
 *
 * The rejected literals live ONLY in this script (the FORBIDDEN array). They must not appear in
 * the panel sources — this script greps for them.
 */
const fs = require("node:fs");
const path = require("node:path");

const SRC_DIR = path.resolve(__dirname, "..", "src");
// The shared UI module holds the field primitives this bundle consumes; class-discipline + XSS must
// hold over them here too (this bundle renders them), so its src/ is scanned alongside this one's.
const SHARED_SRC_DIR = path.resolve(__dirname, "..", "..", "..", "..", "..", "shared", "cove-extensions-ui", "src");

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
const PANEL_FILES = [...tsxFiles(SRC_DIR), ...tsxFiles(SHARED_SRC_DIR)];

// Host-absent Tailwind classes (verified 0× in cove-ui).
const FORBIDDEN = [
  "focus-visible:ring-1",
  "focus-visible:ring-accent",
  "lg:top-4",
];

// The raw-HTML React prop — banned as actual JSX usage (`dangerouslySetInnerHTML=` or `:`), but NOT when
// it merely appears in a comment/doc string (e.g. "NO dangerouslySetInnerHTML"). Render escaped only.
const RAW_HTML_RE = /dangerouslySetInnerHTML\s*[:=]/;

// Arbitrary-value Tailwind utilities like `grid-cols-[1.4fr_1fr]` / `w-[300px]` render NO css because the
// host Tailwind JIT never scans this bundle (Phase-9 finding). Flag any `prefix-[...]` arbitrary utility
// appearing inside a className string literal. Allows bracket usage OUTSIDE className (e.g. TS index types).
const ARBITRARY_CLASS_RE = /\b[a-z][a-z0-9:-]*-\[[^\]]+\]/g;

// Exception: an arbitrary value IS host-emitted when Cove core's OWN source uses it verbatim (the host
// Tailwind JIT scans core, so the class ships in wwwroot's stylesheet). Re-implemented primitives that
// mirror core may use these to match it exactly. Each entry must be verified present in the host CSS.
const HOST_EMITTED_ARBITRARY = new Set([
  // core `components/SettingsPrimitives.tsx` SettingsSection card shadow (verified in wwwroot CSS).
  "shadow-[0_12px_30px_-20px_rgba(0,0,0,0.7)]",
]);
function classNameLiterals(text) {
  // crude but effective: capture the string contents of className="..." and className={`...`}
  const out = [];
  const dq = /className\s*=\s*"([^"]*)"/g;
  const tpl = /className\s*=\s*\{`([^`]*)`\}/g;
  let m;
  while ((m = dq.exec(text))) out.push(m[1]);
  while ((m = tpl.exec(text))) out.push(m[1]);
  return out;
}

let failed = false;

for (const full of PANEL_FILES) {
  const file = path.relative(SRC_DIR, full);
  const text = fs.readFileSync(full, "utf8");
  for (const bad of FORBIDDEN) {
    if (text.includes(bad)) {
      console.error(`FORBIDDEN "${bad}" found in src/${file}`);
      failed = true;
    }
  }
  if (RAW_HTML_RE.test(text)) {
    console.error(`FORBIDDEN raw-HTML prop (dangerouslySetInnerHTML) used in src/${file}`);
    failed = true;
  }
  for (const cls of classNameLiterals(text)) {
    const hits = (cls.match(ARBITRARY_CLASS_RE) ?? []).filter((h) => !HOST_EMITTED_ARBITRARY.has(h));
    if (hits.length) {
      console.error(`ARBITRARY Tailwind class ${JSON.stringify(hits)} in src/${file} — host JIT won't emit it; use a standard utility.`);
      failed = true;
    }
  }
}

if (failed) {
  console.error("check-classes: FAILED");
  process.exit(1);
}
console.log("check-classes: OK (no host-absent classes, no raw-HTML rendering)");
