#!/usr/bin/env node
// Advisory comment-hygiene gate. The comment policy (root CLAUDE.md) is "why-only" — no name
// restatement, no change-narrative, no consumer-behavior narration, no author voice. That is a
// judgment call no formatter or linter enforces, so this flags the common tells in the STAGED diff
// for the committer to review. Heuristic: with --warn it never blocks (mirrors sdk/docs-drift);
// without --warn a match exits non-zero.
//
// Scope: ADDED comment lines in staged *.cs / *.ts / *.tsx. It reads only what is being committed,
// so pre-existing comments are never re-flagged.

import { execSync } from "node:child_process";

const warnOnly = process.argv.includes("--warn");
// --working reads the uncommitted working tree instead of the staged diff, so comments can be audited mid-change
// (before anything is staged), not only at commit time.
const working = process.argv.includes("--working");

// Each tell is a phrasing the why-only rule forbids: consumer/behavior narration, justification /
// author voice, or change-narrative. Deliberately loose — a false positive costs one glance.
const TELLS = [
  { re: /\bso (?:the|it|we|a|callers?|the ui|the caller|nothing|an?)\b/i, why: "consumer/behavior narration" },
  { re: /\bso that\b/i, why: "consumer/behavior narration" },
  { re: /\b(?:rather than|instead of|worse than|for clarity|to be safe|as (?:mentioned|noted|above))\b/i, why: "justification / author voice" },
  { re: /\b(?:we (?:now|no longer)|no longer|used to|previously|now that we)\b/i, why: "change-narrative" },
  { re: /\b(?:defensively|for completeness|just in case)\b/i, why: "justification" },
  { re: /^not (?:a|an|the|just|only)\b/i, why: "justification by naming the alternative" },
  { re: /\b(?:this milestone|the milestone|milestones?|sprint|backlog|GSD|user stor(?:y|ies)|planning phase)\b/i, why: "process/workflow jargon" },
];

const DIFF = `git diff${working ? "" : " --cached"} --unified=0 --no-color -- '*.cs' '*.ts' '*.tsx'`;

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
for (const raw of execSync(DIFF, { encoding: "utf8" }).split("\n")) {
  if (raw.startsWith("+++ b/")) { file = raw.slice(6); continue; }
  const hunk = raw.match(/^@@ -\d+(?:,\d+)? \+(\d+)/);
  if (hunk) { line = Number(hunk[1]); continue; }
  if (raw.startsWith("+") && !raw.startsWith("+++")) {
    const text = commentText(raw);
    if (text) {
      const tell = TELLS.find((t) => t.re.test(text));
      if (tell) hits.push({ file, line, text, why: tell.why });
    }
    line++;
  } else if (!raw.startsWith("-")) {
    line++;
  }
}

if (hits.length === 0) process.exit(0);

console.error(`\ncomment-hygiene: ${hits.length} staged comment(s) look like narrative, not a why:`);
for (const h of hits) console.error(`  ${h.file}:${h.line}  [${h.why}]  ${h.text}`);
console.error("\nThe rule (CLAUDE.md): comment only a non-obvious WHY. Remove or reword — or keep it if it genuinely explains one.\n");
process.exit(warnOnly ? 0 : 1);
