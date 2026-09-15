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

/** What one save came to, including a request that never reached Cove at all. */
export type FolderSaveAnswer =
  | { readonly kind: "answered"; readonly result: FolderMappingSaveResult }
  | { readonly kind: "didNotReach" };

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

/** How <code>refusal</code> reads on its own. */
export function describeFolderRefusal(_refusal: FolderAgreementRefusal): string {
  return "";
}

/** Whether a folder in <code>refusal</code> is one a stated path could settle. */
export function asksForAPath(_refusal: FolderAgreementRefusal): boolean {
  return false;
}

/** Whether there is anything to ask about at all. */
export function hasAnythingToAsk(_view: FolderAgreementView | null): boolean {
  return false;
}

/** The folders to prompt for, in the order they read in. */
export function agreementLines(
  _view: FolderAgreementView | null,
): readonly FolderAgreementRootLine[] {
  return [];
}

/** The whole of what <code>line</code> says: the folder, what happened, and what was asked. */
export function sentenceFor(_line: FolderAgreementRootLine): string {
  return "";
}

/** The path already stated for <code>line</code>, or null where none is. */
export function mappingSentenceFor(_line: FolderAgreementRootLine): string | null {
  return null;
}

/** What <code>answer</code> says to the reader who pressed save. */
export function saveAnswerSentence(_answer: FolderSaveAnswer): string {
  return "";
}

/** Whether <code>answer</code> settled the folder, so the prompts are worth reading again. */
export function saveSettled(_answer: FolderSaveAnswer): boolean {
  return false;
}
