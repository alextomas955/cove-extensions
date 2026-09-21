import type {
  FolderAgreementRefusal,
  FolderAgreementRootLine,
  FolderAgreementView,
  FolderMappingSaveResult,
} from "../wire/api";
import {
  folderAgreementTriedSentence,
  FOLDER_AGREEMENT_NEEDS_A_PATH,
  FOLDER_AGREEMENT_NOTHING_TO_SETTLE,
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

// The wire type admits null for a folder whose stated path is working.
type FolderRefusal = NonNullable<FolderAgreementRefusal>;

export type FolderSaveAnswer =
  | { readonly kind: "answered"; readonly result: FolderMappingSaveResult }
  | { readonly kind: "didNotReach" };

// Keyed by the wire enum, so a refusal added to it fails this build until it gets a sentence.
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
 * Every refusal a folder can carry, spelled as the server's enum spells it. Transcribed by hand,
 * because a list computed from the generated module would agree with it whatever it says.
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

// Refusals a stated path cannot settle. Cove holds no file under the folder, so no typed path
// would change anything.
const NOTHING_TO_STATE: readonly FolderRefusal[] = ["noFileToProbeWith"];

export function describeFolderRefusal(refusal: FolderRefusal): string {
  return REFUSAL_SENTENCES[refusal];
}

/**
 * Whether the path in force on `line` can only be withdrawn. A save re-runs the read that produced
 * the refusal, so every path a reader could type is refused again.
 */
export function withdrawsOnly(line: FolderAgreementRootLine): boolean {
  return line.mapping !== null && line.refusal !== null && NOTHING_TO_STATE.includes(line.refusal);
}

/**
 * Whether the folder has a path field under it. The field also withdraws the path in force, because
 * a blank save is the withdrawal. Where no typed path could be stored, withdrawal gets its own
 * control instead.
 */
export function asksForAPath(line: FolderAgreementRootLine): boolean {
  if (line.mapping !== null) {
    return !withdrawsOnly(line);
  }
  return line.refusal === null || !NOTHING_TO_STATE.includes(line.refusal);
}

/** The folders to prompt for, in the order the server stored them. */
export function agreementLines(
  view: FolderAgreementView | null,
): readonly FolderAgreementRootLine[] {
  return view?.roots ?? [];
}

export function hasAnythingToShow(view: FolderAgreementView | null): boolean {
  return agreementLines(view).length > 0;
}

/** What a folder's own row says about it at a glance. */
export type FolderAgreementState = "settled" | "needsAPath" | "nothingToSettle";

/**
 * Which of the three states `line` is in. A line carrying no refusal is settled whether Cove worked
 * its path out or a reader stated one, because neither leaves anything to do.
 */
export function stateOf(line: FolderAgreementRootLine): FolderAgreementState {
  if (line.refusal === null) {
    return "settled";
  }
  return NOTHING_TO_STATE.includes(line.refusal) ? "nothingToSettle" : "needsAPath";
}

const STATE_LABELS: Record<FolderAgreementState, string> = {
  settled: FOLDER_AGREEMENT_SETTLED,
  needsAPath: FOLDER_AGREEMENT_NEEDS_A_PATH,
  nothingToSettle: FOLDER_AGREEMENT_NOTHING_TO_SETTLE,
};

export function stateLabel(state: FolderAgreementState): string {
  return STATE_LABELS[state];
}

/** Why the folder is not settled, or null where it is. */
export function reasonFor(line: FolderAgreementRootLine): string | null {
  return line.refusal === null ? null : describeFolderRefusal(line.refusal);
}

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

/** Whether the save settled the folder, so the prompts are worth reading again. */
export function saveSettled(answer: FolderSaveAnswer): boolean {
  return (
    answer.kind === "answered" &&
    (answer.result.outcome === "stored" || answer.result.outcome === "removed")
  );
}
