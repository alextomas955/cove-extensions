/**
 * Which of the grid's stated reasons is in force, and what each one offers.
 *
 * The invariant this module holds: a grid with no cards always names a cause, and only a cause a
 * retry could clear offers one. The two Whisparr cases are the pair that makes this worth holding in
 * one place - they read alike and differ only in whether asking again could change the answer.
 */
import {
  EVERY_SCENE_ON_THIS_PAGE_IS_OWNED,
  NOTHING_MISSING,
  NO_INSTANCE_CONNECTED,
  NO_METADATA_PROVIDER_CONFIGURED,
  NO_PROVIDER_ID_FOR_ENTITY,
  NO_SCENES_MATCH_THESE_FILTERS,
  NO_SCENES_WITHOUT_SUB_STUDIOS,
  NO_TITLES_MATCH,
  PROVIDER_UNREACHABLE,
  READ_IS_STALE,
  WHISPARR_KEEPS_NO_SCENE_RECORDS,
  WHISPARR_STATUS_NOT_READ,
} from "../common/ui/copy";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import type { MissingPageView } from "../wire/api";

/** Every reason the grid can state. */
export type MissingGridStateKind =
  | "nothingMissing"
  | "everySceneOnThisPageIsOwned"
  | "noScenesWithoutSubStudios"
  | "noScenesMatchTheseFilters"
  | "noTitlesMatch"
  | "providerUnreachable"
  | "whisparrStatusNotRead"
  | "whisparrKeepsNoSceneRecords"
  | "noMetadataProviderConfigured"
  | "noProviderIdForEntity"
  | "noInstanceConnected"
  | "readIsStale";

/** How one reason reads and what it offers. */
export interface MissingGridState {
  /** The sentence, which every caller reads from here rather than choosing its own. */
  readonly sentence: string;
  /** Whether asking again could give a different answer. */
  readonly refreshIsOffered: boolean;
  /** The sentence stands in the grid's place rather than above a page of cards. */
  readonly replacesTheGrid: boolean;
  /** The reason is cleared by dropping the facet selections. */
  readonly clearFiltersIsOffered: boolean;
  /** The reason is cleared by dropping the title search. */
  readonly clearSearchIsOffered: boolean;
}

const NEITHER_ESCAPE = { clearFiltersIsOffered: false, clearSearchIsOffered: false } as const;

/**
 * Every reason, total by type so a kind added to the union fails this build rather than compiling
 * with no decision made about it.
 */
const STATES: Record<MissingGridStateKind, MissingGridState> = {
  nothingMissing: {
    sentence: NOTHING_MISSING,
    refreshIsOffered: false,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  // A page emptied by the owned subtraction, not a catalogue with nothing in it. The count beside
  // the grid still states a size, so claiming the catalogue is empty would contradict it.
  everySceneOnThisPageIsOwned: {
    sentence: EVERY_SCENE_ON_THIS_PAGE_IS_OWNED,
    refreshIsOffered: false,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  noScenesWithoutSubStudios: {
    sentence: NO_SCENES_WITHOUT_SUB_STUDIOS,
    refreshIsOffered: false,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  noScenesMatchTheseFilters: {
    sentence: NO_SCENES_MATCH_THESE_FILTERS,
    refreshIsOffered: false,
    replacesTheGrid: true,
    clearFiltersIsOffered: true,
    clearSearchIsOffered: false,
  },
  noTitlesMatch: {
    sentence: NO_TITLES_MATCH,
    refreshIsOffered: false,
    replacesTheGrid: true,
    clearFiltersIsOffered: false,
    clearSearchIsOffered: true,
  },
  providerUnreachable: {
    sentence: PROVIDER_UNREACHABLE,
    refreshIsOffered: true,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  whisparrStatusNotRead: {
    sentence: WHISPARR_STATUS_NOT_READ,
    refreshIsOffered: true,
    replacesTheGrid: false,
    ...NEITHER_ESCAPE,
  },
  // The same sentence shape as the row above and the opposite answer on a retry: this generation
  // keeps no per-scene record at all, so asking again reads the same absence.
  whisparrKeepsNoSceneRecords: {
    sentence: WHISPARR_KEEPS_NO_SCENE_RECORDS,
    refreshIsOffered: false,
    replacesTheGrid: false,
    ...NEITHER_ESCAPE,
  },
  noMetadataProviderConfigured: {
    sentence: NO_METADATA_PROVIDER_CONFIGURED,
    refreshIsOffered: false,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  noProviderIdForEntity: {
    sentence: NO_PROVIDER_ID_FOR_ENTITY,
    refreshIsOffered: false,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  noInstanceConnected: {
    sentence: NO_INSTANCE_CONNECTED,
    refreshIsOffered: false,
    replacesTheGrid: true,
    ...NEITHER_ESCAPE,
  },
  readIsStale: {
    sentence: READ_IS_STALE,
    refreshIsOffered: true,
    replacesTheGrid: false,
    ...NEITHER_ESCAPE,
  },
};

/** The kinds, so a caller that must cover them all cannot miss one. */
export const MISSING_GRID_STATE_KINDS: readonly MissingGridStateKind[] = Object.keys(
  STATES,
) as MissingGridStateKind[];

/** What the grid knows about itself when it decides what to state. */
export interface MissingGridSituation {
  readonly read: AsyncRead;
  /** The last page that answered, or null before one has. */
  readonly view: MissingPageView | null;
  /** A facet selection is in force. */
  readonly filtersActive: boolean;
  /** A title search is in force. */
  readonly searchActive: boolean;
  /**
   * A studio read without the scenes the provider attributes to its sub-studios.
   *
   * The host's own toggle above this tab, which is why an empty answer here is not the reader
   * owning everything: the scenes sit one level down and were never asked for.
   */
  readonly subStudioContentIsExcluded: boolean;
}

/**
 * Which reason `situation` is in, or null where the cards themselves are the answer.
 *
 * Null covers both a first read still in flight and a page of cards with nothing to say about it.
 */
export function deriveGridState(situation: MissingGridSituation): MissingGridStateKind | null {
  const { read, view } = situation;

  if (read.failed) {
    return read.hasContent ? "readIsStale" : "providerUnreachable";
  }
  if (view === null) {
    return null;
  }

  switch (view.refusal) {
    case "noMetadataProviderConfigured":
      return "noMetadataProviderConfigured";
    case "noProviderIdForEntity":
      return "noProviderIdForEntity";
    case "noInstanceConnected":
      return "noInstanceConnected";
    case "providerUnreachable":
      return "providerUnreachable";
    case "whisparrStatusNotRead":
    case "whisparrKeepsNoSceneRecords":
      // Stated above the cards, so it is only the answer while there are cards to have a status for.
      if (view.cards.length > 0) {
        return view.refusal === "whisparrStatusNotRead"
          ? "whisparrStatusNotRead"
          : "whisparrKeepsNoSceneRecords";
      }
      break;
    case "none":
      break;
  }

  if (view.cards.length > 0) {
    return null;
  }
  // Owned scenes are removed after a page arrives and a page is never topped back up, so a page can
  // empty while the catalogue still holds scenes. Every reason below claims the catalogue itself is
  // empty, and none of them is true while the size says otherwise.
  if (view.catalogueSize > 0) {
    return "everySceneOnThisPageIsOwned";
  }
  if (situation.filtersActive) {
    return "noScenesMatchTheseFilters";
  }
  if (situation.searchActive) {
    return "noTitlesMatch";
  }
  if (situation.subStudioContentIsExcluded) {
    return "noScenesWithoutSubStudios";
  }
  return "nothingMissing";
}

/** The sentence `kind` states. */
export function emptyStateFor(kind: MissingGridStateKind): string {
  return STATES[kind].sentence;
}

/** Whether `kind` offers a way to ask again. */
export function refreshIsOffered(kind: MissingGridStateKind): boolean {
  return STATES[kind].refreshIsOffered;
}

/** Everything `kind` offers, for a caller rendering it. */
export function describeGridState(kind: MissingGridStateKind): MissingGridState {
  return STATES[kind];
}

/** A specified sentence with the provider and entity names its placeholders stand for. */
export function fillNames(sentence: string, provider: string, entity: string): string {
  return sentence.replace("{provider}", provider).replace("{entity}", entity);
}
