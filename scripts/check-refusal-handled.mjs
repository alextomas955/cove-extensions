#!/usr/bin/env node
// Class gate: can a mutating discovery call be added to the Missing tab without a rejection handler?
//
// The bug class it exists for: a control posts a mutation, the server refuses with a classified 400, the promise
// rejects, and the click handler discards it. Nothing renders. The rejection reaches nothing at all — the host's
// error alert is a mutation option on its own action-handler dispatch, reached only for a registered bulk action
// handler, and neither the vendored SDK nor the host UI installs a global rejection handler. Every offline gate
// stays green, because no gate can see a promise nobody awaited. Seven call sites shipped that way.
//
// SCOPE IS DELIBERATELY NARROW: the discovery slice's own mutating action routes, and the one tab that calls them.
// A mutating route in another slice needs its own entry here. A gate that tried to classify every store call would
// report the read paths as unhandled mutations and be switched off within a week, which is worth less than a
// narrow gate that keeps working.
//
// Method, stated so a reader can judge what it can and cannot know:
//   - DISCOVER the governed set instead of listing it: read the discovery store, take every exported function
//     whose body references a route literal matching MUTATING_ROUTE, and govern exactly those. A read function
//     that also POSTs (the catalogue read) references a different route and is not governed. An eighth mutation
//     added tomorrow is governed the moment it names one of these routes;
//   - blank out comments and string/template literals first, preserving offsets, so neither a comment nor a
//     string can create a match or hide one;
//   - for every call of a governed function in the tab, take the whole enclosing statement by depth-aware
//     scanning (a `;` nested inside a callback body does not end it) and require a rejection handler in it.
//
// A handler one indirection away — a shared runner taking the call as a callback — reads as unhandled here, and
// that is intended: what a reader checks at the call site is what this gate checks.
//
// AN EMPTY INPUT IS A FAILURE, never a pass. A gate whose glob opened nothing exits 0 having inspected nothing,
// which is indistinguishable on the wire from a clean tree; that shipped once already in this repo. Zero routes,
// zero governed functions, zero call sites and a missing file each fail.
//
// --root <dir> overrides the scan root so a pre-change tree can be measured without touching the working tree.
// --warn downgrades every failure to advisory.

import { readFileSync, existsSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * `--root <dir>` points the scan at a materialised pre-change tree; `--warn` downgrades every failure.
 * The default root goes through fileURLToPath rather than a URL's raw pathname, which would keep the
 * percent-encoding of any repo path containing a space.
 */
function readFlags(argv) {
  const at = argv.indexOf("--root");
  const override = at === -1 ? null : argv[at + 1];
  return {
    warnOnly: argv.includes("--warn"),
    root: override
      ? resolve(override)
      : fileURLToPath(new URL("..", import.meta.url)),
  };
}

const { warnOnly, root } = readFlags(process.argv.slice(2));

const UI = "extensions/WhisparrSync/src/WhisparrSync.Ui/src";
const STORE = join(root, UI, "discovery/missingStore.ts");
const TAB = join(root, UI, "discovery/WhisparrMissingTab.tsx");

// The discovery slice's mutating action routes. The catalogue read (`discovery/entity`) is deliberately outside it.
const MUTATING_ROUTE = /^discovery\/action(-all)?$/;
// What counts as handling a rejection at the call site.
const REJECTION_HANDLER = /\.catch\s*\(/;

/** Replace every comment and string/template literal with spaces, preserving every offset. */
function blankNonCode(src) {
  const out = src.split("");
  let i = 0;
  const blank = (from, to) => {
    for (let k = from; k < to && k < out.length; k++) {
      if (out[k] !== "\n") out[k] = " ";
    }
  };
  while (i < src.length) {
    const two = src.slice(i, i + 2);
    if (two === "//") {
      const end = src.indexOf("\n", i);
      const stop = end === -1 ? src.length : end;
      blank(i, stop);
      i = stop;
    } else if (two === "/*") {
      const end = src.indexOf("*/", i + 2);
      const stop = end === -1 ? src.length : end + 2;
      blank(i, stop);
      i = stop;
    } else if (src[i] === '"' || src[i] === "'" || src[i] === "`") {
      const quote = src[i];
      let k = i + 1;
      while (k < src.length) {
        if (src[k] === "\\") k += 2;
        else if (src[k] === quote) break;
        else k++;
      }
      blank(i + 1, k);
      i = k + 1;
    } else {
      i++;
    }
  }
  return out.join("");
}

/** Index of the matching `)` for the `(` at `openIdx`, or -1. */
function parenEnd(src, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < src.length; i++) {
    if (src[i] === "(") depth++;
    else if (src[i] === ")" && --depth === 0) return i;
  }
  return -1;
}

/**
 * Index of a function's BODY `{`, skipping a braced return-type annotation. `): Promise<{ ok: boolean }> {` puts a
 * brace before the body, and taking the first one read the type as the whole function and governed nothing in it.
 */
function bodyStart(src, afterParams) {
  let angle = 0;
  for (let i = afterParams; i < src.length; i++) {
    const ch = src[i];
    if (ch === "<") angle++;
    else if (ch === ">" && angle > 0) angle--;
    else if (ch === "{") {
      if (angle === 0) return i;
      const skip = bodyEnd(src, i);
      if (skip === -1) return -1;
      i = skip - 1;
    } else if (ch === ";") {
      return -1;
    }
  }
  return -1;
}

/** Index just past the brace matching the `{` at `openIdx`, or -1. */
function bodyEnd(src, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < src.length; i++) {
    if (src[i] === "{") depth++;
    else if (src[i] === "}" && --depth === 0) return i + 1;
  }
  return -1;
}

function lineAt(src, idx) {
  return src.slice(0, idx).split("\n").length;
}

/** The whole statement containing `idx`, by depth-aware scanning in both directions. */
function statementAround(src, idx) {
  let depth = 0;
  let start = 0;
  for (let i = idx; i >= 0; i--) {
    const ch = src[i];
    if (ch === ")" || ch === "}" || ch === "]") depth++;
    else if (ch === "(" || ch === "[") depth--;
    else if (ch === "{") depth--;
    if (depth < 0) {
      start = i + 1;
      break;
    }
    if (depth === 0 && (ch === ";" || ch === "{" || ch === "}")) {
      start = i + 1;
      break;
    }
  }
  depth = 0;
  let end = src.length;
  for (let i = idx; i < src.length; i++) {
    const ch = src[i];
    if (ch === "(" || ch === "{" || ch === "[") depth++;
    else if (ch === ")" || ch === "}" || ch === "]") depth--;
    if (depth <= 0 && ch === ";") {
      end = i + 1;
      break;
    }
  }
  return { start, end, text: src.slice(start, end) };
}

const missing = [];
if (!existsSync(STORE)) missing.push(STORE);
if (!existsSync(TAB)) missing.push(TAB);
if (missing.length > 0) {
  console.error(
    `check-refusal-handled: NO INPUT — ${missing.length} governed file(s) absent under ${root}:`,
  );
  for (const f of missing) console.error(`  ${f}`);
  console.error(
    "A green here would say nothing about a tree this gate never opened.",
  );
  process.exit(warnOnly ? 0 : 1);
}

const storeSrc = blankNonCode(readFileSync(STORE, "utf8"));
const storeRaw = readFileSync(STORE, "utf8");

// The route literals must be read from the RAW source (blanking removed them), then located in the blanked source
// so a route named inside a comment cannot be counted.
const routes = new Set();
for (const m of storeRaw.matchAll(/api\(\s*["'`]([^"'`]+)["'`]\s*\)/g)) {
  if (MUTATING_ROUTE.test(m[1])) routes.add(m[1]);
}
// Offsets of every mutating-route reference that is real code (the api( call survives blanking; its argument does
// not, so the call's own offset is what places it inside a function body).
const routeOffsets = [...storeSrc.matchAll(/api\(\s*["'`]\s*["'`]\s*\)/g)]
  .map((m) => m.index)
  .filter((idx) => {
    const literal = /api\(\s*["'`]([^"'`]*)["'`]\s*\)/.exec(
      storeRaw.slice(idx, idx + 200),
    );
    return literal !== null && MUTATING_ROUTE.test(literal[1]);
  });

const governed = [];
for (const m of storeSrc.matchAll(
  /export\s+(?:async\s+)?function\s+([A-Za-z0-9_$]+)\s*\(/g,
)) {
  const params = parenEnd(storeSrc, m.index + m[0].length - 1);
  if (params === -1) continue;
  const open = bodyStart(storeSrc, params + 1);
  if (open === -1) continue;
  const end = bodyEnd(storeSrc, open);
  if (end === -1) continue;
  if (routeOffsets.some((idx) => idx > open && idx < end)) {
    governed.push({ name: m[1], line: lineAt(storeSrc, m.index) });
  }
}

const tabSrc = blankNonCode(readFileSync(TAB, "utf8"));
const sites = [];
for (const { name } of governed) {
  for (const m of tabSrc.matchAll(new RegExp(`\\b${name}\\s*\\(`, "g"))) {
    const stmt = statementAround(tabSrc, m.index);
    sites.push({
      name,
      line: lineAt(tabSrc, m.index),
      handled: REJECTION_HANDLER.test(stmt.text),
    });
  }
}
sites.sort((a, b) => a.line - b.line);
const unhandled = sites.filter((s) => !s.handled);

const rel = (p) => p.slice(root.length + 1);
for (const s of unhandled) {
  console.log(
    `unhandled discovery mutation  ${rel(TAB)}:${s.line}  ${s.name}() discards its rejection`,
  );
}
console.log(
  `check-refusal-handled: routes=${routes.size} governed=${governed.length} sites=${sites.length} handled=${sites.length - unhandled.length}`,
);
console.log(`  routes:   ${[...routes].sort().join(", ") || "(none)"}`);
console.log(
  `  governed: ${
    governed
      .map((g) => g.name)
      .sort()
      .join(", ") || "(none)"
  }`,
);

const empties = [];
if (routes.size === 0)
  empties.push("no mutating action route found in the discovery store");
if (governed.length === 0)
  empties.push("no exported function references one of those routes");
if (sites.length === 0)
  empties.push("no call of a governed function found in the discovery tab");
for (const e of empties)
  console.error(`check-refusal-handled: NO INPUT — ${e}`);
if (unhandled.length > 0) {
  console.error(
    "A rejected mutation with no handler at its call site renders nothing: the host's error alert belongs to its\n" +
      "action-handler dispatch, and no global rejection handler exists. Compose the line and place it at the control.",
  );
}

if ((unhandled.length > 0 || empties.length > 0) && !warnOnly) process.exit(1);
