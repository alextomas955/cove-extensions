/**
 * What one catalogue card draws, derived from the scene the server answered with.
 *
 * Pure and relative-import-only, so every branch below runs with no environment and no mocks. The
 * view holds the markup and nothing else, which is what keeps the row-omission rule and the failure
 * vocabulary testable in a node environment where a `.tsx` is never rendered.
 */
import {
  ACTION_DID_NOT_REACH_WHISPARR,
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  NO_AGREED_ROOT_FOR_THIS_ENTITY,
  SEARCH_WITH_NO_ENTRY,
} from "../common/ui/copy";
import type {
  MissingCard,
  MissingPerformerChip,
  MissingSceneActionRefusal,
  MissingSceneActionResult,
  MissingSceneState,
} from "../wire/api";

/** How many performer chips one card draws before the rest become a count. */
export const PERFORMER_CHIP_LIMIT = 4;

/** The date and the studio, each present only where the provider named it. */
export interface CardMetaRow {
  readonly releaseDate: string | null;
  readonly studioName: string | null;
}

/**
 * The rows one card draws.
 *
 * A row the scene carries no value for is null, and a null row is omitted rather than drawn as an
 * empty strip: an empty strip reads as a value the provider gave and the card failed to render.
 */
export interface CardRows {
  readonly meta: CardMetaRow | null;
  readonly performers: readonly MissingPerformerChip[] | null;
  readonly description: string | null;
  readonly counts: string | null;
}

/** Which of a card's two verbs a request is in the air for. */
export type CardVerb = "monitor" | "search";

/**
 * What one card's action row is doing, and what its last press produced.
 *
 * "No answer arrived" and "the instance answered and declined" are two fields, following the entity
 * control's own store: a reader acts differently on the two, and a single field would collapse them.
 * Neither carries a word the instance sent back.
 */
export interface CardActionState {
  /** The verb whose request is in the air, or null when none is. */
  readonly inFlight: CardVerb | null;
  /**
   * The state the pill draws while a press is unsettled, or null to draw the answered one.
   *
   * Cleared by every settle, which is what puts a refused card back as it was.
   */
  readonly optimistic: MissingSceneState | null;
  /** Why the instance declined the last press, or null. */
  readonly refusal: MissingSceneActionRefusal | null;
  /** The last press produced no answer at all. */
  readonly failed: boolean;
}

/** No press has been made, or the last one settled and took. */
export const CARD_ACTION_AT_REST: CardActionState = {
  inFlight: null,
  optimistic: null,
  refusal: null,
  failed: false,
};

/** One sentence beneath the action row, and how it reads. */
export interface CardFailureLine {
  readonly sentence: string;
  /** Muted for a legitimate answer, error for everything else. */
  readonly kind: "error" | "muted";
}

/**
 * The sentence each refusal reads as.
 *
 * Total by type, so a value added to the wire enum fails the typecheck here rather than falling
 * through to no line at all. The two muted answers report an absence: the instance holds no entry
 * for the scene, or the connected generation registers no role for the verb. Neither is the
 * instance declining, and each sends a reader somewhere different from every other value.
 */
const LINE_FOR: Record<MissingSceneActionRefusal, CardFailureLine | null> = {
  none: null,
  didNotReachWhisparr: { sentence: ACTION_DID_NOT_REACH_WHISPARR, kind: "error" },
  instanceRefused: { sentence: INSTANCE_REFUSED, kind: "error" },
  capabilityAbsentOnThisGeneration: {
    sentence: CAP_UNAVAILABLE_ON_THIS_GENERATION,
    kind: "muted",
  },
  instanceOffersNoQualityProfile: {
    sentence: INSTANCE_OFFERS_NO_QUALITY_PROFILE,
    kind: "error",
  },
  instanceOffersNoRootFolder: { sentence: INSTANCE_OFFERS_NO_ROOT_FOLDER, kind: "error" },
  noAgreedRootForThisEntity: { sentence: NO_AGREED_ROOT_FOR_THIS_ENTITY, kind: "error" },
  whisparrHasNoEntryForScene: { sentence: SEARCH_WITH_NO_ENTRY, kind: "muted" },
};

/**
 * What is stated beneath one card's action row, or null when nothing is.
 *
 * A request that produced no answer reads the same as one the server classified as having reached
 * nothing: both mean the instance was not told, and the reader's next move is the same.
 */
export function cardFailureLine(action: CardActionState): CardFailureLine | null {
  if (action.failed) {
    return LINE_FOR.didNotReachWhisparr;
  }
  return action.refusal === null ? null : LINE_FOR[action.refusal];
}

/**
 * The state one card's pill draws.
 *
 * The optimistic value only while a press is unsettled. Every settle clears it, so a refused press
 * draws the state the server last answered with, which is the state before the press.
 */
export function displayedState(
  answered: MissingSceneState,
  action: CardActionState,
): MissingSceneState {
  return action.optimistic ?? answered;
}

/**
 * The result <code>answered</code> carries, or null where it carries none.
 *
 * The post helper resolves a bodyless success as an empty object, so a caller cannot assume the
 * shape. An answer that names neither member is one nothing can be read from, which is the same
 * position as no answer at all.
 */
export function sceneActionIn(answered: unknown): MissingSceneActionResult | null {
  if (answered === null || typeof answered !== "object") {
    return null;
  }

  const { state, refusal } = answered as Partial<MissingSceneActionResult>;
  return state === undefined || refusal === undefined ? null : { state, refusal };
}

/** The performers a card draws as chips. */
export function visiblePerformerChips(
  performers: readonly MissingPerformerChip[],
): readonly MissingPerformerChip[] {
  return performers.slice(0, PERFORMER_CHIP_LIMIT);
}

/** How many performers the chips do not name. */
export function overflowChipCount(performers: readonly MissingPerformerChip[]): number {
  return Math.max(0, performers.length - PERFORMER_CHIP_LIMIT);
}

/** The rows <code>card</code> gives the view something to draw. */
export function deriveCardRows(card: MissingCard): CardRows {
  const releaseDate = present(card.releaseDate);
  const studioName = present(card.studioName);

  return {
    meta: releaseDate === null && studioName === null ? null : { releaseDate, studioName },
    performers: card.performers.length === 0 ? null : card.performers,
    description: present(card.description),
    counts: countsFooter(card.performerCount, card.tagCount),
  };
}

/** <code>value</code> where it carries something, and null where it is absent or blank. */
function present(value: string | null | undefined): string | null {
  return value === null || value === undefined || value.trim() === "" ? null : value;
}

/**
 * How many performers and tags the provider names, or null where it names neither.
 *
 * A half that counts nothing is omitted rather than rendered as a zero: a zero here reads as a
 * measurement of the scene, and the provider simply did not list any.
 */
function countsFooter(performerCount: number, tagCount: number): string | null {
  const parts: string[] = [];
  if (performerCount > 0) {
    parts.push(`${String(performerCount)} ${performerCount === 1 ? "performer" : "performers"}`);
  }
  if (tagCount > 0) {
    parts.push(`${String(tagCount)} ${tagCount === 1 ? "tag" : "tags"}`);
  }
  return parts.length === 0 ? null : parts.join(" · ");
}
