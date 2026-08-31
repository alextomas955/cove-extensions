/**
 * Pure, DOM-free rules for the connect surface: which sentence a refusal reads as, which affordances
 * it offers, and when a typed address stops being the one a result describes.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles. The wire types
 * arrive as `import type`, which erases at runtime and so takes nothing with it.
 */
import type {
  ConnectionFailureKind,
  ConnectionTestView,
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

/** Every outcome except the one that is not a refusal. */
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

/** Nothing named. The starting point for a caller that has only a kind. */
export const NO_REFUSAL_VALUES: RefusalValues = {
  address: null,
  version: null,
  otherApplication: null,
};

/** What a refusal offers the user to do about it. */
export interface RefusalAffordances {
  /** Whether trying the same thing again could give a different answer. */
  readonly retry: boolean;
  /** Whether a setting would fix it, so the sentence may point at one. */
  readonly settingsLink: boolean;
}

/** The sentence <code>kind</code> reads as, naming whichever of <code>values</code> it uses. */
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
 * What <code>kind</code> offers.
 *
 * A version this product does not manage offers neither: retrying asks the same instance the same
 * question, and no setting would enable it, so advice to change one sends the user to the wrong place.
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

/** The values <code>view</code> carries, for the sentence its kind reads as. */
export function valuesOf(view: ConnectionTestView): RefusalValues {
  return {
    address: view.address,
    version: view.version,
    otherApplication: view.otherApplication,
  };
}

/**
 * The address with the parts that do not change where it points removed.
 *
 * Trimming and a trailing separator only. Nothing is added: an address with no scheme is left without
 * one, so it is refused and named rather than silently turned into a guess at what the user meant.
 */
export function normaliseAddress(raw: string): string {
  return raw.trim().replace(/\/+$/, "");
}

/**
 * Whether <code>next</code> points somewhere other than <code>previous</code>, and so whether a
 * result taken against the previous one still describes what the field now says.
 *
 * Letter case does not move an address, so it is not an edit. The comparison folds case for the same
 * reason the server's own same-address rule does, and the two are only worth having if they agree:
 * a browser that discarded a result the server would have kept reports a stale reading as absent.
 */
export function isAddressEdit(previous: string, next: string): boolean {
  return normaliseAddress(previous).toLowerCase() !== normaliseAddress(next).toLowerCase();
}

/** The two generations a card can show. Never null: a card always names one. */
export type CardGeneration = "v3" | "v2";

/** Both cards, in the order the page draws them. */
export const CARD_GENERATIONS: readonly CardGeneration[] = ["v3", "v2"];

/** What a generation is called on screen. Declared once, so two surfaces cannot name it differently. */
export function generationLabel(card: CardGeneration): string {
  return card === "v3" ? "Whisparr v3 (Eros)" : "Whisparr v2";
}

/** What one card's form holds that is not yet saved. */
export interface GenerationDraft {
  /** The address as typed. */
  readonly address: string;
  /** The key typed this session. Blank leaves the stored key alone. */
  readonly apiKey: string;
  /** The stored key is to be removed by the next save. */
  readonly keyCleared: boolean;
}

/**
 * A test result, and the address it describes.
 *
 * The address is carried rather than read from the field, so a field edited while the request was in
 * flight does not silently relabel the answer as being about somewhere it never reached.
 */
export type TransientTest =
  | { readonly phase: "none" }
  | { readonly phase: "running"; readonly address: string }
  | { readonly phase: "answered"; readonly address: string; readonly result: ConnectionTestView }
  | { readonly phase: "failed"; readonly address: string; readonly message: string };

/** No test has been run, or the last one no longer describes the field. */
export const NO_TRANSIENT_TEST: TransientTest = { phase: "none" };

/**
 * Whether changing the address field from <code>previous</code> to <code>next</code> retires the
 * result on screen.
 *
 * An emptied field always does: there is no address left for a result to be about.
 */
export function clearsTransientResult(previous: string, next: string): boolean {
  return normaliseAddress(next) === "" || isAddressEdit(previous, next);
}

/** What <code>test</code> becomes when the address field goes from <code>previous</code> to <code>next</code>. */
export function afterAddressEdit(
  test: TransientTest,
  previous: string,
  next: string,
): TransientTest {
  return clearsTransientResult(previous, next) ? NO_TRANSIENT_TEST : test;
}

/** What a successful test's detected generation means for the card it was run from. */
export type DetectionOutcome =
  | { readonly kind: "matchesCard" }
  | {
      readonly kind: "otherGeneration";
      readonly detected: CardGeneration;
      readonly version: string | null;
    };

/**
 * Which of the two <code>result</code> is for <code>card</code>, or null when the test did not
 * connect at all.
 *
 * Each generation's connection is remembered separately. The other-generation outcome therefore
 * names the version found and carries nothing else: it holds no value a caller could act on as an
 * instruction to store a connection under the generation that answered.
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

/** The stored connection <code>card</code> shows, or null before the settings read answers. */
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
 * Whether saving with <code>card</code> shown changes which generation is selected.
 *
 * A save selecting the generation already selected is not a change, and neither is one made before
 * the settings read has said which is selected.
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
 * The four-way read the recorded lines render through, for one card.
 *
 * The empty state is a card whose instance has never been reached and whose version has never been
 * read, which is a true zero rather than an absence of an answer.
 */
export function recordedRead(
  stored: WhisparrSyncGenerationSettingsView | null,
  failed: boolean,
): AsyncRead {
  if (stored === null) {
    return { reading: !failed, failed, hasContent: false };
  }
  return {
    reading: false,
    failed: false,
    hasContent: stored.recordedVersion !== null || stored.lastReachableAtUtc !== null,
  };
}

/** The two recorded lines, which measure different things and are never merged into one. */
export interface RecordedLines {
  /** The version read, and when it was verified. */
  readonly version: string;
  /** When the instance last answered anything at all. */
  readonly reachable: string;
}

/**
 * How the two lines read for <code>stored</code> as of <code>nowMs</code>.
 *
 * A version never verified says so rather than showing nothing, and it never shares a rendering with
 * a version that was verified and whose instance has since stopped answering: the second still names
 * the version and the instant it was read.
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
