/**
 * The state vocabulary as the spec states it.
 *
 * Every glyph is asserted by CODE POINT rather than by appearance, because a lookalike substitution -
 * a hyphen-minus for the en dash, a bullet for the filled circle, a slashed zero for the circled
 * division slash - is invisible in a diff and in a review, and renders as something the spec does not
 * specify. The code points below were transcribed by hand from the spec's own legend.
 */
import { describe, expect, it } from "vitest";

import {
  deriveState,
  describeState,
  renameState,
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

/** The legend, transcribed by hand: each state's glyph as a Unicode scalar value. */
const EXPECTED_CODE_POINT: Record<WhisparrEntityState, number> = {
  monitored: 0x25cf, // BLACK CIRCLE
  unmonitored: 0x25cb, // WHITE CIRCLE
  notAdded: 0x2013, // EN DASH
  excluded: 0x2298, // CIRCLED DIVISION SLASH
  statusUnknown: 0x003f, // QUESTION MARK
};

/** The legend's labels, transcribed by hand. */
const EXPECTED_LABEL: Record<WhisparrEntityState, string> = {
  monitored: "Monitored",
  unmonitored: "Unmonitored",
  notAdded: "Not added",
  excluded: "Excluded",
  statusUnknown: "Status unknown",
};

/** The marker the spec declares is not a state, so no entry may carry it. */
const IN_LIBRARY_CODE_POINT = 0x25c6; // BLACK DIAMOND

const PRESENT_AND_MONITORED: EntityStateInput = {
  excluded: false,
  present: true,
  monitored: true,
};

describe("the transcribed vocabulary", () => {
  it("holds exactly the five states", () => {
    expect(Object.keys(STATE_VOCABULARY).sort()).toEqual([...STATES].sort());
  });

  it("gives every state the glyph the legend specifies, by code point", () => {
    for (const state of STATES) {
      // Whole-string equality rather than a first-code-point read, so a lookalike followed by a
      // variation selector is caught too.
      expect(describeState(state).glyph, state).toBe(
        String.fromCodePoint(EXPECTED_CODE_POINT[state]),
      );
    }
  });

  it("gives every state the label the legend specifies", () => {
    for (const state of STATES) {
      expect(describeState(state).label, state).toBe(EXPECTED_LABEL[state]);
    }
  });

  it("gives every state a glyph and a label with something in them", () => {
    for (const state of STATES) {
      const { glyph, label } = describeState(state);
      expect(glyph.length, state).toBeGreaterThanOrEqual(1);
      expect(label.trim(), state).not.toBe("");
    }
  });

  it("gives no two states the same glyph or the same label", () => {
    expect(new Set(STATES.map((s) => describeState(s).glyph)).size).toBe(STATES.length);
    expect(new Set(STATES.map((s) => describeState(s).label)).size).toBe(STATES.length);
  });

  it("still tells two states apart when they share a tint", () => {
    const sharedTint = STATES.filter((s) => describeState(s).variant === "gray");
    expect(sharedTint.length).toBeGreaterThan(1);
    expect(new Set(sharedTint.map((s) => describeState(s).glyph)).size).toBe(sharedTint.length);
    expect(new Set(sharedTint.map((s) => describeState(s).label)).size).toBe(sharedTint.length);
  });

  it("gives no state the in-library marker, which is not a state", () => {
    for (const state of STATES) {
      expect(describeState(state).glyph.codePointAt(0), state).not.toBe(IN_LIBRARY_CODE_POINT);
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

describe("a view that renames a state", () => {
  it("keeps the glyph so both read as the same underlying fact", () => {
    expect(renameState("monitored", "Wanted").glyph).toBe(describeState("monitored").glyph);
  });

  it("keeps the tint and takes the new label", () => {
    const renamed = renameState("monitored", "Wanted");
    expect(renamed.label).toBe("Wanted");
    expect(renamed.variant).toBe(describeState("monitored").variant);
  });

  it("leaves the shared entry alone", () => {
    renameState("monitored", "Wanted");
    expect(describeState("monitored").label).toBe(EXPECTED_LABEL.monitored);
  });
});
