/**
 * Pure rules for the folder agreement prompts: whether there is anything to ask about, what each
 * unsettled folder reads as, which of them is worth asking a path for, and what one save came to.
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
const REFUSAL_SENTENCES: Record<FolderAgreementRefusal, string> = {
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
export const FOLDER_AGREEMENT_REFUSALS: readonly FolderAgreementRefusal[] = [
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
const NOTHING_TO_STATE: readonly FolderAgreementRefusal[] = ["noFileToProbeWith"];

/** How <code>refusal</code> reads on its own. */
export function describeFolderRefusal(refusal: FolderAgreementRefusal): string {
  return REFUSAL_SENTENCES[refusal];
}

/** Whether a folder in <code>refusal</code> is one a stated path could settle. */
export function asksForAPath(refusal: FolderAgreementRefusal): boolean {
  return !NOTHING_TO_STATE.includes(refusal);
}

/** The folders to prompt for, in the order the server stored them. */
export function agreementLines(
  view: FolderAgreementView | null,
): readonly FolderAgreementRootLine[] {
  return view?.roots ?? [];
}

/** Whether there is anything to ask about at all. Nothing to ask is rendered as nothing at all. */
export function hasAnythingToAsk(view: FolderAgreementView | null): boolean {
  return agreementLines(view).length > 0;
}

/**
 * The whole of what <code>line</code> says: which folder, what happened to it, and what the
 * instance was asked about.
 *
 * The paths tried are named rather than counted, because they are what a reader compares against
 * what Whisparr really holds.
 */
export function sentenceFor(line: FolderAgreementRootLine): string {
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
