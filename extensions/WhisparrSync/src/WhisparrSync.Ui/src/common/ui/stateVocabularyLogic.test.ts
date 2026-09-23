import { describe, expect, it } from "vitest";

import {
  deriveState,
  describeState,
  FILE_MARKER,
  NOT_LINKED_MARKER,
  STATE_VOCABULARY,
  type EntityStateInput,
  type WhisparrEntityState,
} from "./stateVocabularyLogic";

const STATES: readonly WhisparrEntityState[] = [
  "monitored",
  "unmonitored",
  "notAdded",
  "excluded",
  "statusUnknown",
];

// Transcribed by hand from the spec's legend: the mark each state is named by.
const EXPECTED_ICON_KEY: Record<WhisparrEntityState, string> = {
  monitored: "bookmark",
  unmonitored: "bookmarkMinus",
  notAdded: "circleDashed",
  excluded: "ban",
  statusUnknown: "circleQuestion",
};

// The legend's tints, transcribed by hand.
const EXPECTED_VARIANT: Record<WhisparrEntityState, string> = {
  monitored: "green",
  unmonitored: "gray",
  notAdded: "cyan",
  excluded: "red",
  statusUnknown: "amber",
};

// The legend's labels, transcribed by hand.
const EXPECTED_LABEL: Record<WhisparrEntityState, string> = {
  monitored: "Monitored",
  unmonitored: "Unmonitored",
  notAdded: "Not added",
  excluded: "Excluded",
  statusUnknown: "Status unknown",
};

// Every badge the product draws, states and markers alike. A reader meets them side by side on one
// row, so the set is checked as a whole rather than the states on their own.
const BADGES = [...STATES.map((state) => describeState(state)), FILE_MARKER, NOT_LINKED_MARKER];

const PRESENT_AND_MONITORED: EntityStateInput = {
  excluded: false,
  present: true,
  monitored: true,
};

describe("the transcribed vocabulary", () => {
  it("holds exactly the five states", () => {
    expect(Object.keys(STATE_VOCABULARY).sort()).toEqual([...STATES].sort());
  });

  it("gives every state the mark the legend specifies", () => {
    for (const state of STATES) {
      expect(describeState(state).iconKey, state).toBe(EXPECTED_ICON_KEY[state]);
    }
  });

  it("gives every state the label the legend specifies", () => {
    for (const state of STATES) {
      expect(describeState(state).label, state).toBe(EXPECTED_LABEL[state]);
    }
  });

  it("gives every state the tint the legend specifies", () => {
    for (const state of STATES) {
      expect(describeState(state).variant, state).toBe(EXPECTED_VARIANT[state]);
    }
  });

  // The row draws all of these at once. Two sharing a mark or a tint there leaves the reader
  // telling them apart by the label alone.
  it("gives no two badges the same mark, tint or label", () => {
    expect(new Set(BADGES.map((badge) => badge.iconKey)).size).toBe(BADGES.length);
    expect(new Set(BADGES.map((badge) => badge.variant)).size).toBe(BADGES.length);
    expect(new Set(BADGES.map((badge) => badge.label)).size).toBe(BADGES.length);
  });

  it("gives every badge a mark and a label with something in them", () => {
    for (const badge of BADGES) {
      expect(badge.iconKey.trim(), badge.label).not.toBe("");
      expect(badge.label.trim()).not.toBe("");
    }
  });
});

describe("deriving a state", () => {
  it("reads an excluded entity as excluded even when it is also absent", () => {
    expect(deriveState({ excluded: true, present: false, monitored: null })).toBe("excluded");
  });

  it("reads an excluded entity as excluded even when Whisparr monitors it", () => {
    expect(deriveState({ excluded: true, present: true, monitored: true })).toBe("excluded");
  });

  it("reads an absent entity as not added", () => {
    expect(deriveState({ excluded: false, present: false, monitored: null })).toBe("notAdded");
  });

  it("claims nothing when presence or the flag could not be established", () => {
    expect(deriveState({ excluded: false, present: null, monitored: null })).toBe("statusUnknown");
    expect(deriveState({ excluded: false, present: true, monitored: null })).toBe("statusUnknown");
  });

  it("reads the monitored flag as the axis", () => {
    expect(deriveState(PRESENT_AND_MONITORED)).toBe("monitored");
    expect(deriveState({ excluded: false, present: true, monitored: false })).toBe("unmonitored");
  });

  it("holds nothing between calls", () => {
    const first = deriveState(PRESENT_AND_MONITORED);
    deriveState({ excluded: true, present: false, monitored: null });
    expect(deriveState(PRESENT_AND_MONITORED)).toBe(first);
  });
});
