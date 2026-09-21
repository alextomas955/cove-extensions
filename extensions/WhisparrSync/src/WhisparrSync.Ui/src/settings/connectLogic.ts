/**
 * Pure rules for the connect surface: the sentence a refusal reads as, the affordances it offers,
 * and when a typed address stops being the one a result describes.
 *
 * Relative imports only, so this module runs with no environment. The wire types arrive as
 * `import type`, which erases at runtime.
 */
import type {
  ConnectionFailureKind,
  ConnectionTestView,
  WhisparrGeneration,
  WhisparrSyncGenerationSettingsView,
} from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import {
  CONNECT_KEY_REJECTED,
  CONNECT_NOT_CONFIGURED,
  connectNotTheWhisparrApiSentence,
  connectUnreachableSentence,
  connectVersionNotManagedSentence,
} from "../common/ui/copy";
import { describeInstant } from "./relativeTimeLogic";

export type RefusalKind = Exclude<ConnectionFailureKind, "connected">;

/** The refusal kinds, in the order the decision table reaches them. */
export const REFUSAL_KINDS: readonly RefusalKind[] = [
  "notConfigured",
  "unreachable",
  "keyRejected",
  "notTheWhisparrApi",
  "versionNotManaged",
];

/** The values a refusal sentence may name, in the spelling the response carries them. */
export interface RefusalValues {
  address: string | null;
  version: string | null;
  otherApplication: string | null;
}

/** Nothing named, for a caller that has only a kind. */
export const NO_REFUSAL_VALUES: RefusalValues = {
  address: null,
  version: null,
  otherApplication: null,
};

export interface RefusalAffordances {
  /** Whether trying the same thing again could give a different answer. */
  readonly retry: boolean;
  /** Whether a setting would fix it, so the sentence may point at one. */
  readonly settingsLink: boolean;
}

export function sentenceForKind(kind: RefusalKind, values: RefusalValues): string {
  switch (kind) {
    case "notConfigured":
      return CONNECT_NOT_CONFIGURED;
    case "unreachable":
      return connectUnreachableSentence(values.address);
    case "keyRejected":
      return CONNECT_KEY_REJECTED;
    case "notTheWhisparrApi":
      return connectNotTheWhisparrApiSentence(values.address);
    case "versionNotManaged":
      return connectVersionNotManagedSentence(values.version, values.otherApplication);
  }
}

/**
 * A version this product does not manage offers neither affordance. Retrying asks the same instance
 * the same question, and no setting would enable it.
 */
export function affordancesForKind(kind: RefusalKind): RefusalAffordances {
  switch (kind) {
    case "notConfigured":
      return { retry: false, settingsLink: true };
    case "unreachable":
      return { retry: true, settingsLink: false };
    case "keyRejected":
      return { retry: false, settingsLink: true };
    case "notTheWhisparrApi":
      return { retry: false, settingsLink: true };
    case "versionNotManaged":
      return { retry: false, settingsLink: false };
  }
}

export function valuesOf(view: ConnectionTestView): RefusalValues {
  return {
    address: view.address,
    version: view.version,
    otherApplication: view.otherApplication,
  };
}

/**
 * Trims space and trailing separators. Nothing is added: an address with no scheme is left without
 * one, so it is refused and named rather than guessed at.
 */
export function normaliseAddress(raw: string): string {
  return raw.trim().replace(/\/+$/, "");
}

/**
 * Whether `next` points somewhere other than `previous`, and so whether a result taken against the
 * previous one still describes the field.
 *
 * The comparison folds case, as the server's own same-address rule does. A browser that did not
 * would discard a result the server would have kept.
 */
export function isAddressEdit(previous: string, next: string): boolean {
  return normaliseAddress(previous).toLowerCase() !== normaliseAddress(next).toLowerCase();
}

/** The wire type admits null. A card always names a generation. */
export type CardGeneration = NonNullable<WhisparrGeneration>;

/** Both cards, in the order the page draws them. */
export const CARD_GENERATIONS: readonly CardGeneration[] = ["v3", "v2"];

/** Declared once, so two surfaces cannot name a generation differently. */
export function generationLabel(card: CardGeneration): string {
  return card === "v3" ? "Whisparr v3 (Eros)" : "Whisparr v2";
}

/** What one card's form holds that is not yet saved. */
export interface GenerationDraft {
  readonly address: string;
  /** The key typed this session. Blank leaves the stored key alone. */
  readonly apiKey: string;
  /** The stored key is to be removed by the next save. */
  readonly keyCleared: boolean;
}

/**
 * A test result, and the address it describes. The address is carried rather than read from the
 * field, so an edit made while the request was in flight does not relabel the answer.
 */
export type TransientTest =
  | { readonly phase: "none" }
  | { readonly phase: "running"; readonly address: string }
  | { readonly phase: "answered"; readonly address: string; readonly result: ConnectionTestView }
  | { readonly phase: "failed"; readonly address: string; readonly message: string };

/** No test has been run, or the last one no longer describes the field. */
export const NO_TRANSIENT_TEST: TransientTest = { phase: "none" };

/**
 * Whether changing the address field retires the result on screen. An emptied field always does:
 * there is no address left for a result to be about.
 */
export function clearsTransientResult(previous: string, next: string): boolean {
  return normaliseAddress(next) === "" || isAddressEdit(previous, next);
}

export function afterAddressEdit(
  test: TransientTest,
  previous: string,
  next: string,
): TransientTest {
  return clearsTransientResult(previous, next) ? NO_TRANSIENT_TEST : test;
}

export type DetectionOutcome =
  | { readonly kind: "matchesCard" }
  | {
      readonly kind: "otherGeneration";
      readonly detected: CardGeneration;
      readonly version: string | null;
    };

/**
 * Which generation `result` is for, or null when the test did not connect.
 *
 * Each generation's connection is stored separately. The other-generation outcome names the version
 * found and nothing else, so a caller cannot act on it as an instruction to store a connection
 * under the generation that answered.
 */
export function detectionOutcome(
  result: ConnectionTestView,
  card: CardGeneration,
): DetectionOutcome | null {
  if (result.kind !== "connected" || result.generation === null) {
    return null;
  }
  return result.generation === card
    ? { kind: "matchesCard" }
    : { kind: "otherGeneration", detected: result.generation, version: result.version };
}

/** The stored connection a card shows, or null before the settings read answers. */
export function valuesForCard(
  settings: {
    v3: WhisparrSyncGenerationSettingsView;
    v2: WhisparrSyncGenerationSettingsView;
  } | null,
  card: CardGeneration,
): WhisparrSyncGenerationSettingsView | null {
  if (settings === null) {
    return null;
  }
  return card === "v3" ? settings.v3 : settings.v2;
}

/**
 * Whether saving with `card` shown changes which generation is selected. A save made before the
 * settings read has said which is selected is not a change.
 */
export function isGenerationChange(selected: string | null, card: CardGeneration): boolean {
  return selected !== null && selected !== card;
}

/** Whether a save would write nothing that is not already stored. */
export function isNoOpSave(
  stored: WhisparrSyncGenerationSettingsView | null,
  selected: string | null,
  card: CardGeneration,
  draft: GenerationDraft,
): boolean {
  if (stored === null) {
    return false;
  }
  return (
    !isGenerationChange(selected, card) &&
    !isAddressEdit(stored.address, draft.address) &&
    draft.apiKey === "" &&
    !draft.keyCleared
  );
}

/**
 * Whether pressing Test asks about the stored connection rather than about a typed pair.
 *
 * The key is write-only, so a page that has just saved one holds no copy to send back. Asking about
 * the stored connection is the only way a test can run in that state, and it is the only test whose
 * answer may update the recorded version.
 *
 * It has to be the generation in use. The stored test asks about the selected connection, so
 * running one from the other card would answer about an instance that card does not name.
 */
export function testsStoredConnection(
  stored: WhisparrSyncGenerationSettingsView | null,
  selected: string | null,
  card: CardGeneration,
  draft: GenerationDraft,
): boolean {
  return stored !== null && stored.keyIsSet && isNoOpSave(stored, selected, card, draft);
}

/**
 * The four-way read the recorded lines render through, for one card. The empty state means the
 * instance has never been reached and the version has never been read, not that no answer arrived.
 */
export function recordedRead(
  stored: WhisparrSyncGenerationSettingsView | null,
  failed: boolean,
): AsyncRead {
  if (stored === null) {
    return { reading: !failed, failed, hasContent: false };
  }
  const hasContent = stored.recordedVersion !== null || stored.lastReachableAtUtc !== null;
  // A failure is reported only when there is content to keep beside it. With none, the failure
  // branch would replace the stored lines with an error.
  return { reading: false, failed: hasContent && failed, hasContent };
}

/** The two recorded lines, which measure different things and are never merged into one. */
export interface RecordedLines {
  /** The version read, and when it was verified. */
  readonly version: string;
  /** When the instance last answered anything at all. */
  readonly reachable: string;
}

/**
 * How the two lines read as of `nowMs`.
 *
 * A version never verified says so rather than showing nothing. It never reads the same as a
 * version that was verified and whose instance has since stopped answering, which still names the
 * version and the instant it was read.
 */
export function describeRecorded(
  stored: WhisparrSyncGenerationSettingsView,
  nowMs: number,
): RecordedLines {
  const verifiedAt =
    stored.versionVerifiedAtUtc === null
      ? null
      : describeInstant(stored.versionVerifiedAtUtc, nowMs);
  const reachableAt =
    stored.lastReachableAtUtc === null ? null : describeInstant(stored.lastReachableAtUtc, nowMs);

  return {
    version:
      stored.recordedVersion === null || verifiedAt === null
        ? "Whisparr version not verified yet"
        : `Whisparr reported ${stored.recordedVersion} · verified ${verifiedAt.text}`,
    reachable:
      reachableAt === null
        ? "Whisparr has not answered yet"
        : `Whisparr last reachable ${reachableAt.text}`,
  };
}
