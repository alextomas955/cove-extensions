#!/usr/bin/env node
// Advisory wire-reachability gate: registered endpoints and response-DTO fields that NOTHING consumes.
// The wire is a closed contract — every route and every response field exists to be read by a known caller — so
// an unread one is either dead surface or a missing UI. Neither is visible to a compiler, a linter or knip:
// the C# side compiles fine and the TS side never mentions the symbol at all.
//
// A route has three legitimate callers, all checked before it is reported:
//   - the UI bundle (a string literal the bundle fetches),
//   - the HOST (declared in the C# manifest as countEndpoint/apiEndpoint — Cove calls it, not the bundle),
//   - an external system (the inbound webhook, called by Whisparr).
// A field only matters here if it can reach the UI, so inbound provider models and internal port DTOs are
// excluded — those are parsed FROM the provider or passed between server layers.
//
// Findings are CANDIDATES, not verdicts. Two things it cannot know, both reported rather than assumed:
//   - a setting with no UI may be a deliberate "advanced / not exposed" one that the SERVER still reads,
//   - a symbol may be referenced only by tests or docs, which is why both are listed per finding.
// With --warn it never blocks; without, a finding exits non-zero.

import { readFileSync, readdirSync, statSync, existsSync } from "node:fs";
import { join, extname, dirname, resolve } from "node:path";

const warnOnly = process.argv.includes("--warn");
const root = resolve(dirname(new URL(import.meta.url).pathname), "..");

// The audited register of response fields that are working advanced options with no UI. Without it the
// candidate count could not reach zero: this check tells the reader to DOCUMENT such a field rather than
// delete it, and then went on counting the documented field forever — so the gate's own flip condition
// ("zero candidates on two consecutive commits") named an outcome no correct repo state could produce.
// A register entry is a classification, not a suppression: the field is still listed, under its own
// heading, and removing the option from the product must remove its entry here too.
const ADVANCED_NO_UI = new Map([
  [
    "ScanSummary.SchemaVersion",
    "Versions the persisted whole-library scan blob so a future reader can reject a shape it predates. Server-side only by design — nothing renders it.",
  ],
  [
    "OptionsSaveRequest.PathTranslation",
    "Cove↔Whisparr path rewriting for split-mount installs. Settable through the options endpoint; no settings-page control, because the mapping is wrong to guess and rare to need.",
  ],
  [
    "PathTranslationRule.CovePrefix",
    "One half of a PathTranslation rule — carried by the same advanced option, documented in docs/settings.md.",
  ],
  [
    "PathTranslationRule.WhisparrPrefix",
    "The other half of a PathTranslation rule — same option, same documentation.",
  ],
  [
    "OptionsView.TpdbEndpoint",
    "The ThePornDB endpoint the v2 identity match keys on. Overridable for a self-hosted mirror; the default is correct for every hosted instance, so no control exists.",
  ],
  [
    "OptionsView.WebhookHost",
    "The Cove origin persisted from the last webhook registration. Read back by the server to keep the URL stable; the page shows the built URL, never this field.",
  ],
]);

const SKIP_DIRS = new Set([
  "node_modules",
  "obj",
  "bin",
  "dist",
  ".git",
  "vendor",
  ".planning",
]);

function walk(dir, out = []) {
  if (!existsSync(dir)) return out;
  for (const entry of readdirSync(dir)) {
    if (SKIP_DIRS.has(entry)) continue;
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) walk(p, out);
    else out.push(p);
  }
  return out;
}

// Splits a record's parameter list on TOP-LEVEL commas only — a generic argument or an attribute carries its own
// commas, and a regex that consumes the delimiter silently drops every other parameter.
function recordParams(body) {
  const parts = [];
  let depth = 0;
  let cur = "";
  for (const ch of body) {
    if ("<([{".includes(ch)) depth++;
    else if (">)]}".includes(ch)) depth--;
    if (ch === "," && depth === 0) {
      parts.push(cur);
      cur = "";
    } else cur += ch;
  }
  parts.push(cur);
  return parts
    .map((raw) => raw.replace(/\[[^\]]*\]/g, " ").trim())
    .map((raw) => raw.split("=")[0].trim())
    .map((raw) => raw.split(/\s+/).filter(Boolean).pop() ?? "")
    .filter((name) => /^[A-Za-z_]\w*$/.test(name));
}

// Only records that can reach the UI: the wire Contracts unit, the redaction-safe options view, and the
// per-slice projections.
const RESPONSE_DTO_FILE =
  /(\/Contracts\/|WhisparrOptions\.cs$|SceneStatusModels\.cs$|EntityStatusProjection\.cs$|PreviewContracts\.cs$|RenamerPlan\.cs$)/;
// Inbound provider shapes and server-internal DTOs: never serialized to the UI, so absence there means nothing.
const INTERNAL_DTO =
  /^(Whisparr(Movie|Image|Performer|Episode|QueueRecord|HistoryRecord|ManualImportItem|FileSettings|NamingConfig)|Cove(Video|EntityIdentity|EntityRef|PerformerImage)|MetadataServerCandidate|ResolvedMetadataCredential|DiscoveryPage|MatchResult)$/;

function analyze(extensionDir) {
  const files = walk(join(root, extensionDir));
  const load = (pred) =>
    files
      .filter((p) => pred(p))
      .map((p) => ({ p, s: readFileSync(p, "utf8") }));
  const isTest = (p) => /\.Tests\//.test(p) || /\/e2e\//.test(p);

  const prodCs = load((p) => extname(p) === ".cs" && !isTest(p));
  const ui = load((p) => [".ts", ".tsx"].includes(extname(p)) && !isTest(p));
  const tests = load(
    (p) => isTest(p) && [".cs", ".mjs", ".ts", ".js"].includes(extname(p)),
  );
  const docs = load((p) => extname(p) === ".md");

  const text = (list) => list.map((f) => f.s).join("\n");
  const uiText = text(ui);
  const csText = text(prodCs);
  const testText = text(tests);
  const docText = text(docs);

  const routeConst = new Map();
  for (const { s } of prodCs) {
    for (const m of s.matchAll(
      /const string (\w+)\s*=\s*RouteBase\s*\+\s*"([^"]+)"/g,
    )) {
      routeConst.set(m[1], m[2].replace(/^\//, ""));
    }
  }
  const registered = new Map();
  for (const { s } of prodCs) {
    for (const m of s.matchAll(
      /endpoints\.Map(Get|Post|Put|Delete|Patch)\(\s*(\w+)/g,
    )) {
      const name = routeConst.get(m[2]);
      if (!name) continue;
      if (!registered.has(name)) registered.set(name, new Set());
      registered.get(name).add(m[1].toUpperCase());
    }
  }
  const hostDeclared = new Set();
  for (const m of csText.matchAll(
    /(?:countEndpoint|apiEndpoint)\s*:\s*RouteBase\s*\+\s*"\/([^"?]+)/g,
  )) {
    hostDeclared.add(m[1]);
  }

  const routes = [...registered.entries()]
    .map(([name, verbs]) => ({
      name,
      verbs: [...verbs].sort((a, b) => a.localeCompare(b)).join("+"),
      ui: new RegExp(`["'\`]${name}["'\`]`).test(uiText),
      host: hostDeclared.has(name),
      external: name === "webhook",
      tests: new RegExp(`["'\`]${name}["'\`]|/${name}\\b`).test(testText),
      docs: new RegExp(`/${name}\\b`).test(docText),
    }))
    .sort((a, b) => a.name.localeCompare(b.name));

  const fields = [];
  const seen = new Set();
  for (const { s } of prodCs.filter((f) => RESPONSE_DTO_FILE.test(f.p))) {
    for (const m of s.matchAll(
      /(?:internal|public)\s+sealed\s+record\s+(\w+)\s*\(([\s\S]*?)\)\s*(?:\{|;)/g,
    )) {
      const [, dto, body] = m;
      if (INTERNAL_DTO.test(dto)) continue;
      for (const field of recordParams(body)) {
        const key = `${dto}.${field}`;
        if (seen.has(key)) continue;
        seen.add(key);
        const camel = field.charAt(0).toLowerCase() + field.slice(1);
        const re = new RegExp(`\\b(${camel}|${field})\\b`);
        fields.push({
          dto,
          field,
          ui: re.test(uiText),
          // A field the SERVER still reads is a working setting with no UI, not dead surface.
          server: new RegExp(`\\b${field}\\b`).test(
            csText.replace(
              /(?:internal|public)\s+sealed\s+record[\s\S]*?\)\s*[;{]/g,
              "",
            ),
          ),
          tests: re.test(testText),
          docs: re.test(docText),
        });
      }
    }
  }

  return { routes, fields };
}

const catalog = JSON.parse(
  readFileSync(join(root, "extensions", "catalog.json"), "utf8"),
);
let findings = 0;
const seenFieldKeys = new Set();

for (const entry of catalog.extensions) {
  if (entry.manifestOnly === true) continue;
  const { routes, fields } = analyze(entry.path);
  for (const f of fields) seenFieldKeys.add(`${f.dto}.${f.field}`);
  const orphanRoutes = routes.filter((r) => !r.ui && !r.host && !r.external);
  const orphanFields = fields.filter((f) => !f.ui);
  const registered = orphanFields.filter((f) =>
    ADVANCED_NO_UI.has(`${f.dto}.${f.field}`),
  );
  const unregisteredFields = orphanFields.filter(
    (f) => !ADVANCED_NO_UI.has(`${f.dto}.${f.field}`),
  );

  console.log(
    `\n${entry.name}: ${routes.length} routes (${routes.filter((r) => r.ui).length} UI, ` +
      `${routes.filter((r) => r.host).length} host), ${fields.length} response fields`,
  );

  for (const r of orphanRoutes) {
    const also =
      [r.tests && "tests", r.docs && "docs"].filter(Boolean).join(" + ") ||
      "nothing";
    console.log(
      `  UNREACHABLE ROUTE  ${r.verbs.padEnd(8)} /${r.name.padEnd(24)} referenced only by: ${also}`,
    );
    findings++;
  }
  for (const f of registered) {
    console.log(
      `  REGISTERED advanced option        ${(f.dto + "." + f.field).padEnd(40)} ${ADVANCED_NO_UI.get(f.dto + "." + f.field)}`,
    );
  }
  for (const f of unregisteredFields) {
    // The name appearing in server code is evidence the value is still used, not proof: it can also be an
    // identically-named enum member or local. It separates "probably a working option with no UI" from
    // "unread anywhere", which is the judgement the reader needs.
    const kind = f.server
      ? "UNREAD BY UI, name used server-side"
      : "UNREAD ANYWHERE                   ";
    const also =
      [f.tests && "tests", f.docs && "docs"].filter(Boolean).join(" + ") ||
      "nothing";
    console.log(
      `  ${kind}  ${(f.dto + "." + f.field).padEnd(40)} referenced only by: ${also}`,
    );
    findings++;
  }
  if (orphanRoutes.length === 0 && unregisteredFields.length === 0)
    console.log(
      "  every route and response field is consumed or registered",
    );
}

// A register entry that matches nothing is the failure mode a register invites: the option was deleted
// or renamed and the classification outlived it, so the next reader trusts a line that describes nothing.
// This is always a hard failure — --warn covers the heuristic's judgement calls, not a stale register.
const stale = [...ADVANCED_NO_UI.keys()].filter((k) => !seenFieldKeys.has(k));
if (stale.length > 0) {
  console.log(
    `\ncheck-wire-usage: ${stale.length} stale ADVANCED_NO_UI entr(ies) matching no response field:`,
  );
  for (const k of stale) console.log(`  ${k}`);
  console.log("Remove the entry, or fix the name it no longer matches.");
  process.exit(1);
}

if (findings === 0) {
  console.log("\ncheck-wire-usage: OK");
  process.exit(0);
}
console.log(
  `\ncheck-wire-usage: ${findings} candidate(s). A field whose name is still used server-side may be a working ` +
    "advanced option with no UI — document it as such rather than deleting it.",
);
process.exit(warnOnly ? 0 : 1);
