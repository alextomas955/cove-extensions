import { describe, expect, it } from "vitest";

import {
  ACTION_DID_NOT_REACH_WHISPARR,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  SEARCH_WITH_NO_ENTRY,
} from "../common/ui/copy";
import type { MissingCard, MissingPerformerChip, MissingSceneActionRefusal } from "../wire/api";
import {
  CARD_ACTION_AT_REST,
  cardFailureLine,
  deriveCardRows,
  displayedState,
  overflowChipCount,
  sceneActionIn,
  visiblePerformerChips,
} from "./missingCardLogic";

function chip(id: string): MissingPerformerChip {
  return { providerPerformerId: id, name: `Performer ${id}`, imageUrl: null };
}

function scene(overrides: Partial<MissingCard> = {}): MissingCard {
  return {
    providerSceneId: "scene-1",
    title: "A title",
    releaseDate: "2026-01-02",
    coverUrl: "https://provider.example/cover.jpg",
    studioName: "A studio",
    description: "A description",
    performers: [],
    tags: [],
    performerCount: 0,
    tagCount: 0,
    state: "notAdded",
    ...overrides,
  };
}

describe("a row the provider gave no value for is omitted", () => {
  it("omits the date and studio row when the scene names neither", () => {
    expect(deriveCardRows(scene({ releaseDate: null, studioName: null })).meta).toBeNull();
  });

  it("keeps the row when only one half is known", () => {
    expect(deriveCardRows(scene({ releaseDate: null })).meta).toEqual({
      releaseDate: null,
      studioName: "A studio",
    });
  });

  it("omits the description row when the scene carries none", () => {
    expect(deriveCardRows(scene({ description: null })).description).toBeNull();
  });

  it("omits a description that is present and blank", () => {
    expect(deriveCardRows(scene({ description: "   " })).description).toBeNull();
  });

  it("omits the chips row when the scene names no performer", () => {
    expect(deriveCardRows(scene({ performers: [] })).performers).toBeNull();
  });

  it("omits the counts row when the provider counted neither", () => {
    expect(deriveCardRows(scene()).counts).toBeNull();
  });

  it("names only the half that counts something, singular at one", () => {
    expect(deriveCardRows(scene({ performerCount: 1, tagCount: 0 })).counts).toBe("1 performer");
    expect(deriveCardRows(scene({ performerCount: 0, tagCount: 3 })).counts).toBe("3 tags");
    expect(deriveCardRows(scene({ performerCount: 2, tagCount: 1 })).counts).toBe(
      "2 performers · 1 tag",
    );
  });
});

describe("performer chips are capped and the rest become a count", () => {
  it("draws at most four", () => {
    const performers = ["a", "b", "c", "d", "e", "f"].map(chip);
    expect(visiblePerformerChips(performers)).toHaveLength(4);
    expect(visiblePerformerChips(performers).map((one) => one.providerPerformerId)).toEqual([
      "a",
      "b",
      "c",
      "d",
    ]);
  });

  it("counts the remainder, and counts nothing at or below the cap", () => {
    expect(overflowChipCount(["a", "b", "c", "d", "e", "f"].map(chip))).toBe(2);
    expect(overflowChipCount(["a", "b", "c", "d"].map(chip))).toBe(0);
    expect(overflowChipCount([])).toBe(0);
  });
});

describe("what is stated beneath the action row", () => {
  const EXPECTED: Record<MissingSceneActionRefusal, { sentence: string; kind: string } | null> = {
    none: null,
    didNotReachWhisparr: { sentence: ACTION_DID_NOT_REACH_WHISPARR, kind: "error" },
    instanceRefused: { sentence: INSTANCE_REFUSED, kind: "error" },
    instanceOffersNoQualityProfile: {
      sentence: INSTANCE_OFFERS_NO_QUALITY_PROFILE,
      kind: "error",
    },
    instanceOffersNoRootFolder: { sentence: INSTANCE_OFFERS_NO_ROOT_FOLDER, kind: "error" },
    whisparrHasNoEntryForScene: { sentence: SEARCH_WITH_NO_ENTRY, kind: "muted" },
  };

  it("reads every outcome as the sentence copy declares for it", () => {
    for (const [refusal, expected] of Object.entries(EXPECTED)) {
      expect(
        cardFailureLine({
          ...CARD_ACTION_AT_REST,
          refusal: refusal as MissingSceneActionRefusal,
        }),
        refusal,
      ).toEqual(expected);
    }
  });

  it("reads the no-entry answer as muted and every other outcome as an error", () => {
    for (const [refusal, expected] of Object.entries(EXPECTED)) {
      if (expected === null) continue;
      expect(expected.kind, refusal).toBe(
        refusal === "whisparrHasNoEntryForScene" ? "muted" : "error",
      );
    }
  });

  it("states nothing at rest", () => {
    expect(cardFailureLine(CARD_ACTION_AT_REST)).toBeNull();
  });

  it("reads a request that produced no answer the same as one that reached nothing", () => {
    expect(cardFailureLine({ ...CARD_ACTION_AT_REST, failed: true })).toEqual({
      sentence: ACTION_DID_NOT_REACH_WHISPARR,
      kind: "error",
    });
  });
});

describe("reading one press's answer", () => {
  it("reads a whole answer", () => {
    expect(sceneActionIn({ state: "monitored", refusal: "none" })).toEqual({
      state: "monitored",
      refusal: "none",
    });
  });

  it("reads nothing from an answer that names neither member", () => {
    expect(sceneActionIn({})).toBeNull();
    expect(sceneActionIn(null)).toBeNull();
    expect(sceneActionIn("monitored")).toBeNull();
    expect(sceneActionIn({ state: "monitored" })).toBeNull();
  });
});

describe("a press that did not take leaves the card as it was", () => {
  it("draws the optimistic state only while the press is unsettled", () => {
    const pressed = {
      ...CARD_ACTION_AT_REST,
      inFlight: "monitor" as const,
      optimistic: "monitored" as const,
    };
    expect(displayedState("notAdded", pressed)).toBe("monitored");
  });

  it("returns to the pre-press state on a refusal, and stays there on a failure", () => {
    const before = "notAdded" as const;
    const pressed = {
      ...CARD_ACTION_AT_REST,
      inFlight: "monitor" as const,
      optimistic: "monitored" as const,
    };
    expect(displayedState(before, pressed)).not.toBe(before);

    const refused = { ...CARD_ACTION_AT_REST, refusal: "instanceRefused" as const };
    expect(displayedState(before, refused)).toBe(before);

    const failed = { ...CARD_ACTION_AT_REST, failed: true };
    expect(displayedState(before, failed)).toBe(before);
  });

  it("draws what the instance answered once a press has taken", () => {
    expect(displayedState("monitored", CARD_ACTION_AT_REST)).toBe("monitored");
  });
});
