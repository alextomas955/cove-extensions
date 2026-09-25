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
 * Transcribed by hand from the server's own constant. No real root normalises to it.
 */
export const NO_REPORTED_ROOT = "";

/**
 * How many of a root's paths this surface lists.
 *
 * Transcribed by hand from the bound the stored aggregate keeps, so this surface caps its own
 * output whatever it is handed.
 */
export const NEWEST_PATHS_SHOWN = 3;

// Total by type, so a cause added to the wire enum fails the build instead of compiling silently.
const CAUSE_SENTENCES: Record<ImportRefusalCause, string> = {
  notFoundUnderAnyRoot: IMPORT_CAUSE_NOT_FOUND,
  ambiguousCandidates: IMPORT_CAUSE_AMBIGUOUS,
  unreadable: IMPORT_CAUSE_UNREADABLE,
};

/** The causes. Spellings transcribed by hand from the server's enum. */
export const IMPORT_REFUSAL_CAUSES: readonly ImportRefusalCause[] = [
  "notFoundUnderAnyRoot",
  "ambiguousCandidates",
  "unreadable",
];

export function describeCause(cause: ImportRefusalCause): string {
  return CAUSE_SENTENCES[cause];
}

/** The lines to render: named roots sorted by root, then the line with no reported root. */
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
 * The line for the files the catch-up passed over, or null when it passed over none.
 *
 * The count never clears, so the instant is what tells a current problem from an old one.
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
 * Whether the banner has anything to say.
 *
 * Both halves are checked: a pass can move past a record with no root refusal recorded.
 */
export function hasAnythingToSay(view: ImportBannerView | null): boolean {
  return bannerLines(view).length > 0 || (view?.recordsContained ?? 0) > 0;
}

export function headingFor(line: ImportBannerRootLine): string {
  return line.root === NO_REPORTED_ROOT
    ? importRefusalsWithNoReportedRootSentence(line.countSinceLastSuccess)
    : importRefusalsUnderRootSentence(line.root, line.countSinceLastSuccess);
}

export function pathsShownFor(line: ImportBannerRootLine): ImportBannerRootLine["newestPaths"] {
  return line.newestPaths.slice(0, NEWEST_PATHS_SHOWN);
}
