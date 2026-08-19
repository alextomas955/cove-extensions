#!/usr/bin/env node
// Advisory comment-hygiene gate. The comment policy (root CLAUDE.md) is "why-only" — no name
// restatement, no change-narrative, no consumer-behavior narration, no author voice. That is a
// judgment call no formatter or linter enforces, so this flags the common tells in the STAGED diff
// for the committer to review. Heuristic: with --warn it never blocks (mirrors sdk-drift);
// without --warn a match exits non-zero.
//
// Scope: ADDED comment lines in staged *.cs / *.ts / *.tsx. It reads only what is being committed,
// so pre-existing comments are never re-flagged.
//
// An empty diff is NOT a pass. Run outside a staged commit this gate used to exit 0 having inspected
// nothing, which is indistinguishable on the wire from a clean tree and was recorded as evidence at
// least twice. It now prints the size of what it read and treats an empty diff as NO INPUT: a hard
// failure, downgraded to a loud line under --warn so the pre-commit hook (which passes --warn, and
// carries no glob, so it fires on doc-only commits too) keeps working.

import { execSync } from "node:child_process";

const warnOnly = process.argv.includes("--warn");
// --working reads the uncommitted working tree instead of the staged diff, so comments can be audited mid-change
// (before anything is staged), not only at commit time.
const working = process.argv.includes("--working");
// --range A..B audits history, the only mode that can answer "what did this branch add" — the staged diff cannot.
const rangeArg = process.argv[process.argv.indexOf("--range") + 1];
const range = process.argv.includes("--range") ? rangeArg : null;
// A revision reaches a shell, so accept only rev-shaped text.
if (range !== null && !/^[A-Za-z0-9._/@^~-]+$/.test(range)) {
  console.error("comment-hygiene: --range needs a revision or revision range (e.g. main..HEAD)");
  process.exit(2);
}

// Each tell is a phrasing the why-only rule forbids: consumer/behavior narration, justification /
// author voice, or change-narrative. Deliberately loose — a false positive costs one glance.
const TELLS = [
  {
    re: /\bso (?:the|it|we|a|callers?|the ui|the caller|nothing|an?)\b/i,
    why: "consumer/behavior narration",
  },
  { re: /\bso that\b/i, why: "consumer/behavior narration" },
  {
    re: /\b(?:rather than|instead of|worse than|for clarity|to be safe|as (?:mentioned|noted|above))\b/i,
    why: "justification / author voice",
  },
  {
    re: /\b(?:we (?:now|no longer)|no longer|used to|previously|now that we)\b/i,
    why: "change-narrative",
  },
  { re: /\b(?:defensively|for completeness|just in case)\b/i, why: "justification" },
  { re: /^not (?:a|an|the|just|only)\b/i, why: "justification by naming the alternative" },
  {
    re: /\b(?:this milestone|the milestone|milestones?|sprint|backlog|GSD|user stor(?:y|ies)|planning phase)\b/i,
    why: "process/workflow jargon",
  },
  // A numbered phase or a bare requirement id is the same jargon wearing a code. Both shipped past the
  // rule above because it only matched prose words: "Phase 57 GATE-PROMOTE" and "(CI-02's own
  // requirement)" reached a released workflow, and a reader outside the project cannot resolve either.
  { re: /\bphase\s+\d+\b/i, why: "numbered planning phase" },
  {
    re: /\([A-Z]{2,12}-\d{1,3}\)|\b[A-Z]{2,12}-\d{1,3}(?:'s\b|\s+(?:says|requires|covers))/,
    why: "requirement id",
  },
];

const mode = range !== null ? `range ${range}` : working ? "working tree" : "staged";
const selector = range !== null ? ` ${range}` : working ? "" : " --cached";
const DIFF = `git diff${selector} --unified=0 --no-color -- "*.cs" "*.ts" "*.tsx"`;

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
  if (raw.startsWith("+++ b/")) {
    file = raw.slice(6);
    filesSeen.add(file);
    continue;
  }
  const hunk = raw.match(/^@@ -\d+(?:,\d+)? \+(\d+)/);
  if (hunk) {
    line = Number(hunk[1]);
    continue;
  }
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

const scanned = `${mode}: ${filesSeen.size} file(s), ${commentLines} added comment line(s)`;

if (diff.trim() === "") {
  console.error(
    `comment-hygiene: NO INPUT — ${mode} diff over *.cs/*.ts/*.tsx is empty, so nothing was inspected.`,
  );
  console.error(
    "A green here is not evidence of clean comments. Use --range A..B to audit history.",
  );
  process.exit(warnOnly ? 0 : 1);
}

if (hits.length === 0) {
  console.log(`comment-hygiene: OK — read ${scanned}, 0 flagged`);
  process.exit(0);
}

console.error(`\ncomment-hygiene: read ${scanned}; ${hits.length} look like narrative, not a why:`);
for (const h of hits) console.error(`  ${h.file}:${h.line}  [${h.why}]  ${h.text}`);
console.error(
  "\nThe rule (CLAUDE.md): comment only a non-obvious WHY. Remove or reword — or keep it if it genuinely explains one.\n",
);
process.exit(warnOnly ? 0 : 1);
