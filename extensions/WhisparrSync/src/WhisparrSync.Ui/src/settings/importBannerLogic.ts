/**
 * Pure rules for the import banner: whether there is anything to say, the order the roots read in,
 * how each root's line is headed, what each cause reads as, and how the files the catch-up passed
 * over read.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles. The wire types
 * arrive as `import type`, which erases at runtime and so takes nothing with it.
 */
import type { ImportBannerRootLine, ImportBannerView, ImportRefusalCause } from "../wire/api";
import {
  IMPORT_CAUSE_AMBIGUOUS,
  IMPORT_CAUSE_NOT_FOUND,
  IMPORT_CAUSE_UNREADABLE,
  importRefusalsUnderRootSentence,
  importRefusalsWithNoReportedRootSentence,
  importsPassedOverSentence,
} from "../common/ui/copy";
import { describeInstant } from "./relativeTimeLogic";

/**
 * The key a refusal is counted under when no reporting root contained the path.
 *
 * Transcribed by hand from the server's own constant. No reported root normalises to it, which is
 * what makes it usable as a key and unusable as a heading.
 */
export const NO_REPORTED_ROOT = "";

/**
 * How many of a root's paths this surface lists.
 *
 * Transcribed by hand from the bound the stored aggregate keeps. Held here as well so the number of
 * elements this surface renders is decided by this surface, whatever it is handed.
 */
export const NEWEST_PATHS_SHOWN = 3;

/**
 * How each cause reads.
 *
 * Total by TYPE, so a cause added to the wire enum fails this build rather than compiling with no
 * decision made about it. Three sentences rather than one generic failure: a misconfigured library
 * folder and a single unreadable file send the reader somewhere different.
 */
const CAUSE_SENTENCES: Record<ImportRefusalCause, string> = {
  notFoundUnderAnyRoot: IMPORT_CAUSE_NOT_FOUND,
  ambiguousCandidates: IMPORT_CAUSE_AMBIGUOUS,
  unreadable: IMPORT_CAUSE_UNREADABLE,
};

/**
 * The causes, so a caller that must cover all three cannot miss one.
 *
 * The spellings are transcribed by hand from the server's enum. A list computed from the generated
 * module would agree with it whatever it says.
 */
export const IMPORT_REFUSAL_CAUSES: readonly ImportRefusalCause[] = [
  "notFoundUnderAnyRoot",
  "ambiguousCandidates",
  "unreadable",
];

/** How <code>cause</code> reads. */
export function describeCause(cause: ImportRefusalCause): string {
  return CAUSE_SENTENCES[cause];
}

/**
 * The lines to render, in the order they read in.
 *
 * Named roots first and in their own order, then the line no root was reported for, which names no
 * folder the reader can go and look at.
 */
export function bannerLines(view: ImportBannerView | null): readonly ImportBannerRootLine[] {
  if (view === null) {
    return [];
  }
  const named = view.roots.filter((line) => line.root !== NO_REPORTED_ROOT);
  return [
    ...[...named].sort((left, right) => left.root.localeCompare(right.root)),
    ...view.roots.filter((line) => line.root === NO_REPORTED_ROOT),
  ];
}

/**
 * The line for the files the catch-up passed over, or null when it has passed over none.
 *
 * The count never clears, so the instant is what separates a problem still happening from one that
 * stopped long ago.
 */
export function passedOverLine(view: ImportBannerView | null, nowMs: number): string | null {
  if (view === null || view.recordsContained <= 0) {
    return null;
  }
  const when =
    view.lastContainedAtUtc === null ? null : describeInstant(view.lastContainedAtUtc, nowMs);
  return importsPassedOverSentence(view.recordsContained, when?.text ?? null);
}

/**
 * Whether there is anything to say at all. Nothing to say is rendered as nothing at all.
 *
 * The two halves have different writers, and a pass can move past a record without any root having a
 * refusal recorded against it, so neither half alone decides this.
 */
export function hasAnythingToSay(view: ImportBannerView | null): boolean {
  return bannerLines(view).length > 0 || (view?.recordsContained ?? 0) > 0;
}

/** How <code>line</code> is headed, naming its root where one was reported. */
export function headingFor(line: ImportBannerRootLine): string {
  return line.root === NO_REPORTED_ROOT
    ? importRefusalsWithNoReportedRootSentence(line.countSinceLastSuccess)
    : importRefusalsUnderRootSentence(line.root, line.countSinceLastSuccess);
}

/** The paths <code>line</code> lists, at most <code>NEWEST_PATHS_SHOWN</code> of them. */
export function pathsShownFor(line: ImportBannerRootLine): ImportBannerRootLine["newestPaths"] {
  return line.newestPaths.slice(0, NEWEST_PATHS_SHOWN);
}
