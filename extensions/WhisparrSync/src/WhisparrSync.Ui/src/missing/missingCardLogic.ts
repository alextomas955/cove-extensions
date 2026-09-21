/** What one catalogue card draws, derived from the scene the server answered with. */
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

/** Performers beyond this become a count. */
export const PERFORMER_CHIP_LIMIT = 4;

export interface CardMetaRow {
  readonly releaseDate: string | null;
  readonly studioName: string | null;
}

/**
 * The rows one card draws. A row the scene carries no value for is null, and the view omits it
 * rather than drawing an empty strip that would read as a value the card failed to render.
 */
export interface CardRows {
  readonly meta: CardMetaRow | null;
  readonly performers: readonly MissingPerformerChip[] | null;
  readonly description: string | null;
  readonly counts: string | null;
}

export type CardVerb = "monitor" | "search";

/**
 * What one card's action row is doing, and what its last press produced. No answer and a refusal
 * are two fields, because a reader acts differently on the two. Neither carries a word the
 * instance sent back.
 */
export interface CardActionState {
  readonly inFlight: CardVerb | null;
  /**
   * The state the pill draws while a press is unsettled, or null to draw the answered one. Every
   * settle clears it, which puts a refused card back as it was.
   */
  readonly optimistic: MissingSceneState | null;
  readonly refusal: MissingSceneActionRefusal | null;
  /** The last press produced no answer at all. */
  readonly failed: boolean;
}

export const CARD_ACTION_AT_REST: CardActionState = {
  inFlight: null,
  optimistic: null,
  refusal: null,
  failed: false,
};

export interface CardFailureLine {
  readonly sentence: string;
  /** Muted for a legitimate answer, error for everything else. */
  readonly kind: "error" | "muted";
}

// Total by type, so a value added to the wire enum fails the typecheck here rather than falling
// through to no line. The two muted answers report an absence, not the instance declining.
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
 * What is stated beneath one card's action row, or null when nothing is. A request that produced
 * no answer reads the same as one the server classified as having reached nothing.
 */
export function cardFailureLine(action: CardActionState): CardFailureLine | null {
  if (action.failed) {
    return LINE_FOR.didNotReachWhisparr;
  }
  return action.refusal === null ? null : LINE_FOR[action.refusal];
}

/** The optimistic value while a press is unsettled, and the answered state otherwise. */
export function displayedState(
  answered: MissingSceneState,
  action: CardActionState,
): MissingSceneState {
  return action.optimistic ?? answered;
}

/**
 * The result `answered` carries, or null where it carries none. The post helper resolves a
 * bodyless success as an empty object, so a caller cannot assume the shape.
 */
export function sceneActionIn(answered: unknown): MissingSceneActionResult | null {
  if (answered === null || typeof answered !== "object") {
    return null;
  }

  const { state, refusal } = answered as Partial<MissingSceneActionResult>;
  return state === undefined || refusal === undefined ? null : { state, refusal };
}

export function visiblePerformerChips(
  performers: readonly MissingPerformerChip[],
): readonly MissingPerformerChip[] {
  return performers.slice(0, PERFORMER_CHIP_LIMIT);
}

export function overflowChipCount(performers: readonly MissingPerformerChip[]): number {
  return Math.max(0, performers.length - PERFORMER_CHIP_LIMIT);
}

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

// Null where the value is absent or blank.
function present(value: string | null | undefined): string | null {
  return value === null || value === undefined || value.trim() === "" ? null : value;
}

// A half that counts nothing is omitted rather than rendered as a zero, which would read as a
// measurement of the scene.
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
