/**
 * Pure logic for the folder-overlap advisory — "do Cove's and Whisparr's folders line up". The server reports two
 * kinds of finding: a Whisparr root and a Cove root where one contains the other, and (on Eros) a Whisparr root
 * whose trailing segment equals the Scene Folder Format's leading literal, which concatenates into a doubled
 * segment (`/data/media/scenes` + `scenes/…` → `/data/media/scenes/scenes/…`, unfindable, every scene shows
 * "Missing"). It also reports when it compared NOTHING, and why. This module parses that untrusted shape and
 * derives every sentence, extracted so it is unit-testable without a DOM — the server returns facts only.
 */

/** One flagged root: the offending Whisparr root, the doubled leading literal, and the fix one level above. */
export interface SceneFolderOverlap {
  /** The Whisparr root folder whose trailing segment doubles the format prefix, as the server supplied it. */
  root: string;
  /** The Scene Folder Format's leading literal that the root's trailing segment repeats (e.g. `scenes`). */
  prefix: string;
  /** The root one level above (trailing overlapping segment stripped) the user should set in Whisparr. */
  suggestedRoot: string;
}

/** The concrete doubled path Eros produces, so the user sees the repeated segment rather than an abstraction. */
export function doubledPathDisplay(o: SceneFolderOverlap): string {
  return `${o.root}/${o.prefix}/…`;
}

/**
 * The advisory's one-sentence body: names the doubled path, then the fix. Phrased as guidance the user applies in
 * Whisparr (the extension cannot change Whisparr's config), and always contains the `suggestedRoot` literal so the
 * exact value to enter is on screen.
 */
export function sceneFolderOverlapSummary(o: SceneFolderOverlap): string {
  return `Whisparr will write scenes to ${doubledPathDisplay(o)} — the “${o.prefix}” segment is doubled, so Cove can’t find them. In Whisparr, set this root folder to ${o.suggestedRoot} (the level above).`;
}

/** Why the server compared nothing. Exactly the four values the wire can carry. */
export type FolderOverlapReason =
  "notConfigured" | "readFailed" | "coveRootsUnknown" | "unsupportedVersion";

/** A Whisparr root and a Cove root where one contains (or equals) the other, both as the server supplied them. */
export interface RootContainmentFinding {
  kind: "rootContainment";
  whisparrRoot: string;
  coveRoot: string;
}

/** The shipped scene-folder-format finding, now tagged with its kind so one array can carry both. */
export interface SceneFolderFormatFinding extends SceneFolderOverlap {
  kind: "sceneFolderFormat";
}

export type FolderFinding = RootContainmentFinding | SceneFolderFormatFinding;

/**
 * The whole folder-overlap answer. `checked` is the discriminator, and it is the reason an empty `findings` array
 * is only an all-clear when `checked` is true.
 */
export interface FolderOverlapAnswer {
  checked: boolean;
  reason: FolderOverlapReason | null;
  findings: FolderFinding[];
  notApplicable: string[];
}

/**
 * One sentence per abstention reason. Each names what would make the answer knowable rather than only stating
 * that it is unknown, and none of them implies the user should change Whisparr generation — v2 and v3 are both
 * first-class. Exported as a record so a fifth wire reason fails the offline gate until its sentence exists.
 */
export const NOT_CHECKED_SUMMARY: Record<FolderOverlapReason, string> = {
  notConfigured:
    "Cove hasn’t compared your folders yet — no Whisparr connection is saved. Add one under Connection and it will check on the next load.",
  readFailed:
    "Cove couldn’t compare your folders — Whisparr didn’t answer the folder read. Test the connection above, then reload this page.",
  coveRootsUnknown:
    "Cove couldn’t compare your folders — it couldn’t resolve its own library folders. Add a library path in Cove’s settings, then reload this page.",
  unsupportedVersion:
    "Cove couldn’t compare your folders — the saved Whisparr version isn’t one this extension manages. Test the connection above to detect it again.",
};

const NOT_CHECKED_FALLBACK: FolderOverlapReason = "readFailed";

/**
 * The answer a read that THREW produces. A thrown read is the same "no answer arrived" fact as a payload this
 * build cannot read, and returning it here is what stops the page's catch from drawing an outage as silence.
 */
export function readFailedAnswer(): FolderOverlapAnswer {
  return notChecked(NOT_CHECKED_FALLBACK);
}

function isReason(value: unknown): value is FolderOverlapReason {
  return typeof value === "string" && Object.hasOwn(NOT_CHECKED_SUMMARY, value);
}

function notChecked(reason: FolderOverlapReason): FolderOverlapAnswer {
  return { checked: false, reason, findings: [], notApplicable: [] };
}

function nonBlankString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function findingFrom(raw: unknown): FolderFinding | null {
  if (!raw || typeof raw !== "object") return null;
  const r = raw as Record<string, unknown>;
  if (
    r.kind === "rootContainment" &&
    nonBlankString(r.whisparrRoot) &&
    nonBlankString(r.coveRoot)
  ) {
    return { kind: "rootContainment", whisparrRoot: r.whisparrRoot, coveRoot: r.coveRoot };
  }
  if (
    r.kind === "sceneFolderFormat" &&
    nonBlankString(r.root) &&
    nonBlankString(r.prefix) &&
    nonBlankString(r.suggestedRoot)
  ) {
    return {
      kind: "sceneFolderFormat",
      root: r.root,
      prefix: r.prefix,
      suggestedRoot: r.suggestedRoot,
    };
  }
  // An unrecognised kind is dropped rather than kept: a future finding kind this build cannot word would
  // otherwise render as a blank row inside the advisory.
  return null;
}

/**
 * Read the whole answer from an untrusted response. A payload this build cannot make sense of maps to
 * NOT-CHECKED, never to an empty finding list: the shipped "anything off yields empty" posture drew a broken read
 * as silence, which is exactly the false all-clear this shape removes.
 */
export function folderOverlapFromServer(raw: unknown): FolderOverlapAnswer {
  if (!raw || typeof raw !== "object") return notChecked(NOT_CHECKED_FALLBACK);
  const r = raw as Record<string, unknown>;
  if (typeof r.checked !== "boolean") return notChecked(NOT_CHECKED_FALLBACK);
  if (!r.checked) return notChecked(isReason(r.reason) ? r.reason : NOT_CHECKED_FALLBACK);
  if (!Array.isArray(r.findings)) return notChecked(NOT_CHECKED_FALLBACK);

  return {
    checked: true,
    reason: null,
    findings: r.findings.map(findingFrom).filter((f): f is FolderFinding => f !== null),
    notApplicable: Array.isArray(r.notApplicable) ? r.notApplicable.filter(nonBlankString) : [],
  };
}

/** True when the block has anything to say: a finding, or the fact that nothing was compared. */
export function hasFolderOverlapAdvisory(answer: FolderOverlapAnswer): boolean {
  return !answer.checked || answer.findings.length > 0;
}

/**
 * The root-containment sentence. Deliberately a heads-up rather than a fault: a single shared volume is a
 * legitimate layout, and the extension cannot tell that apart from an accident, so alarming wording here would be
 * a new way for the interface to overstate what it knows. The remedy is stated as the one the user can actually
 * perform, in Whisparr.
 */
export function rootContainmentSummary(f: RootContainmentFinding): string {
  return `Whisparr’s root ${f.whisparrRoot} and your Cove library root ${f.coveRoot} sit inside one another. Cove imports files where they already are, so a file landing here can come back to Whisparr as a new grab and be picked up again. If both systems are meant to see the same folder, that’s expected — otherwise, in Whisparr, point this root somewhere outside your Cove library.`;
}

/** The sentence for an abstention, keyed on the reason the server named. */
export function notCheckedSummary(reason: string): string {
  return NOT_CHECKED_SUMMARY[isReason(reason) ? reason : NOT_CHECKED_FALLBACK];
}

/**
 * The sentence for a check this connection cannot answer at all. It names the connected generation as the reason
 * — a fact, not a shortcoming — and never suggests changing it.
 * Its only home is the advisory's tooltip, which bounds its length and forbids leaning on surrounding text.
 */
export function notApplicableSummary(kind: string, version: string): string {
  if (kind === "sceneFolderFormat") {
    return `The scene-folder-format check reads a setting that only Whisparr v3 (Eros) has, so there’s nothing for it to compare on your ${version} connection.`;
  }
  return `This check isn’t something a Whisparr ${version} connection can answer.`;
}
