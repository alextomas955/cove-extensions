/**
 * Behavior contract for the pure folder-overlap logic. The manifest runner (run-logic-gate.mjs, driven by this
 * package's gates.json entry) compiles folderOverlapLogic.ts and passes the compiled module URL via
 * FOLDER_OVERLAP_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.FOLDER_OVERLAP_LOGIC_MODULE);
const {
  doubledPathDisplay,
  sceneFolderOverlapSummary,
  folderOverlapFromServer,
  hasFolderOverlapAdvisory,
  rootContainmentSummary,
  notCheckedSummary,
  notApplicableSummary,
  NOT_CHECKED_SUMMARY,
} = mod;

const ONE = { root: "/data/media/scenes", prefix: "scenes", suggestedRoot: "/data/media" };

// A shared root is a HEADS-UP, not a fault: the live layout has one deliberately (both systems see one volume),
// so wording that reads as a failure would be a new way for the interface to mislead. Declared here rather than
// grepped from the repo so the assertion is about this sentence, not about the codebase.
const READS_AS_A_FAULT = /\b(error|failed|failure|broken|invalid|wrong|misconfigur\w*|urgent|must)\b/i;

// Any wording that pushes the user between generations. v2 and v3 are both first-class.
const IMPLIES_MIGRATION = /\b(upgrade|migrat\w+|switch to|move to v\d|newer version)\b/i;

const CONTAINMENT = {
  kind: "rootContainment",
  whisparrRoot: "/data/media",
  coveRoot: "/data/media/scenes",
};
const FORMAT = { kind: "sceneFolderFormat", ...ONE };
const REASONS = ["notConfigured", "readFailed", "coveRootsUnknown", "unsupportedVersion"];

test("hasFolderOverlapAdvisory: true for a finding, true for an abstention, false for a real all-clear", () => {
  assert.equal(
    hasFolderOverlapAdvisory({ checked: true, reason: null, findings: [CONTAINMENT], notApplicable: [] }),
    true,
  );
  assert.equal(
    hasFolderOverlapAdvisory({ checked: false, reason: "readFailed", findings: [], notApplicable: [] }),
    true,
  );
  assert.equal(
    hasFolderOverlapAdvisory({ checked: true, reason: null, findings: [], notApplicable: [] }),
    false,
  );
});

test("doubledPathDisplay: renders <root>/<prefix>/…", () => {
  assert.equal(doubledPathDisplay(ONE), "/data/media/scenes/scenes/…");
});

test("sceneFolderOverlapSummary: names the doubled path and the suggested-root fix", () => {
  const s = sceneFolderOverlapSummary(ONE);
  assert.ok(s.includes("/data/media/scenes/scenes/…"), "contains the doubled path");
  assert.ok(s.includes("/data/media"), "contains the suggestedRoot literal");
  assert.ok(s.includes("scenes"), "names the doubled segment");
  assert.match(s, /In Whisparr/); // guidance the user applies in Whisparr, not a command to the extension
});

test("folderOverlapFromServer: a rootContainment entry parses with both paths", () => {
  const out = folderOverlapFromServer({
    checked: true,
    reason: null,
    findings: [CONTAINMENT],
    notApplicable: [],
  });
  assert.equal(out.checked, true);
  assert.equal(out.reason, null);
  assert.deepEqual(out.findings, [CONTAINMENT]);
  assert.deepEqual(out.notApplicable, []);
});

test("folderOverlapFromServer: a sceneFolderFormat entry keeps the shipped three-field shape", () => {
  const out = folderOverlapFromServer({
    checked: true,
    reason: null,
    findings: [FORMAT],
    notApplicable: [],
  });
  assert.deepEqual(out.findings, [FORMAT]);
  // The shipped display helpers still read it, so the kind tag is additive rather than a reshape.
  assert.equal(doubledPathDisplay(out.findings[0]), "/data/media/scenes/scenes/…");
});

test("folderOverlapFromServer: both kinds and a notApplicable list ride one answer", () => {
  const out = folderOverlapFromServer({
    checked: true,
    reason: null,
    findings: [CONTAINMENT, FORMAT],
    notApplicable: ["sceneFolderFormat"],
  });
  assert.equal(out.findings.length, 2);
  assert.deepEqual(out.notApplicable, ["sceneFolderFormat"]);
});

test("folderOverlapFromServer: an unrecognised kind is dropped, never rendered blank", () => {
  const out = folderOverlapFromServer({
    checked: true,
    reason: null,
    findings: [CONTAINMENT, { kind: "somethingNew", root: "/x" }, { whisparrRoot: "/a", coveRoot: "/b" }],
    notApplicable: [],
  });
  assert.deepEqual(out.findings, [CONTAINMENT]);
});

test("folderOverlapFromServer: a per-field-invalid entry of a known kind is dropped", () => {
  const out = folderOverlapFromServer({
    checked: true,
    reason: null,
    findings: [
      { kind: "rootContainment", whisparrRoot: "/a", coveRoot: "" }, // blank path
      { kind: "rootContainment", whisparrRoot: 7, coveRoot: "/b" }, // non-string
      { kind: "sceneFolderFormat", root: "/a", prefix: "scenes" }, // missing suggestedRoot
      CONTAINMENT, // kept
    ],
    notApplicable: [],
  });
  assert.deepEqual(out.findings, [CONTAINMENT]);
});

test("folderOverlapFromServer: notApplicable keeps only non-blank strings", () => {
  const out = folderOverlapFromServer({
    checked: true,
    reason: null,
    findings: [],
    notApplicable: ["sceneFolderFormat", "", 7, null],
  });
  assert.deepEqual(out.notApplicable, ["sceneFolderFormat"]);
});

test("folderOverlapFromServer: EVERY malformed shape is NOT-CHECKED, never an all-clear", () => {
  // This is the whole posture change: the shipped parse collapsed all of these to an empty list, which the page
  // then drew as silence — indistinguishable from "your folders are fine".
  for (const raw of [
    null,
    undefined,
    "nope",
    42,
    {}, // no checked
    { checked: "yes" }, // non-boolean
    { checked: true }, // no findings
    { checked: true, findings: "x" }, // non-array findings
    { checked: true, findings: {} },
  ]) {
    const out = folderOverlapFromServer(raw);
    assert.equal(out.checked, false, `checked must be false for ${JSON.stringify(raw)}`);
    assert.equal(out.reason, "readFailed", `reason must be readFailed for ${JSON.stringify(raw)}`);
    assert.deepEqual(out.findings, []);
  }
});

test("folderOverlapFromServer: checked:false carries its reason through; an unknown reason degrades to readFailed", () => {
  for (const reason of REASONS) {
    const out = folderOverlapFromServer({ checked: false, reason, findings: [], notApplicable: [] });
    assert.equal(out.checked, false);
    assert.equal(out.reason, reason);
  }
  const bogus = folderOverlapFromServer({ checked: false, reason: "somethingElse", findings: [] });
  assert.equal(bogus.reason, "readFailed");
  const missing = folderOverlapFromServer({ checked: false, findings: [] });
  assert.equal(missing.reason, "readFailed");
});

test("folderOverlapFromServer: a not-checked answer never carries findings", () => {
  const out = folderOverlapFromServer({
    checked: false,
    reason: "readFailed",
    findings: [CONTAINMENT],
    notApplicable: [],
  });
  assert.deepEqual(out.findings, []);
});

test("notCheckedSummary: one distinct sentence per reason value", () => {
  const sentences = REASONS.map((r) => notCheckedSummary(r));
  assert.equal(new Set(sentences).size, REASONS.length, "every reason gets its own sentence");
  for (const s of sentences) {
    assert.ok(s.length > 0);
    assert.doesNotMatch(s, IMPLIES_MIGRATION);
  }
  // Each names what would make the answer knowable, rather than only stating that it is not.
  assert.match(notCheckedSummary("notConfigured"), /Connection/);
  assert.match(notCheckedSummary("readFailed"), /Test the connection/);
  assert.match(notCheckedSummary("coveRootsUnknown"), /librar/i);
  assert.match(notCheckedSummary("unsupportedVersion"), /version/i);
});

test("notCheckedSummary: the reason record is exhaustive over the four wire values", () => {
  // Mirrors the missing-logic exhaustiveness check: a fifth reason on the wire fails this gate until its
  // sentence exists, so a new abstention cause can never render as a blank line.
  assert.deepEqual(Object.keys(NOT_CHECKED_SUMMARY).sort(), [...REASONS].sort());
});

test("rootContainmentSummary: names both roots, the echo risk, and the same-path caveat", () => {
  const s = rootContainmentSummary(CONTAINMENT);
  assert.ok(s.includes("/data/media"), "names the Whisparr root");
  assert.ok(s.includes("/data/media/scenes"), "names the Cove root");
  assert.match(s, /Whisparr/); // the next step is applied in Whisparr, not in Cove
  assert.match(s, /grab|again/i); // the re-grab echo is the reason this matters
  assert.match(s, /expected|deliberately|normal/i); // a shared root can be intentional
});

test("rootContainmentSummary: reads as a heads-up, not as a fault", () => {
  assert.doesNotMatch(rootContainmentSummary(CONTAINMENT), READS_AS_A_FAULT);
  assert.doesNotMatch(rootContainmentSummary(CONTAINMENT), IMPLIES_MIGRATION);
});

test("notApplicableSummary: names the connected generation as the reason, without implying a change", () => {
  const s = notApplicableSummary("sceneFolderFormat", "v2");
  assert.match(s, /v2/);
  assert.doesNotMatch(s, IMPLIES_MIGRATION);
  assert.doesNotMatch(s, READS_AS_A_FAULT);
  // An unknown kind still yields a sentence rather than an empty line.
  assert.ok(notApplicableSummary("somethingNew", "v3").length > 0);
});
