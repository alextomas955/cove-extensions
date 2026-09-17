import { describe, expect, it } from "vitest";

import type { SceneDetailView, SceneRefusalKind } from "../wire/api";
import type { WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import { STATE_VOCABULARY } from "../common/ui/stateVocabularyLogic";
import {
  ACTION_DID_NOT_REACH_WHISPARR,
  INSTANCE_OFFERS_NO_QUALITY_PROFILE,
  INSTANCE_OFFERS_NO_ROOT_FOLDER,
  INSTANCE_REFUSED,
  MONITOR_IN_WHISPARR,
  MONITORING_COULD_NOT_BE_READ,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  SCENE_ADD,
  SCENE_EXCLUDE,
  SCENE_IS_ALREADY_IN_WHISPARR,
  SCENE_IS_ON_THE_EXCLUSION_LIST,
  SCENE_MONITOR_NEEDS_AN_ENTRY,
  SCENE_REMOVE_EXCLUSION,
  SCENE_SEARCH,
  SCENE_SEARCH_IS_WITH_WHISPARR,
  SCENE_SEARCH_NEEDS_AN_ENTRY,
  SCENE_SEARCH_NEEDS_MONITORING,
  STOP_MONITORING_IN_WHISPARR,
  WAITING_FOR_WHISPARR,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import {
  deriveSceneControls,
  routeSegmentFor,
  sceneControls,
  sceneReadRefusal,
  sceneVerbRefusalIn,
  searchIsWithWhisparrIn,
  SCENE_CONTROL_KEYS,
  type SceneControlInput,
  type SceneControlKey,
} from "./sceneControlLogic";

/**
 * The three booleans each state is derived from. Named by the state they produce, so a case reads
 * as the state under test rather than as a triple the reader has to decode.
 */
const FACTS_FOR: Record<
  WhisparrEntityState,
  Pick<SceneDetailView, "excluded" | "present" | "monitored">
> = {
  notAdded: { excluded: false, present: false, monitored: null },
  unmonitored: { excluded: false, present: true, monitored: false },
  monitored: { excluded: false, present: true, monitored: true },
  excluded: { excluded: true, present: true, monitored: true },
  statusUnknown: { excluded: false, present: null, monitored: null },
};

/** Every state in the vocabulary, transcribed by hand so a sixth one has no row here. */
const EVERY_STATE: readonly WhisparrEntityState[] = [
  "notAdded",
  "unmonitored",
  "monitored",
  "excluded",
  "statusUnknown",
];

function input(
  state: WhisparrEntityState,
  over: Partial<Omit<SceneControlInput, "view">> = {},
): SceneControlInput {
  return {
    view: {
      refusal: "none",
      qualityName: null,
      qualityProfileName: null,
      cutoffName: null,
      profileReadDidNotComplete: false,
      ...FACTS_FOR[state],
    },
    acting: false,
    actionFailed: false,
    actionRefusal: null,
    searchIsWithWhisparr: false,
    ...over,
  };
}

/** The label of each control, in the order the tab draws them. */
const labels = (state: WhisparrEntityState, over?: Partial<Omit<SceneControlInput, "view">>) =>
  sceneControls(deriveSceneControls(input(state, over))).map((control) => control.label);

/** The reason each control gives, in the same order. */
const reasons = (state: WhisparrEntityState, over?: Partial<Omit<SceneControlInput, "view">>) =>
  sceneControls(deriveSceneControls(input(state, over))).map((control) => control.reason);

/** Which controls carry the accent fill. */
const primaries = (state: WhisparrEntityState) =>
  sceneControls(deriveSceneControls(input(state)))
    .filter((control) => control.variant === "primary")
    .map((control) => control.key);

describe("a scene the instance does not hold", () => {
  it("offers the add control with the accent fill and nothing to hear", () => {
    const controls = deriveSceneControls(input("notAdded"));

    expect(controls.add.label).toBe(SCENE_ADD);
    expect(controls.add.reason).toBeNull();
    expect(controls.add.variant).toBe("primary");
    expect(controls.add.verb).toBe("add");
  });

  it("stops monitor and search, each in its own words, and leaves exclude open", () => {
    expect(reasons("notAdded")).toEqual([
      null,
      SCENE_MONITOR_NEEDS_AN_ENTRY,
      SCENE_SEARCH_NEEDS_AN_ENTRY,
      null,
    ]);
    expect(labels("notAdded")).toEqual([
      SCENE_ADD,
      MONITOR_IN_WHISPARR,
      SCENE_SEARCH,
      SCENE_EXCLUDE,
    ]);
  });
});

describe("a scene the instance holds and is not monitoring", () => {
  it("puts the accent fill on monitor and stops the search for want of monitoring", () => {
    expect(primaries("unmonitored")).toEqual(["monitor"]);
    expect(reasons("unmonitored")).toEqual([
      SCENE_IS_ALREADY_IN_WHISPARR,
      null,
      SCENE_SEARCH_NEEDS_MONITORING,
      null,
    ]);
  });
});

describe("a scene the instance monitors", () => {
  it("carries no accent fill, because every enabled control is a retreat or a spend", () => {
    expect(primaries("monitored")).toEqual([]);
  });

  it("names the unmonitoring half and leaves search and exclude open", () => {
    expect(labels("monitored")).toEqual([
      SCENE_ADD,
      STOP_MONITORING_IN_WHISPARR,
      SCENE_SEARCH,
      SCENE_EXCLUDE,
    ]);
    expect(reasons("monitored")).toEqual([SCENE_IS_ALREADY_IN_WHISPARR, null, null, null]);
    expect(deriveSceneControls(input("monitored")).monitor.verb).toBe("unmonitor");
  });
});

describe("a scene on the instance's exclusion list", () => {
  it("becomes the removing half, with the accent fill, and stops the other three", () => {
    const controls = deriveSceneControls(input("excluded"));

    expect(controls.exclude.label).toBe(SCENE_REMOVE_EXCLUSION);
    expect(controls.exclude.verb).toBe("removeExclusion");
    expect(controls.exclude.reason).toBeNull();
    expect(controls.exclude.variant).toBe("primary");
    expect(reasons("excluded")).toEqual([
      SCENE_IS_ON_THE_EXCLUSION_LIST,
      SCENE_IS_ON_THE_EXCLUSION_LIST,
      SCENE_IS_ON_THE_EXCLUSION_LIST,
      null,
    ]);
  });

  it("never shows both labels of the one exclusion control", () => {
    expect(labels("excluded")).not.toContain(SCENE_EXCLUDE);
    expect(labels("monitored")).not.toContain(SCENE_REMOVE_EXCLUSION);
  });
});

describe("a scene whose state was not established", () => {
  it("stops all four, each carrying a reason of its own, and fills none", () => {
    expect(reasons("statusUnknown")).toEqual([
      MONITORING_COULD_NOT_BE_READ,
      MONITORING_COULD_NOT_BE_READ,
      MONITORING_COULD_NOT_BE_READ,
      MONITORING_COULD_NOT_BE_READ,
    ]);
    expect(primaries("statusUnknown")).toEqual([]);
  });
});

describe("a refusal is stated in one place", () => {
  it("states a reason two or more controls share once, through the notice", () => {
    for (const kind of ["noInstanceConnected", "noIdentityInThisNamespace"] as const) {
      const controls = deriveSceneControls(input("monitored", { actionRefusal: kind }));
      expect(controls.affectedControls).toBe(SCENE_CONTROL_KEYS.length);
      expect(controls.sharedReason).toBe(
        kind === "noInstanceConnected" ? NO_INSTANCE_CONNECTED : NO_IDENTITY_IN_THIS_NAMESPACE,
      );
      expect(
        sceneControls(controls).map((control) => control.reason),
        "a shared reason was repeated on the controls the notice already speaks for",
      ).toEqual([SCENE_IS_ALREADY_IN_WHISPARR, null, null, null]);
    }
  });

  it("puts a reason stopping one control on that control and draws no notice", () => {
    const controls = deriveSceneControls(
      input("notAdded", { actionRefusal: "instanceOffersNoRootFolder" }),
    );

    expect(controls.add.reason).toBe(INSTANCE_OFFERS_NO_ROOT_FOLDER);
    expect(controls.sharedReason).toBeNull();
    expect(controls.affectedControls).toBe(0);
  });

  it("never gives the add control the other setting's sentence", () => {
    expect(
      deriveSceneControls(input("notAdded", { actionRefusal: "instanceOffersNoQualityProfile" }))
        .add.reason,
    ).toBe(INSTANCE_OFFERS_NO_QUALITY_PROFILE);
    expect(
      deriveSceneControls(input("notAdded", { actionRefusal: "instanceOffersNoRootFolder" })).add
        .reason,
    ).not.toBe(INSTANCE_OFFERS_NO_QUALITY_PROFILE);
  });
});

describe("a verb in flight", () => {
  it("states the waiting reason on all four controls, not only the pressed one", () => {
    expect(reasons("monitored", { acting: true })).toEqual([
      WAITING_FOR_WHISPARR,
      WAITING_FOR_WHISPARR,
      WAITING_FOR_WHISPARR,
      WAITING_FOR_WHISPARR,
    ]);
  });

  it("states it in every state, so no state leaves one control pressable mid-flight", () => {
    for (const state of EVERY_STATE) {
      expect(reasons(state, { acting: true }), state).toEqual([
        WAITING_FOR_WHISPARR,
        WAITING_FOR_WHISPARR,
        WAITING_FOR_WHISPARR,
        WAITING_FOR_WHISPARR,
      ]);
    }
  });
});

describe("the status line after a press", () => {
  it("says nothing until something has been pressed", () => {
    expect(deriveSceneControls(input("monitored")).statusLine).toBeNull();
  });

  it("reports a verb that never arrived ahead of anything it might have answered", () => {
    expect(
      deriveSceneControls(
        input("monitored", {
          actionFailed: true,
          actionRefusal: "instanceRefused",
          searchIsWithWhisparr: true,
        }),
      ).statusLine,
    ).toEqual({ sentence: ACTION_DID_NOT_REACH_WHISPARR, failed: true });
  });

  it("reports an instance that answered and declined, in the failure tone", () => {
    expect(
      deriveSceneControls(input("monitored", { actionRefusal: "instanceRefused" })).statusLine,
    ).toEqual({ sentence: INSTANCE_REFUSED, failed: true });
  });

  it("confirms a search only that the instance holds it, and not as a failure", () => {
    expect(
      deriveSceneControls(input("monitored", { searchIsWithWhisparr: true })).statusLine,
    ).toEqual({ sentence: SCENE_SEARCH_IS_WITH_WHISPARR, failed: false });
  });

  it("returns the controls to enabled after a failure, so a failure is retryable", () => {
    expect(reasons("monitored", { actionFailed: true })).toEqual([
      SCENE_IS_ALREADY_IN_WHISPARR,
      null,
      null,
      null,
    ]);
  });
});

describe("the control set covers the vocabulary and fills at most one control", () => {
  it("has a row for every state in the vocabulary and no sixth", () => {
    expect([...EVERY_STATE].sort()).toEqual(Object.keys(STATE_VOCABULARY).sort());
  });

  it("answers the primary variant for at most one control in any state", () => {
    for (const state of EVERY_STATE) {
      expect(primaries(state).length, state).toBeLessThanOrEqual(1);
    }
  });

  it("never fills the search control and never fills the excluding half", () => {
    const filled: SceneControlKey[] = EVERY_STATE.flatMap((state) => [...primaries(state)]);

    expect(
      filled,
      "a search spends indexer traffic and disk, so its fill invites the press",
    ).not.toContain("search");
    for (const state of EVERY_STATE) {
      const controls = deriveSceneControls(input(state));
      expect(
        controls.exclude.variant === "primary" && controls.exclude.label === SCENE_EXCLUDE,
        state,
      ).toBe(false);
    }
  });

  it("names the same four controls in every state", () => {
    for (const state of EVERY_STATE) {
      expect(
        sceneControls(deriveSceneControls(input(state))).map((control) => control.key),
        state,
      ).toEqual(SCENE_CONTROL_KEYS);
    }
  });
});

describe("what a read's own refusal states", () => {
  it("states the read sentence for a refusal a read can answer, over all four surfaces", () => {
    expect(sceneReadRefusal("didNotReachWhisparr")).toEqual({
      sentence: WHISPARR_STATUS_COULD_NOT_BE_READ,
      affectedControls: SCENE_CONTROL_KEYS.length,
    });
  });

  it("states nothing for a healthy read, and for a refusal only a verb answers", () => {
    for (const kind of ["none", "instanceOffersNoRootFolder"] as const) {
      expect(sceneReadRefusal(kind), kind).toEqual({ sentence: null, affectedControls: 0 });
    }
  });
});

describe("what a verb's answer is read for", () => {
  it("reads a refusal this build recognises and nothing else", () => {
    expect(sceneVerbRefusalIn({ refusal: "instanceRefused" })).toBe("instanceRefused");
    expect(sceneVerbRefusalIn({ refusal: "somethingLater" })).toBeNull();
    expect(sceneVerbRefusalIn({})).toBeNull();
    expect(sceneVerbRefusalIn(null)).toBeNull();
  });

  it("reads the search confirmation as the one member that carries it", () => {
    expect(searchIsWithWhisparrIn({ searchIsWithWhisparr: true })).toBe(true);
    expect(searchIsWithWhisparrIn({ searchIsWithWhisparr: false })).toBe(false);
    expect(searchIsWithWhisparrIn({})).toBe(false);
  });
});

describe("each verb names the route it is served at", () => {
  it("names the removing half by the segment the server mounts", () => {
    expect(routeSegmentFor("removeExclusion")).toBe("remove-exclusion");
    expect(routeSegmentFor("unmonitor")).toBe("unmonitor");
  });

  it("names a route for every verb the four controls can carry out", () => {
    const verbs = EVERY_STATE.flatMap((state) =>
      sceneControls(deriveSceneControls(input(state))).map((control) => control.verb),
    );

    for (const verb of verbs) {
      expect(routeSegmentFor(verb), verb).not.toBe("");
    }
    expect(new Set(verbs).size, "the five states between them reach every verb but one").toBe(6);
  });
});

/**
 * The refusal members, transcribed by hand from the server's enum. A list computed from the
 * generated module would agree with it whatever it says.
 */
const EVERY_REFUSAL: readonly SceneRefusalKind[] = [
  "none",
  "noInstanceConnected",
  "noIdentityInThisNamespace",
  "severalIdentitiesInThisNamespace",
  "capabilityAbsentOnThisGeneration",
  "didNotReachWhisparr",
  "instanceRefused",
  "instanceOffersNoQualityProfile",
  "instanceOffersNoRootFolder",
  "noAgreedRootForThisEntity",
  "whisparrHasNoEntryForScene",
  "whisparrAlreadyHoldsThisScene",
  "whisparrIsNotMonitoringThisScene",
];

describe("every refusal the server can answer reaches somewhere", () => {
  it("leaves no member with a place and no sentence, or a sentence and no place", () => {
    for (const kind of EVERY_REFUSAL) {
      const controls = deriveSceneControls(input("monitored", { actionRefusal: kind }));
      const stated =
        controls.sharedReason !== null ||
        controls.statusLine !== null ||
        controls.add.reason !== SCENE_IS_ALREADY_IN_WHISPARR;

      // The three members naming a state say nothing of their own: the re-read that follows every
      // verb puts that state on the chip and on the control reasons.
      const theFactsSayIt = [
        "none",
        "whisparrHasNoEntryForScene",
        "whisparrAlreadyHoldsThisScene",
        "whisparrIsNotMonitoringThisScene",
      ].includes(kind);

      expect(stated, kind).toBe(!theFactsSayIt);
    }
  });

  it("recognises every member as one of its own", () => {
    for (const kind of EVERY_REFUSAL) {
      expect(sceneVerbRefusalIn({ refusal: kind }), kind).toBe(kind);
    }
  });
});
