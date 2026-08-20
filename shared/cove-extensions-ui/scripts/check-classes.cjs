#!/usr/bin/env node
/**
 * Class-discipline + XSS gate for an extension's panel sources — one shared copy for every UI.
 *
 * Fails (exit 1) if any panel .tsx contains:
 *   1. A Tailwind utility class the host does NOT emit — those would silently render unstyled,
 *      because the host's Tailwind JIT never scans this bundle (it only generates classes it sees
 *      in its own source). Arbitrary-value classes (e.g. `w-[123px]`) are the common trap.
 *   2. The raw-HTML React prop (dangerouslySetInnerHTML) — filenames/diff/flags must render as
 *      escaped text nodes only, never as raw HTML, to avoid an injection vector.
 *
 * The consuming package's `src/` and the shared UI module's `src/` are both scanned (this bundle
 * renders the shared primitives, so class-discipline + XSS must hold over them here too).
 *
 * Usage:
 *   node check-classes.cjs [--allow <class>]... [--src <dir>]
 *
 * `--allow` whitelists an arbitrary-value class that IS host-emitted because Cove core's own source
 * uses it verbatim (the host Tailwind JIT scans core, so the class ships in wwwroot's stylesheet);
 * a re-implemented primitive that mirrors core may use it to match exactly. Each allowed entry must
 * be verified present in the host CSS. `--src` overrides the scanned package src (default `<cwd>/src`).
 */
const fs = require("node:fs");
const path = require("node:path");

function parseArgs(argv) {
  const allow = [];
  let src = null;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--allow") allow.push(argv[++i]);
    else if (argv[i] === "--src") src = argv[++i];
    else throw new Error(`check-classes: unknown argument ${argv[i]}`);
  }
  return { allow, src };
}

const { allow, src } = parseArgs(process.argv.slice(2));

const SRC_DIR = src ? path.resolve(src) : path.resolve(process.cwd(), "src");
// The shared UI module holds the field primitives every bundle consumes; resolved from this script's
// own location so it is correct regardless of which package invoked the shared copy.
const SHARED_SRC_DIR = path.resolve(__dirname, "..", "src");

// Scan ALL .tsx sources (not a hardcoded list — so new components are covered too).
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
const FORBIDDEN = ["focus-visible:ring-1", "focus-visible:ring-accent", "lg:top-4"];

// The raw-HTML React prop — banned as actual JSX usage (`dangerouslySetInnerHTML=` or `:`), but NOT when
// it merely appears in a comment/doc string (e.g. "NO dangerouslySetInnerHTML"). Render escaped only.
const RAW_HTML_RE = /dangerouslySetInnerHTML\s*[:=]/;

// Arbitrary-value Tailwind utilities like `grid-cols-[1.4fr_1fr]` / `w-[300px]` render NO css because the
// host Tailwind JIT never scans this bundle. Flag any `prefix-[...]` arbitrary utility appearing inside a
// className string literal. Allows bracket usage OUTSIDE className (e.g. TS index types).
const ARBITRARY_CLASS_RE = /\b[a-z][a-z0-9:-]*-\[[^\]]+\]/g;

// Per the `--allow` flag: arbitrary values that ARE host-emitted (verified present in the host CSS).
const HOST_EMITTED_ARBITRARY = new Set(allow);

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
    const hits = (cls.match(ARBITRARY_CLASS_RE) ?? []).filter(
      (h) => !HOST_EMITTED_ARBITRARY.has(h),
    );
    if (hits.length) {
      console.error(
        `ARBITRARY Tailwind class ${JSON.stringify(hits)} in src/${file} — host JIT won't emit it; use a standard utility.`,
      );
      failed = true;
    }
  }
}

if (failed) {
  console.error("check-classes: FAILED");
  process.exit(1);
}
console.log("check-classes: OK (no host-absent classes, no raw-HTML rendering)");
