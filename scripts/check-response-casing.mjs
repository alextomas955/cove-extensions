#!/usr/bin/env node
// Casing gate for camelCase-wire RESPONSE shapes: does any type used as a response shape declare a
// property whose name starts with an uppercase letter?
//
// The bug class it exists for: this wire is asymmetric on purpose — request bodies are PascalCase,
// responses are camelCase. A response type declared with the request's casing still typechecks,
// because the interface is DECLARED rather than inferred from the wire, so every field silently
// reads undefined at runtime. Neither tsc, nor eslint, nor check-wire-usage.mjs can see it:
// check-wire-usage only asks whether a field NAME appears somewhere in the UI text, which says
// nothing about casing.
//
// SCOPE IS DELIBERATELY NOT REPO-WIDE — it is per-extension, because one extension can serve BOTH
// casings. Renamer does: its response endpoints serialize through PreviewResponseJsonOptions
// (CoveJsonOptions.WebWithEnumStrings(), i.e. JsonSerializerDefaults.Web = camelCase), while its
// settings blob rides Cove's generic extension-data store and is legitimately PascalCase — which is
// why Renamer.Ui/src/settings/options.ts is PascalCase on purpose. The gate only inspects types
// reached through request<…>/postAction<…> generics, so it sees the response wire and not the
// options blob. A repo-wide rule would cry wolf on the latter.
//
// Method, stated so a reader can judge what it can and cannot know:
//   - walk .ts/.tsx under each policy-listed extension's UI src/, skipping node_modules, dist, vendor;
//   - find every request<…>( and postAction<…>( call, capturing the generic by BALANCED angle-bracket
//     scanning — a flat regex breaks on Record<string, X>;
//   - resolve that type expression to a property list, recursively: split it on top-level | and &
//     and inspect EVERY member, strip [] and wrapping parens, then per member take an inline object
//     literal, or hop to a named declaration (interface or type alias, generic or not) in this file,
//     through a relative import, or through the repo's one UI path alias;
//   - a keyless expression (unknown, void, a literal, a primitive) declares no property, so it
//     cannot carry a PascalCase key and is not a blind spot;
//   - anything else is UNRESOLVED, and unresolved FAILS the gate.
//
// Walking every union member is the whole point of the split. A union whose first member is braced
// used to be truncated to that member and counted resolved-and-clean, so `A | { Id, Name }` passed
// on member order alone — and two shipped response types have exactly that shape.
//
// Unresolved fails rather than warns because every generic in this tree resolves today, so the
// count is a ratchet at zero: a new unreachable shape is a gate blindness to close, not a fact to
// tolerate. It is the same reasoning as the union fix — a guard that cannot see an input must say
// so in its exit code, or its green means "no findings among the ones I could read".
//
// THE UNIT OF `findings` IS ONE OFFENDING PROPERTY KEY — not one file and not one type. A single
// two-field interface therefore contributes two findings.
//
// Exits non-zero on any finding or any unresolved generic; --warn downgrades to advisory. --root
// <dir> overrides the scan root so the gate can be run against a copy of a pre-change tree.

import { readFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { join, extname, dirname, resolve } from "node:path";

const argv = process.argv.slice(2);
const warnOnly = argv.includes("--warn");
const rootFlag = argv.indexOf("--root");
const root =
  rootFlag !== -1 && argv[rootFlag + 1]
    ? resolve(argv[rootFlag + 1])
    : resolve(dirname(new URL(import.meta.url).pathname), "..");

// Extensions whose Cove-facing responses serialize through JsonSerializerDefaults.Web (camelCase).
const POLICY = ["extensions/Renamer"];

const SKIP_DIRS = new Set(["node_modules", "dist", "vendor", "obj", "bin", ".git"]);

function walk(dir, out = []) {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) walk(p, out);
    else if ([".ts", ".tsx"].includes(extname(p))) out.push(p);
  }
  return out;
}

/** The generic argument starting at the `<` index, by balanced scanning; null if unterminated. */
function balancedGeneric(src, openIdx) {
  let depth = 0;
  for (let i = openIdx; i < src.length; i++) {
    const ch = src[i];
    if (ch === "<") depth++;
    else if (ch === ">") {
      depth--;
      if (depth === 0) return src.slice(openIdx + 1, i);
    }
  }
  return null;
}

/** Property keys of an inline object-literal type body, ignoring nested braces. */
function keysOfBody(body) {
  const keys = [];
  let depth = 0;
  let atKeyPosition = true;
  let cur = "";
  for (const ch of body) {
    if (ch === "{" || ch === "(" || ch === "[" || ch === "<") depth++;
    else if (ch === "}" || ch === ")" || ch === "]" || ch === ">") depth--;
    if (depth === 0 && (ch === ";" || ch === ",")) {
      atKeyPosition = true;
      cur = "";
      continue;
    }
    if (depth === 0 && ch === ":") {
      if (atKeyPosition) {
        const name = cur
          .trim()
          .replace(/\?$/, "")
          .replace(/^readonly\s+/, "");
        if (/^[A-Za-z_]\w*$/.test(name)) keys.push(name);
      }
      atKeyPosition = false;
      cur = "";
      continue;
    }
    cur += ch;
  }
  return keys;
}

/** Split a type expression on its top-level `|` and `&`, so every member of a union is reachable. */
function unionMembers(expr) {
  const parts = [];
  let depth = 0;
  let cur = "";
  for (const ch of expr) {
    if (ch === "{" || ch === "(" || ch === "[" || ch === "<") depth++;
    else if (ch === "}" || ch === ")" || ch === "]" || ch === ">") depth--;
    if (depth === 0 && (ch === "|" || ch === "&")) {
      parts.push(cur);
      cur = "";
      continue;
    }
    cur += ch;
  }
  parts.push(cur);
  return parts.map((p) => p.trim()).filter((p) => p !== "");
}

/**
 * The declared type EXPRESSION for `name` in `src` — an interface's `{…}` body wrapped in braces, or
 * a type alias's whole right-hand side (not just a leading object literal). Null if not declared here.
 */
function declaredType(src, name) {
  const iface = new RegExp(
    `\\binterface\\s+${name}\\b\\s*(?:<[^{]*>)?\\s*(?:extends[^{]*)?\\{`,
  ).exec(src);
  if (iface) {
    const open = iface.index + iface[0].length - 1;
    let depth = 0;
    for (let i = open; i < src.length; i++) {
      if (src[i] === "{") depth++;
      else if (src[i] === "}" && --depth === 0) return src.slice(open, i + 1);
    }
    return null;
  }
  const alias = new RegExp(`\\btype\\s+${name}\\b\\s*(?:<[^=]*>)?\\s*=\\s*`).exec(src);
  if (!alias) return null;
  // A type alias's RHS runs to the first depth-0 `;` — prettier always terminates one.
  const start = alias.index + alias[0].length;
  let depth = 0;
  for (let i = start; i < src.length; i++) {
    const ch = src[i];
    if (ch === "{" || ch === "(" || ch === "[" || ch === "<") depth++;
    else if (ch === "}" || ch === ")" || ch === "]" || ch === ">") depth--;
    else if (ch === ";" && depth === 0) return src.slice(start, i);
  }
  return null;
}

/** The module specifier an `import type { name }`/`import { name }` in `src` pulls `name` from. */
function importSourceFor(src, name) {
  const re = /import\s+(?:type\s+)?\{([^}]*)\}\s*from\s*["']([^"']+)["']/g;
  let m;
  while ((m = re.exec(src))) {
    const named = m[1].split(",").map((s) =>
      s
        .replace(/^\s*type\s+/, "")
        .split(/\s+as\s+/)[0]
        .trim(),
    );
    if (named.includes(name)) return m[2];
  }
  return null;
}

// The UI bundles resolve this one package from raw TS source through a Vite alias rather than
// node_modules, so following it is a source hop like any relative import.
const ALIASES = [["@cove-extensions/ui-shared", "shared/cove-extensions-ui/src"]];

function resolveSpec(fromFile, spec) {
  let base;
  if (spec.startsWith(".")) {
    base = resolve(dirname(fromFile), spec);
  } else {
    const hit = ALIASES.find(([pkg]) => spec === pkg || spec.startsWith(`${pkg}/`));
    if (!hit) return null;
    base = join(root, hit[1], spec.slice(hit[0].length));
  }
  for (const cand of [base, `${base}.ts`, `${base}.tsx`, join(base, "index.ts")]) {
    if (existsSync(cand) && statSync(cand).isFile()) return cand;
  }
  return null;
}

// These declare no properties at all, so they cannot carry a PascalCase key. Not a blind spot.
const KEYLESS =
  /^(?:unknown|void|never|any|null|undefined|string|number|boolean|true|false|\d|["'`])/;

/**
 * Where `name` is declared, following imports and `export *` / `export { … } from` barrels — the
 * shared UI package is consumed through one, so a hop that stops at the barrel reads nothing.
 * Returns the declaration's type expression with the file and source it came from, or null.
 */
function lookupNamed(name, file, src, depth = 0) {
  if (depth > 6) return null;
  const own = declaredType(src, name);
  if (own !== null) return { expr: own, file, src };

  const direct = importSourceFor(src, name) ?? reExportSourceFor(src, name);
  const specs = direct !== null ? [direct] : starExportSpecs(src);
  for (const spec of specs) {
    const hop = resolveSpec(file, spec);
    if (!hop) continue;
    const hit = lookupNamed(name, hop, readFileSync(hop, "utf8"), depth + 1);
    if (hit) return hit;
  }
  return null;
}

/** The module an `export { name } from "…"` re-exports `name` from. */
function reExportSourceFor(src, name) {
  const re = /export\s+(?:type\s+)?\{([^}]*)\}\s*from\s*["']([^"']+)["']/g;
  let m;
  while ((m = re.exec(src))) {
    const named = m[1].split(",").map((s) =>
      s
        .replace(/^\s*type\s+/, "")
        .split(/\s+as\s+/)
        .pop()
        .trim(),
    );
    if (named.includes(name)) return m[2];
  }
  return null;
}

/** Every `export * from "…"` specifier, the barrel form that names nothing. */
function starExportSpecs(src) {
  const out = [];
  const re = /export\s+\*\s*from\s*["']([^"']+)["']/g;
  let m;
  while ((m = re.exec(src))) out.push(m[1]);
  return out;
}

/**
 * Every property key reachable from a type expression, walking all union/intersection members and
 * hopping through named declarations. Returns the keys found and the members it could not reach.
 */
function collectKeys(expr, file, src, seen = new Set(), depth = 0) {
  const keys = [];
  const unreached = [];
  if (depth > 12) return { keys, unreached: [expr] };

  for (const raw of unionMembers(expr)) {
    let m = raw.trim();
    while (m.startsWith("(") && balancedSpan(m, 0, "(", ")") === m.length - 1)
      m = m.slice(1, -1).trim();
    m = m
      .replace(/(?:\s*\[\s*\])+$/, "")
      .replace(/^readonly\s+/, "")
      .trim();
    if (m === "") continue;

    if (m.startsWith("{")) {
      const close = balancedSpan(m, 0, "{", "}");
      if (close === -1) unreached.push(raw.trim());
      else
        keys.push(...keysOfBody(m.slice(1, close)).map((k) => ({ typeName: "<inline>", key: k })));
      continue;
    }
    if (KEYLESS.test(m)) continue;

    // A bare name or a generic application; the type arguments carry no keys of the named type.
    const named = /^([A-Za-z_]\w*)\s*(?:<.*>)?$/s.exec(m);
    if (!named) {
      unreached.push(raw.trim());
      continue;
    }
    const name = named[1];
    const found = lookupNamed(name, file, src);
    if (found === null) {
      unreached.push(raw.trim());
      continue;
    }
    const { expr: body, file: target, src: targetSrc } = found;
    const key = `${target}::${name}`;
    if (seen.has(key)) continue;
    seen.add(key);
    const inner = collectKeys(body, target, targetSrc, seen, depth + 1);
    keys.push(
      ...inner.keys.map((k) => ({
        typeName: k.typeName === "<inline>" ? name : k.typeName,
        key: k.key,
      })),
    );
    unreached.push(...inner.unreached);
  }
  return { keys, unreached };
}

/** Index of the bracket closing the one at `openIdx`, or -1. */
function balancedSpan(s, openIdx, open, close) {
  let depth = 0;
  for (let i = openIdx; i < s.length; i++) {
    if (s[i] === open) depth++;
    else if (s[i] === close && --depth === 0) return i;
  }
  return -1;
}

/** Line number (1-based) of a character offset. */
function lineAt(src, idx) {
  return src.slice(0, idx).split("\n").length;
}

const findings = [];
const unresolved = [];
let filesScanned = 0;
let resolvedCount = 0;
let membersInspected = 0;

for (const policyDir of POLICY) {
  for (const file of walk(join(root, policyDir))) {
    const src = readFileSync(file, "utf8");
    if (!/\b(?:request|postAction)\s*</.test(src)) continue;
    filesScanned++;
    const callRe = /\b(request|postAction)\s*</g;
    let m;
    while ((m = callRe.exec(src))) {
      const openIdx = m.index + m[0].length - 1;
      const generic = balancedGeneric(src, openIdx);
      if (generic === null) continue;
      const rel = file.slice(root.length + 1);
      const line = lineAt(src, m.index);
      const trimmed = generic.trim();
      const { keys, unreached } = collectKeys(trimmed, file, src);

      membersInspected += keys.length;
      for (const { typeName, key } of keys) {
        if (/^[A-Z]/.test(key)) findings.push(`${rel}:${line}  ${typeName}.${key}`);
      }
      // A generic counts as resolved only when EVERY member was reached — a partial read is the
      // failure mode this gate had.
      if (unreached.length > 0) {
        for (const u of unreached)
          unresolved.push(`${rel}:${line}  ${trimmed}  (unreachable member: ${u})`);
      } else {
        resolvedCount++;
      }
    }
  }
}

for (const f of findings) console.log(`PascalCase response key  ${f}`);
for (const u of unresolved) console.log(`unresolved generic       ${u}`);
console.log(
  `check-response-casing: files=${filesScanned} resolved=${resolvedCount} unresolved=${unresolved.length} keys=${membersInspected} findings=${findings.length}`,
);
if (unresolved.length > 0) {
  console.log(
    "An unresolved generic is a shape this gate cannot read, so its silence says nothing about it.",
  );
}

if ((findings.length > 0 || unresolved.length > 0) && !warnOnly) process.exit(1);
