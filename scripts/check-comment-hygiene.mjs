#!/usr/bin/env node
// Blocking gate for ONE clause of the comment policy (root CLAUDE.md): a shipped comment must not
// reference the development process that produced it — no workflow names, numbered phases, ticket
// ids, or planning-document pointers. A contributor reading the source, with only the source in
// front of them, should never hit a reference they have no way to resolve.
//
// It deliberately does NOT police whether a comment states a why rather than a what. That is the
// rest of the policy and it stays a human call in review; see the note above TELLS for the measured
// reason. Naming: the file predates the narrowing, and the gate id is referenced by CI, lefthook and
// CONTRIBUTING, so the name stayed while the scope shrank.
//
// Scope: ADDED comment lines in *.cs / *.ts / *.tsx / *.mjs / *.cjs. It reads only what a commit or
// range adds, so pre-existing comments are never re-flagged. With --warn it never blocks;
// without --warn a match exits non-zero.
//
// An empty diff is NOT a pass. Run outside a staged commit this gate used to exit 0 having inspected
// nothing, which is indistinguishable on the wire from a clean tree and was recorded as evidence at
// least twice. It now prints the size of what it read and treats an empty diff as NO INPUT: a hard
// failure, downgraded to a loud line under --warn so the pre-commit hook (which passes --warn, and
// carries no glob, so it fires on doc-only commits too) keeps working.

import { execSync } from "node:child_process";
import { readFileSync } from "node:fs";

const warnOnly = process.argv.includes("--warn");
// --working reads the uncommitted working tree instead of the staged diff, so comments can be audited mid-change
// (before anything is staged), not only at commit time.
const working = process.argv.includes("--working");
// --range A..B audits history, the only mode that can answer "what did this branch add" — the staged diff cannot.
const rangeArg = process.argv[process.argv.indexOf("--range") + 1];
const range = process.argv.includes("--range") ? rangeArg : null;
// A revision reaches a shell, so accept only rev-shaped text.
if (range !== null && !/^[A-Za-z0-9._/@^~-]+(\.\.\.?[A-Za-z0-9._/@^~-]+)?$/.test(range ?? "")) {
  console.error("comment-hygiene: --range needs a revision or revision range (e.g. main..HEAD)");
  process.exit(2);
}

// The base of a range is a LOCAL branch name that a CI checkout does not necessarily have: the default
// actions/checkout is shallow and single-branch, so `main` resolves on a developer's clone and not on
// the runner. Fall back to the remote-tracking ref, and if neither resolves, exit 2 — an unresolvable
// base must not degrade into an empty diff, which would read as a clean tree and green the gate on
// exactly the history it was meant to inspect.
const resolvedRange = (() => {
  if (range === null) return null;
  const [base, ...rest] = range.split(/(\.\.\.?)/).filter((p) => p && !p.startsWith("."));
  const sep = range.includes("...") ? "..." : "..";
  const head = rest[0] ?? "HEAD";
  const resolves = (rev) => {
    try {
      execSync(`git rev-parse --verify --quiet ${rev}^{commit}`, { stdio: "ignore" });
      return true;
    } catch {
      return false;
    }
  };
  if (resolves(base)) return range;
  if (resolves(`origin/${base}`)) return `origin/${base}${sep}${head}`;
  console.error(
    `comment-hygiene: cannot resolve '${base}' (tried 'origin/${base}' too). ` +
      "In CI give the checkout fetch-depth: 0 so the base branch exists.",
  );
  process.exit(2);
})();

// What this gate does NOT do: judge whether a comment states a why rather than a what. That judgment
// is not mechanically decidable, and the attempt was measured on this repo — every prose tell was
// either dead (0 hits over the whole tree) or wrong about half the time. "the folder a file used to
// live in" is domain vocabulary; "these keys used to be UNMODELED, so a blob saved then still loads"
// is a genuine why; Renamer's "planning phase" is its own plan-vs-execute phase. A blocking gate that
// flags those teaches people to delete good comments to get past it. The literature agrees: automated
// comment-quality scoring reaches only moderate agreement with human judgment and belongs in review,
// not in a merge gate.
//
// What it DOES do: refuse a project-internal reference a reader outside the project cannot resolve —
// a ticket id, a numbered phase, a planning-doc filename, a process noun. That is a closed vocabulary
// and a structural token shape, so a match is a fact rather than an inference, which is the only kind
// of finding that may block. Comment CONTENT quality stays a human call in review.
const TELLS = [
  // A ticket/requirement id. Anchored on a following possessive or verb so a hyphenated technical
  // term (AGPL-3.0, SHA-256, RFC-822) and sample data (ABC-1, SG-042) cannot match.
  { re: /\([A-Z]{2,12}-\d{1,3}\)|\b[A-Z]{2,12}-\d{1,3}(?:'s\b|\s+(?:says|requires|covers|mandates))/, why: "ticket id a reader cannot resolve" },
  // Requires the NUMBER: bare "phase" is domain vocabulary here (Renamer plans, then executes).
  { re: /\bphase[\s-]+\d+\b|\bwave[\s-]+\d+\b/i, why: "numbered planning phase" },
  // The demonstrative form always means the development phase: Renamer's own phases are NAMED
  // (PHASE A / PHASE B), and the definite article does have domain use here ("the message names the
  // phase"), so the demonstrative is the only form of the bare noun that is decidable.
  { re: /\bthis phase\b/i, why: "process/workflow jargon" },
  // A planning artifact by filename — these leaked as trailing "see X" pointers.
  { re: /\b\d{2}-[A-Z][A-Z-]{2,}\.md\b|\b(?:UI-SPEC|WIRE-DELTA|CONTENT-STRATEGY|PIPELINE-VALIDATION)\b|\bRESEARCH\.md\b/, why: "planning-doc reference" },
  // Process nouns. "planning phase" is deliberately absent — it collides with Renamer's own domain.
  { re: /\b(?:GSD|sprint|backlog|user stor(?:y|ies)|this milestone|the milestone|acceptance criteri)\b/i, why: "process/workflow jargon" },
];

const mode = range !== null ? `range ${resolvedRange}` : working ? "working tree" : "staged";
const selector = range !== null ? ` ${resolvedRange}` : working ? "" : " --cached";
// .mjs/.cjs are in scope alongside the product languages: the repo's build, lint and e2e-harness
// config is authored in them, and that is precisely where planning-doc pointers accumulated while
// this gate watched only the product tiers.
//
// The build and workflow tiers are in scope too, but they are read WHOLE-FILE rather than by added
// line (see CONFIG_GLOBS below): an XML block comment's offending sentence is usually a continuation
// line carrying no marker, so a --unified=0 added line cannot be classified as comment-or-not on its
// own. Markdown is deliberately excluded — a .md file is prose end to end, so "comment" is not a
// decidable subset of it and the gate would be judging documentation instead.
const SOURCE_GLOBS = ["*.cs", "*.ts", "*.tsx", "*.mjs", "*.cjs"];
const CONFIG_GLOBS = ["*.csproj", "*.props", "*.targets", "*.yml", "*.yaml"];
const DIFF = `git diff${selector} --unified=0 --no-color -- ${SOURCE_GLOBS.map((g) => `'${g}'`).join(" ")}`;
const CONFIG_DIFF = `git diff${selector} --name-only --no-color -- ${CONFIG_GLOBS.map((g) => `'${g}'`).join(" ")}`;

function commentText(added) {
  const t = added.replace(/^\+/, "").trim();
  if (t.startsWith("///") || t.startsWith("//")) return t.replace(/^\/{2,3}/, "").trim();
  if (t.startsWith("*") || t.startsWith("/*")) return t.replace(/^\/?\*+\/?/, "").trim();
  const inline = t.match(/\s\/\/\s?(.+)$/);
  return inline ? inline[1].trim() : null;
}

let file = null;
let line = 0;
const hits = [];
const filesSeen = new Set();
let commentLines = 0;
const diff = execSync(DIFF, { encoding: "utf8", maxBuffer: 256 * 1024 * 1024 });
for (const raw of diff.split("\n")) {
  if (raw.startsWith("+++ b/")) { file = raw.slice(6); filesSeen.add(file); continue; }
  const hunk = raw.match(/^@@ -\d+(?:,\d+)? \+(\d+)/);
  if (hunk) { line = Number(hunk[1]); continue; }
  if (raw.startsWith("+") && !raw.startsWith("+++")) {
    const text = commentText(raw);
    if (text) {
      commentLines++;
      const tell = TELLS.find((t) => t.re.test(text));
      if (tell) hits.push({ file, line, text, why: tell.why });
    }
    line++;
  } else if (!raw.startsWith("-")) {
    line++;
  }
}

// Config tiers, read whole-file. Only files this diff touched are read, so the gate still answers
// "what did this change introduce" — but within such a file every comment is inspected, because the
// sentence that carries a process reference is often a continuation line the diff shows as bare text.
const configFiles = execSync(CONFIG_DIFF, { encoding: "utf8" }).split("\n").filter(Boolean);
let configComments = 0;
for (const configFile of configFiles) {
  let body;
  try {
    body = readFileSync(configFile, "utf8");
  } catch {
    continue; // deleted or renamed away in this range — nothing to read
  }
  filesSeen.add(configFile);

  const isXml = /\.(?:csproj|props|targets)$/.test(configFile);
  const lines = body.split(/\r?\n/);
  let inXmlComment = false;
  lines.forEach((raw, index) => {
    let text = null;
    if (isXml) {
      const opens = raw.includes("<!--");
      const closes = raw.includes("-->");
      if (opens || inXmlComment) {
        text = raw.replace(/<!--/, "").replace(/-->.*$/, "").trim();
      }
      inXmlComment = (opens && !closes) || (inXmlComment && !closes);
    } else {
      const hash = raw.match(/^\s*#\s?(.*)$/);
      if (hash) text = hash[1].trim();
    }

    if (!text) return;
    configComments++;
    const tell = TELLS.find((t) => t.re.test(text));
    if (tell) hits.push({ file: configFile, line: index + 1, text, why: tell.why });
  });
}

const scanned =
  `${mode}: ${filesSeen.size} file(s), ${commentLines} added comment line(s)` +
  `, ${configComments} config comment line(s)`;

if (diff.trim() === "" && configFiles.length === 0) {
  console.error(`comment-hygiene: NO INPUT — ${mode} diff over the source globs is empty, so nothing was inspected.`);
  console.error("A green here is not evidence of clean comments. Use --range A..B to audit history.");
  process.exit(warnOnly ? 0 : 1);
}

if (hits.length === 0) {
  console.log(`comment-hygiene: OK — read ${scanned}, 0 flagged`);
  process.exit(0);
}

console.error(`\ncomment-hygiene: read ${scanned}; ${hits.length} look like narrative, not a why:`);
for (const h of hits) console.error(`  ${h.file}:${h.line}  [${h.why}]  ${h.text}`);
console.error("\nThe rule (CLAUDE.md): comment only a non-obvious WHY. Remove or reword — or keep it if it genuinely explains one.\n");
process.exit(warnOnly ? 0 : 1);
