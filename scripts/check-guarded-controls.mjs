#!/usr/bin/env node
// Class gate: can a control be added to a guarded surface with its own name replaced by the refusal reason?
//
// The bug class it exists for: `aria-label={configReason}` on a disabled control. `aria-label` REPLACES an
// element's accessible name, so a screen reader announces a thirty-word paragraph of advice and never says which
// control it belongs to; hover still works, every offline gate stays green, and nothing about the rendered text
// looks wrong to a sighted reviewer. Ten controls shipped across three different treatments — replace the name,
// append to it, and put it on an ancestor span where it is no part of the button's accessible name at all.
//
// Method, stated so a reader can judge what it can and cannot know:
//   - DISCOVER the governed set instead of listing it: a component is governed if it consults the shared
//     configuration-health hook, or if it disables a control on an identifier that names a reason. An eighth
//     surface is governed the moment it does either, without an edit here;
//   - blank out comments and string/template literals first, preserving offsets, so neither a comment nor a
//     string can create a match or hide one. Import specifiers ARE string literals, so those are read from the
//     raw source before blanking;
//   - a REGISTRY of governed file → guarded-control count, with a total, is compared against what was found. A
//     new guarded surface fails until it is both listed here and composing through the shared helper, which is
//     the property that cannot be satisfied by silence.
//
// What it cannot know: whether the name a caller passes is the right one. It checks that a name is composed, not
// that the words are good.
//
// AN EMPTY INPUT IS A FAILURE, never a pass. A glob that opened nothing exits 0 having inspected nothing, which
// is indistinguishable on the wire from a clean tree; that shipped once already in this repo.
//
// --root <dir> overrides the scan root so a pre-change tree can be measured without touching the working tree.
// --warn downgrades every failure to advisory.

import { readFileSync, existsSync, readdirSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

function readFlags(argv) {
  const at = argv.indexOf("--root");
  const override = at === -1 ? null : argv[at + 1];
  return {
    warnOnly: argv.includes("--warn"),
    root: override ? resolve(override) : fileURLToPath(new URL("..", import.meta.url)),
  };
}

const { warnOnly, root } = readFlags(process.argv.slice(2));

const UI_SRC = join(root, "extensions/WhisparrSync/src/WhisparrSync.Ui/src");

const CONFIG_HEALTH_IMPORT = /from\s+["'][^"']*common\/lib\/configHealthStore["']/;
const GUARD_HELPER_IMPORT = /from\s+["'][^"']*common\/lib\/refusalAffordanceLogic["']/;
const GUARDED_CONTROL_CALL = /\bguardedControl\s*\(/g;

// The governed set and how many controls each surface composes. A count that moves is a control added or removed.
const REGISTRY = {
  "batch/WhisparrBatchChooser.tsx": 1,
  "discovery/MissingSceneCard.tsx": 1,
  "discovery/MissingSelectionBar.tsx": 1,
  "entity/WhisparrEntityBatchChooser.tsx": 1,
  "monitor/WhisparrMenu.tsx": 3,
  "scene/WhisparrScenePanel.tsx": 3,
  "settings/SyncLibrarySection.tsx": 1,
};
const EXPECTED_TOTAL = Object.values(REGISTRY).reduce((a, b) => a + b, 0);

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

function tsxFiles(dir) {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory()
      ? tsxFiles(join(dir, e.name))
      : e.name.endsWith(".tsx")
        ? [join(dir, e.name)]
        : [],
  );
}

function lineAt(src, idx) {
  return src.slice(0, idx).split("\n").length;
}

/** The balanced `{ … }` expression starting at the `{` at `openIdx`, without its braces. */
function braceExpr(src, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < src.length; i++) {
    if (src[i] === "{") depth++;
    else if (src[i] === "}" && --depth === 0) {
      return { text: src.slice(openIdx + 1, i), end: i };
    }
  }
  return null;
}

/** The opening tag containing `idx`, so an attribute can be checked against its own element. */
function openingTagAround(src, idx) {
  let start = -1;
  for (let i = idx; i >= 0; i--) {
    if (src[i] === "<" && /[A-Za-z]/.test(src[i + 1] ?? "")) {
      start = i;
      break;
    }
  }
  if (start === -1) return "";
  let depth = 0;
  for (let i = start; i < src.length; i++) {
    if (src[i] === "{") depth++;
    else if (src[i] === "}") depth--;
    else if (src[i] === ">" && depth === 0) return src.slice(start, i + 1);
  }
  return src.slice(start);
}

const IDENT = /^[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*$/;
const NAMES_A_REASON = /(?:[Rr]eason|[Rr]efusal)$/;

/** Whether an expression is exactly one identifier whose last segment names a reason. */
function isBareReason(expr) {
  const text = expr.trim();
  if (!IDENT.test(text)) return false;
  return NAMES_A_REASON.test(text.split(".").pop());
}

/** Whether an expression mentions any reason-naming identifier at all. */
function mentionsReason(expr) {
  for (const m of expr.matchAll(/[A-Za-z_$][A-Za-z0-9_$]*/g)) {
    if (NAMES_A_REASON.test(m[0])) return true;
  }
  return false;
}

/** Every `attr={…}` expression in a file, with its offset. */
function attributeExpressions(src, attr) {
  const found = [];
  for (const m of src.matchAll(new RegExp(`${attr}\\s*=\\s*\\{`, "g"))) {
    const open = m.index + m[0].length - 1;
    const expr = braceExpr(src, open);
    if (expr !== null) found.push({ text: expr.text, at: m.index });
  }
  return found;
}

const findings = [];
const governed = [];
let totalControls = 0;

for (const file of tsxFiles(UI_SRC).sort()) {
  const raw = readFileSync(file, "utf8");
  const src = blankNonCode(raw);
  const rel = relative(UI_SRC, file).split(sep).join("/");

  const disabledExprs = attributeExpressions(src, "disabled");
  const consultsConfigHealth = CONFIG_HEALTH_IMPORT.test(raw) && /useConfigHealth/.test(src);
  const disablesOnAReason = disabledExprs.some((e) => mentionsReason(e.text));
  if (!consultsConfigHealth && !disablesOnAReason) continue;

  const controls = (src.match(GUARDED_CONTROL_CALL) ?? []).length;
  governed.push({ rel, controls });
  totalControls += controls;

  if (!GUARD_HELPER_IMPORT.test(raw) || !/guardedControl/.test(src)) {
    findings.push(`${rel}  consults the guard but composes its own affordance`);
  }

  for (const { text, at } of attributeExpressions(src, "aria-label")) {
    const consequent = /\?([^:]*):/.exec(text);
    const coalesced = /^([^?]*)\?\?/.exec(text);
    const bare =
      isBareReason(text) ||
      (coalesced !== null && isBareReason(coalesced[1])) ||
      (consequent !== null && isBareReason(consequent[1]));
    if (bare) {
      findings.push(
        `${rel}:${lineAt(src, at)}  aria-label is the bare reason — it replaces the control's name`,
      );
    }
  }

  for (const { text, at } of disabledExprs) {
    if (!mentionsReason(text)) continue;
    if (!/aria-label/.test(openingTagAround(src, at))) {
      findings.push(
        `${rel}:${lineAt(src, at)}  disabled for a reason on an element with no accessible name`,
      );
    }
  }
}

for (const f of findings) console.log(`guarded control  ${f}`);

console.log(
  `check-guarded-controls: governed=${governed.length} controls=${totalControls} findings=${findings.length}`,
);
for (const g of governed) console.log(`  ${g.rel}  ${String(g.controls)}`);

const empties = [];
if (!existsSync(UI_SRC)) empties.push(`no UI source tree under ${UI_SRC}`);
if (governed.length === 0) empties.push("no guarded surface found — the glob opened nothing");
if (totalControls === 0) empties.push("no composed guarded control found on any governed surface");

// The registry is the half a discovery pass cannot supply: it says what SHOULD be there, so a surface that
// vanished, or one that appeared, is a failure rather than a quietly different number.
const registryMismatches = [];
for (const [rel, expected] of Object.entries(REGISTRY)) {
  const found = governed.find((g) => g.rel === rel);
  if (found === undefined) registryMismatches.push(`${rel}  registered but no longer a guarded surface`);
  else if (found.controls !== expected)
    registryMismatches.push(
      `${rel}  ${String(found.controls)} guarded control(s), registry says ${String(expected)}`,
    );
}
for (const g of governed) {
  if (!(g.rel in REGISTRY)) {
    registryMismatches.push(`${g.rel}  is a guarded surface and is not in the registry`);
  }
}
if (totalControls !== EXPECTED_TOTAL && governed.length > 0) {
  registryMismatches.push(
    `total is ${String(totalControls)}, registry total is ${String(EXPECTED_TOTAL)}`,
  );
}

for (const m of registryMismatches) console.error(`check-guarded-controls: REGISTRY — ${m}`);
for (const e of empties) console.error(`check-guarded-controls: NO INPUT — ${e}`);
if (findings.length > 0) {
  console.error(
    "A reason belongs behind the control's own name, not in place of it. Compose disabled/title/aria-label\n" +
      "through guardedControl in common/lib/refusalAffordanceLogic.",
  );
}
if (registryMismatches.length > 0) {
  console.error(
    "Add the surface to the registry in this file AND compose its controls through the shared helper — a new\n" +
      "guarded control must not be able to arrive with no entry and no composition.",
  );
}

const failed = findings.length > 0 || empties.length > 0 || registryMismatches.length > 0;
if (failed && !warnOnly) process.exit(1);
