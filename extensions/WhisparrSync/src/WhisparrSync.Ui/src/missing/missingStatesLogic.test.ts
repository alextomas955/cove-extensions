import { describe, expect, it } from "vitest";

import {
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
import type { MissingCard, MissingPageView, MissingRefusalKind } from "../wire/api";
import {
  MISSING_GRID_STATE_KINDS,
  deriveGridState,
  describeGridState,
  emptyStateFor,
  refreshIsOffered,
  type MissingGridSituation,
} from "./missingStatesLogic";

const CARD: MissingCard = {
  providerSceneId: "9d1f0e5c-0000-4000-8000-000000000001",
  title: "A scene the library does not hold",
  releaseDate: "2019-04-02",
  coverUrl: null,
  studioName: "Brazzers Exxtra",
  description: null,
  performers: [],
  tags: [],
  performerCount: 0,
  tagCount: 0,
  state: "notAdded",
};

function pageOf(cards: MissingCard[], refusal: MissingRefusalKind): MissingPageView {
  return {
    cards,
    catalogueSize: cards.length,
    sizeIsLowerBound: false,
    page: 1,
    perPage: 40,
    lastPage: 1,
    rangeFrom: cards.length === 0 ? 0 : 1,
    rangeTo: cards.length,
    refusal,
    facets: [],
    sorts: [],
    sortInForce: null,
    statusWasRead: refusal === "none",
    statusIsPermanentlyAbsent: refusal === "whisparrKeepsNoSceneRecords",
    monitorAllIsOffered: true,
  };
}

function situation(over: Partial<MissingGridSituation> = {}): MissingGridSituation {
  return {
    read: { reading: false, failed: false, hasContent: true },
    view: pageOf([], "none"),
    filtersActive: false,
    searchActive: false,
    subStudioContentIsExcluded: false,
    ...over,
  };
}

describe("every situation the grid cannot show cards in says which one it is", () => {
  it("says the catalogue is fully owned when the read succeeded and nothing narrows it", () => {
    const kind = deriveGridState(situation());

    expect(kind).toBe("nothingMissing");
    expect(emptyStateFor("nothingMissing")).toBe(NOTHING_MISSING);
    expect(refreshIsOffered("nothingMissing")).toBe(false);
  });

  it("says the scenes sit under the sub-studios when a studio was read without them", () => {
    const kind = deriveGridState(situation({ subStudioContentIsExcluded: true }));

    expect(kind).toBe("noScenesWithoutSubStudios");
    expect(emptyStateFor("noScenesWithoutSubStudios")).toBe(NO_SCENES_WITHOUT_SUB_STUDIOS);
    expect(refreshIsOffered("noScenesWithoutSubStudios")).toBe(false);
  });

  it("says the filters match nothing rather than that the reader owns everything", () => {
    const kind = deriveGridState(situation({ filtersActive: true }));

    expect(kind).toBe("noScenesMatchTheseFilters");
    expect(emptyStateFor(kind!)).toBe(NO_SCENES_MATCH_THESE_FILTERS);
    expect(describeGridState("noScenesMatchTheseFilters").clearFiltersIsOffered).toBe(true);
    expect(refreshIsOffered("noScenesMatchTheseFilters")).toBe(false);
  });

  it("says the search matches nothing, and offers a way to clear it", () => {
    const kind = deriveGridState(situation({ searchActive: true }));

    expect(kind).toBe("noTitlesMatch");
    expect(emptyStateFor("noTitlesMatch")).toBe(NO_TITLES_MATCH);
    expect(describeGridState("noTitlesMatch").clearSearchIsOffered).toBe(true);
    expect(refreshIsOffered("noTitlesMatch")).toBe(false);
  });

  it("offers a retry when the provider did not answer", () => {
    const kind = deriveGridState(situation({ view: pageOf([], "providerUnreachable") }));

    expect(kind).toBe("providerUnreachable");
    expect(emptyStateFor("providerUnreachable")).toBe(PROVIDER_UNREACHABLE);
    expect(refreshIsOffered("providerUnreachable")).toBe(true);
  });

  it("names the metadata source Cove has not been given", () => {
    const kind = deriveGridState(situation({ view: pageOf([], "noMetadataProviderConfigured") }));

    expect(kind).toBe("noMetadataProviderConfigured");
    expect(emptyStateFor("noMetadataProviderConfigured")).toBe(NO_METADATA_PROVIDER_CONFIGURED);
    expect(refreshIsOffered("noMetadataProviderConfigured")).toBe(false);
  });

  it("says there is no catalogue to check when the entity carries no provider id", () => {
    const kind = deriveGridState(situation({ view: pageOf([], "noProviderIdForEntity") }));

    expect(kind).toBe("noProviderIdForEntity");
    expect(emptyStateFor("noProviderIdForEntity")).toBe(NO_PROVIDER_ID_FOR_ENTITY);
    expect(refreshIsOffered("noProviderIdForEntity")).toBe(false);
  });

  it("names the connection that is missing rather than leaving the grid blank", () => {
    const kind = deriveGridState(situation({ view: pageOf([], "noInstanceConnected") }));

    expect(kind).toBe("noInstanceConnected");
    expect(emptyStateFor("noInstanceConnected")).toBe(NO_INSTANCE_CONNECTED);
    expect(refreshIsOffered("noInstanceConnected")).toBe(false);
  });

  it("does not blame the sub-studios when nothing is connected", () => {
    // A studio read without its sub-studios AND with no instance connected. Both situations are in
    // force at once, and only one of them is the cause. The sub-studio sentence is named as the
    // string that must not appear: a case asserting only the right sentence would have passed
    // against a surface that reached both answers through different code.
    const kind = deriveGridState(
      situation({
        view: pageOf([], "noInstanceConnected"),
        subStudioContentIsExcluded: true,
      }),
    );
    const stated = emptyStateFor(kind ?? "nothingMissing");

    expect(stated).not.toBe(NO_SCENES_WITHOUT_SUB_STUDIOS);
    expect(stated).toBe(NO_INSTANCE_CONNECTED);
  });

  it("keeps the list and says it is stale when a refresh failed over it", () => {
    const kind = deriveGridState(
      situation({
        read: { reading: false, failed: true, hasContent: true },
        view: pageOf([CARD], "none"),
      }),
    );

    expect(kind).toBe("readIsStale");
    expect(emptyStateFor("readIsStale")).toBe(READ_IS_STALE);
    expect(refreshIsOffered("readIsStale")).toBe(true);
    expect(describeGridState("readIsStale").replacesTheGrid).toBe(false);
  });

  it("replaces the grid when the first read failed with nothing on screen", () => {
    const kind = deriveGridState(
      situation({ read: { reading: false, failed: true, hasContent: false }, view: null }),
    );

    expect(kind).toBe("providerUnreachable");
    expect(describeGridState("providerUnreachable").replacesTheGrid).toBe(true);
  });

  it("states nothing while the first read is still in flight", () => {
    expect(
      deriveGridState(
        situation({ read: { reading: true, failed: false, hasContent: false }, view: null }),
      ),
    ).toBeNull();
  });

  it("states nothing when the page carries cards and the read was whole", () => {
    expect(deriveGridState(situation({ view: pageOf([CARD], "none") }))).toBeNull();
  });
});

describe("the two Whisparr cases differ only in whether asking again could help", () => {
  const transient = deriveGridState(situation({ view: pageOf([CARD], "whisparrStatusNotRead") }));
  const permanent = deriveGridState(
    situation({ view: pageOf([CARD], "whisparrKeepsNoSceneRecords") }),
  );

  it("keeps the whole catalogue on screen in both", () => {
    expect(transient).toBe("whisparrStatusNotRead");
    expect(permanent).toBe("whisparrKeepsNoSceneRecords");
    expect(describeGridState(transient!).replacesTheGrid).toBe(false);
    expect(describeGridState(permanent!).replacesTheGrid).toBe(false);
  });

  it("offers a retry for the transient one and none for the permanent one", () => {
    expect(refreshIsOffered("whisparrStatusNotRead")).toBe(true);
    expect(refreshIsOffered("whisparrKeepsNoSceneRecords")).toBe(false);
    expect(emptyStateFor("whisparrStatusNotRead")).toBe(WHISPARR_STATUS_NOT_READ);
    expect(emptyStateFor("whisparrKeepsNoSceneRecords")).toBe(WHISPARR_KEEPS_NO_SCENE_RECORDS);
  });

  it("states the empty reason instead when the page carries no card to have a status for", () => {
    expect(deriveGridState(situation({ view: pageOf([], "whisparrStatusNotRead") }))).toBe(
      "nothingMissing",
    );
  });
});

describe("the vocabulary itself", () => {
  it("states a sentence for every kind", () => {
    for (const kind of MISSING_GRID_STATE_KINDS) {
      expect(emptyStateFor(kind), kind).not.toBe("");
    }
  });

  it("never both replaces the grid and keeps cards", () => {
    for (const kind of MISSING_GRID_STATE_KINDS) {
      const state = describeGridState(kind);
      const keepsCards = kind === "readIsStale" || kind.startsWith("whisparr");
      expect(state.replacesTheGrid, kind).toBe(!keepsCards);
    }
  });

  it("offers an escape only where one would clear the reason", () => {
    for (const kind of MISSING_GRID_STATE_KINDS) {
      const state = describeGridState(kind);
      expect(state.clearFiltersIsOffered && state.clearSearchIsOffered, kind).toBe(false);
    }
  });
});
