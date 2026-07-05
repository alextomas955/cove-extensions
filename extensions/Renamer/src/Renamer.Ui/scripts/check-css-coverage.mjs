#!/usr/bin/env node
/**
 * CSS-coverage gate: every Cove semantic-colour token the panel's JSX references MUST actually
 * emit a rule in the built dist/index.css.
 *
 * Why this exists (the failure it prevents): the extension emits its own CSS via the Tailwind CLI
 * over src/index.css. Tailwind only generates a `bg-card` / `text-foreground` rule if the token
 * (`card`, `foreground`, …) is a known @theme colour at build time. Those are Cove's CUSTOM tokens,
 * not part of Tailwind's default palette — they exist only because src/index.css declares them in
 * its @theme block. If that block drifts (a token removed/renamed, or the panel starts using a new
 * host token nobody added), Tailwind silently emits NOTHING for that utility — no build error, no
 * lint error — and the panel renders unstyled for that colour (or worse, free-rides invisibly on
 * the host's own bundle). This guard turns that silent gap into a hard build failure by cross-
 * checking used-tokens (scanned from .tsx) against emitted-tokens (grepped from dist/index.css).
 *
 * It is deliberately NARROW: only Cove semantic colour tokens (the ones that must be declared in
 * @theme). Tailwind default-palette utilities (red-500, white, …) and non-colour utilities (flex,
 * pb-20, …) are out of scope — they either ship with Tailwind or fail visibly.
 */
import { readFileSync, readdirSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const UI_DIR = path.resolve(HERE, "..");
const SRC_DIR = path.join(UI_DIR, "src");
const DIST_CSS = path.join(UI_DIR, "dist", "index.css");

// The Cove semantic colour tokens the panel is allowed to use. These MUST stay in sync with the
// @theme block in src/index.css (and, upstream, with cove/ui/src/index.css). A used token outside
// this set is almost certainly a typo or a new host token that also needs adding to @theme —
// either way the guard should flag it rather than let it silently emit nothing.
const KNOWN_TOKENS = new Set([
  "background", "surface", "card", "card-hover", "border", "input",
  "foreground", "secondary", "muted", "accent", "accent-hover",
]);

// Colour-bearing utility prefixes that consume a --color-* token.
const COLOR_PREFIXES = [
  "bg", "text", "border", "ring", "from", "to", "via", "fill", "stroke",
  "divide", "outline", "decoration", "placeholder", "caret", "accent", "shadow",
];
// Match `<prefix>-<token>` anywhere in a className string, allowing a leading variant chain
// (hover:, md:, group-hover:, …) and an optional /opacity suffix, which do not change the token.
const TOKEN_RE = new RegExp(
  String.raw`\b(?:${COLOR_PREFIXES.join("|")})-([a-z]+(?:-[a-z]+)*)\b`,
  "g",
);

function tsxFiles(dir) {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory()
      ? tsxFiles(path.join(dir, e.name))
      : e.name.endsWith(".tsx")
        ? [path.join(dir, e.name)]
        : [],
  );
}

// 1. Collect the semantic tokens actually used in JSX.
const usedTokens = new Map(); // token -> first file it appears in
for (const full of tsxFiles(SRC_DIR)) {
  const text = readFileSync(full, "utf8");
  for (const m of text.matchAll(TOKEN_RE)) {
    const token = m[1];
    if (KNOWN_TOKENS.has(token) && !usedTokens.has(token)) {
      usedTokens.set(token, path.relative(SRC_DIR, full));
    }
  }
}

// 2. Read the built CSS. If it is missing, the build did not run — fail loudly.
let css;
try {
  css = readFileSync(DIST_CSS, "utf8");
} catch {
  console.error(
    `check-css-coverage: dist/index.css not found at ${DIST_CSS} — run \`npm run build\` first.`,
  );
  process.exit(1);
}

// 3. Every used token must resolve to var(--color-<token>) in the emitted CSS.
const missing = [];
for (const [token, file] of usedTokens) {
  if (!css.includes(`var(--color-${token})`)) {
    missing.push({ token, file });
  }
}

if (missing.length) {
  console.error("check-css-coverage: FAILED — themed utilities used in JSX emit NO rule in dist/index.css:");
  for (const { token, file } of missing) {
    console.error(
      `  --color-${token}  (used via a *-${token} class, first seen in src/${file})` +
        ` — add "--color-${token}" to the @theme block in src/index.css, then rebuild.`,
    );
  }
  console.error(
    "\nThis is the silent-unstyled-colour failure the @theme block guards against. See src/index.css header.",
  );
  process.exit(1);
}

console.log(
  `check-css-coverage: OK (${usedTokens.size} Cove colour token(s) used in JSX all emit as var(--color-*) in dist/index.css)`,
);
