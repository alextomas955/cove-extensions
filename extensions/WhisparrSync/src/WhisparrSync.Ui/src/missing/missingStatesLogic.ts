/**
 * Which of the grid's stated reasons is in force, and what each one offers.
 *
 * A grid with no cards always names a cause, and only a cause a retry could clear offers one.
 */
import {
  ENTITY_NOT_IN_WHISPARR,
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
  WHISPARR_CATALOGUE_NOT_READ,
  WHISPARR_KEEPS_NO_SCENE_RECORDS,
  WHISPARR_STATUS_NOT_READ,
} from "../common/ui/copy";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import type { MissingPageView } from "../wire/api";

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
  | "entityNotInWhisparr"
  | "whisparrCatalogueNotRead"
  | "readIsStale";

export interface MissingGridState {
  readonly sentence: string;
  /** Whether asking again could give a different answer. */
  readonly refreshIsOffered: boolean;
  /** The sentence stands in the grid's place rather than above a page of cards. */
  readonly replacesTheGrid: boolean;
  /** The reason is cleared by dropping the facet selections. */
  readonly clearFiltersIsOffered: boolean;
  /** The reason is cleared by dropping the title search. */
  readonly clearSearchIsOffered: boolean;
  /** The reason is cleared by adding the entity to Whisparr, which the surface offers. */
  readonly addIsOffered: boolean;
}

const NEITHER_ESCAPE = {
  clearFiltersIsOffered: false,
  clearSearchIsOffered: false,
  addIsOffered: false,
} as const;

// Total by type, so a kind added to the union fails this build.
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
    addIsOffered: false,
  },
  noTitlesMatch: {
    sentence: NO_TITLES_MATCH,
    refreshIsOffered: false,
    replacesTheGrid: true,
    clearFiltersIsOffered: false,
    clearSearchIsOffered: true,
    addIsOffered: false,
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
  // No retry, unlike the row above: this generation keeps no per-scene record at all, so asking
  // again reads the same absence.
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
  // The one state a reader clears by adding the entity: Whisparr lists scenes only for an entity it
  // holds, so nothing else here can produce a list.
  entityNotInWhisparr: {
    sentence: ENTITY_NOT_IN_WHISPARR,
    refreshIsOffered: false,
    replacesTheGrid: true,
    clearFiltersIsOffered: false,
    clearSearchIsOffered: false,
    addIsOffered: true,
  },
  whisparrCatalogueNotRead: {
    sentence: WHISPARR_CATALOGUE_NOT_READ,
    refreshIsOffered: true,
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

export const MISSING_GRID_STATE_KINDS: readonly MissingGridStateKind[] = Object.keys(
  STATES,
) as MissingGridStateKind[];

export interface MissingGridSituation {
  readonly read: AsyncRead;
  /** The last page that answered, or null before one has. */
  readonly view: MissingPageView | null;
  readonly filtersActive: boolean;
  readonly searchActive: boolean;
  /**
   * A studio read without the scenes the provider attributes to its sub-studios. It is the
   * host's own toggle, so an empty answer here is not the reader owning everything: the scenes
   * sit one level down and were never asked for.
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
    case "entityNotInWhisparr":
      return "entityNotInWhisparr";
    case "whisparrCatalogueNotRead":
      return "whisparrCatalogueNotRead";
    case "whisparrStatusNotRead":
    case "whisparrKeepsNoSceneRecords":
      // Stated above the cards, so it is only the answer while there are cards.
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
  // Owned scenes are removed after a page arrives and a page is never topped back up, so a page
  // can empty while the catalogue still holds scenes. Every reason below claims the catalogue is
  // empty, which the stated size would contradict.
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

export function emptyStateFor(kind: MissingGridStateKind): string {
  return STATES[kind].sentence;
}

export function refreshIsOffered(kind: MissingGridStateKind): boolean {
  return STATES[kind].refreshIsOffered;
}

export function describeGridState(kind: MissingGridStateKind): MissingGridState {
  return STATES[kind];
}

/** Fills the `{provider}` and `{entity}` placeholders of a sentence. */
export function fillNames(sentence: string, provider: string, entity: string): string {
  return sentence.replace("{provider}", provider).replace("{entity}", entity);
}
