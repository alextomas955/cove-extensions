/**
 * Pure rules for the folder agreement lines: whether there is anything to show, what each folder
 * reads as, which of them a path can be stated or withdrawn for, and what one save came to.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles. The wire types
 * arrive as `import type`, which erases at runtime and so takes nothing with it.
 */
import type {
  FolderAgreementRefusal,
  FolderAgreementRootLine,
  FolderAgreementView,
  FolderMappingSaveResult,
} from "../wire/api";
import {
  folderAgreementMappingSentence,
  folderAgreementRootSentence,
  folderAgreementTriedSentence,
  FOLDER_AGREEMENT_SETTLED,
  FOLDER_INSTANCE_CANNOT_BE_ASKED,
  FOLDER_INSTANCE_DECLARES_NO_ROOT,
  FOLDER_MORE_THAN_ONE_RESOLVED,
  FOLDER_NOTHING_RESOLVED,
  FOLDER_NO_FILE_TO_PROBE_WITH,
  FOLDER_PROBE_COULD_NOT_BE_READ,
  FOLDER_SAVE_DID_NOT_REACH,
  FOLDER_SAVE_NOT_A_LIBRARY_ROOT,
  FOLDER_SAVE_NOT_CONFIGURED,
  FOLDER_SAVE_REMOVED,
  FOLDER_SAVE_STORED,
  FOLDER_UNDER_NO_LIBRARY_ROOT,
} from "../common/ui/copy";

/**
 * A reason a folder could not be settled.
 *
 * The wire type admits null, because a line for a folder whose stated path is working carries no
 * reason at all. Everything keyed by a reason works in this narrower type instead.
 */
type FolderRefusal = NonNullable<FolderAgreementRefusal>;

/** What one save came to, including a request that never reached Cove at all. */
export type FolderSaveAnswer =
  | { readonly kind: "answered"; readonly result: FolderMappingSaveResult }
  | { readonly kind: "didNotReach" };

/**
 * How each refusal reads.
 *
 * Total by TYPE, so a refusal added to the wire enum fails this build rather than compiling with no
 * decision made about it. An answer that could not be read and an answer of no are separate
 * sentences, because a reader who takes the first for the second stops looking.
 */
const REFUSAL_SENTENCES: Record<FolderRefusal, string> = {
  instanceDeclaresNoRoot: FOLDER_INSTANCE_DECLARES_NO_ROOT,
  noFileToProbeWith: FOLDER_NO_FILE_TO_PROBE_WITH,
  nothingResolved: FOLDER_NOTHING_RESOLVED,
  moreThanOneResolved: FOLDER_MORE_THAN_ONE_RESOLVED,
  probeCouldNotBeRead: FOLDER_PROBE_COULD_NOT_BE_READ,
  instanceCannotBeAsked: FOLDER_INSTANCE_CANNOT_BE_ASKED,
  folderUnderNoLibraryRoot: FOLDER_UNDER_NO_LIBRARY_ROOT,
};

/**
 * Every refusal a folder can carry.
 *
 * The spellings are transcribed by hand from the server's enum. A list computed from the generated
 * module would agree with it whatever it says.
 */
export const FOLDER_AGREEMENT_REFUSALS: readonly FolderRefusal[] = [
  "instanceDeclaresNoRoot",
  "noFileToProbeWith",
  "nothingResolved",
  "moreThanOneResolved",
  "probeCouldNotBeRead",
  "instanceCannotBeAsked",
  "folderUnderNoLibraryRoot",
];

/**
 * The refusals a stated path cannot settle.
 *
 * Nothing is misconfigured for a folder Cove holds no file under, so it is reported and no path is
 * asked for: a form under it would invite an answer that changes nothing.
 */
const NOTHING_TO_STATE: readonly FolderRefusal[] = ["noFileToProbeWith"];

/** How <code>refusal</code> reads on its own. */
export function describeFolderRefusal(refusal: FolderRefusal): string {
  return REFUSAL_SENTENCES[refusal];
}

/**
 * Whether the folder <code>line</code> is about is one a stated path could settle.
 *
 * A line with no reason carries a path that is working, and the field is what withdraws it.
 */
export function asksForAPath(line: FolderAgreementRootLine): boolean {
  return line.refusal === null || !NOTHING_TO_STATE.includes(line.refusal);
}

/** The folders to prompt for, in the order the server stored them. */
export function agreementLines(
  view: FolderAgreementView | null,
): readonly FolderAgreementRootLine[] {
  return view?.roots ?? [];
}

/** Whether there is anything to show at all. Nothing to show is rendered as nothing at all. */
export function hasAnythingToShow(view: FolderAgreementView | null): boolean {
  return agreementLines(view).length > 0;
}

/**
 * The whole of what <code>line</code> says: which folder, what happened to it, and what the
 * instance was asked about.
 *
 * The paths tried are named rather than counted, because they are what a reader compares against
 * what Whisparr really holds. A line with no reason names none: the instance was asked about
 * nothing this run, and saying so would read as a failure rather than a working path.
 */
export function sentenceFor(line: FolderAgreementRootLine): string {
  if (line.refusal === null) {
    return [folderAgreementRootSentence(line.root), FOLDER_AGREEMENT_SETTLED].join(" ");
  }
  return [
    folderAgreementRootSentence(line.root),
    describeFolderRefusal(line.refusal),
    folderAgreementTriedSentence(line.pathsTried),
  ].join(" ");
}

/** The path already stated for <code>line</code>, or null where none is. */
export function mappingSentenceFor(line: FolderAgreementRootLine): string | null {
  return line.mapping === null ? null : folderAgreementMappingSentence(line.mapping);
}

/**
 * What <code>answer</code> says to the reader who pressed save.
 *
 * A refusal carries the refusal's own sentence and the path that was tried, so a reader who mistyped
 * sees what was put to the instance rather than only that it did not work.
 */
export function saveAnswerSentence(answer: FolderSaveAnswer): string {
  if (answer.kind === "didNotReach") {
    return FOLDER_SAVE_DID_NOT_REACH;
  }

  const { outcome, refusal, tried } = answer.result;
  switch (outcome) {
    case "stored":
      return FOLDER_SAVE_STORED;
    case "removed":
      return FOLDER_SAVE_REMOVED;
    case "notALibraryRoot":
      return FOLDER_SAVE_NOT_A_LIBRARY_ROOT;
    case "notConfigured":
      return FOLDER_SAVE_NOT_CONFIGURED;
    case "refused":
      return refusal === null
        ? FOLDER_SAVE_DID_NOT_REACH
        : [describeFolderRefusal(refusal), folderAgreementTriedSentence(tried)].join(" ");
  }
}

/** Whether <code>answer</code> settled the folder, so the prompts are worth reading again. */
export function saveSettled(answer: FolderSaveAnswer): boolean {
  return (
    answer.kind === "answered" &&
    (answer.result.outcome === "stored" || answer.result.outcome === "removed")
  );
}
