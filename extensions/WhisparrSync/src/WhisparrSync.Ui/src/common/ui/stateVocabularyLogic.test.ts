import { describe, expect, it } from "vitest";

import {
  deriveState,
  describeState,
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
  unmonitored: "circle",
  notAdded: "circleDashed",
  excluded: "ban",
  statusUnknown: "circleQuestion",
};

// The legend's labels, transcribed by hand.
const EXPECTED_LABEL: Record<WhisparrEntityState, string> = {
  monitored: "Monitored",
  unmonitored: "Unmonitored",
  notAdded: "Not added",
  excluded: "Excluded",
  statusUnknown: "Status unknown",
};

// The marker that is not a state, so no entry may carry it.
const IN_LIBRARY_ICON_KEY = "download";

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

  it("gives every state a mark and a label with something in them", () => {
    for (const state of STATES) {
      const { iconKey, label } = describeState(state);
      expect(iconKey.trim(), state).not.toBe("");
      expect(label.trim(), state).not.toBe("");
    }
  });

  it("gives no two states the same mark or the same label", () => {
    expect(new Set(STATES.map((s) => describeState(s).iconKey)).size).toBe(STATES.length);
    expect(new Set(STATES.map((s) => describeState(s).label)).size).toBe(STATES.length);
  });

  it("still tells two states apart when they share a tint", () => {
    const sharedTint = STATES.filter((s) => describeState(s).variant === "gray");
    expect(sharedTint.length).toBeGreaterThan(1);
    expect(new Set(sharedTint.map((s) => describeState(s).iconKey)).size).toBe(sharedTint.length);
    expect(new Set(sharedTint.map((s) => describeState(s).label)).size).toBe(sharedTint.length);
  });

  it("gives no state the in-library marker, which is not a state", () => {
    for (const state of STATES) {
      expect(describeState(state).iconKey, state).not.toBe(IN_LIBRARY_ICON_KEY);
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
