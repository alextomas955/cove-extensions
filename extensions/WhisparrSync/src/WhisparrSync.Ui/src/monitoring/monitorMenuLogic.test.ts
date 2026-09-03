import { describe, expect, it } from "vitest";
import type {
  EntityMonitoringView,
  MonitorRefusalKind,
  WhisparrCapability,
  WhisparrEntityKind,
  WhisparrGeneration,
} from "../wire/api";
import {
  CAP_UNAVAILABLE_ON_THIS_GENERATION,
  ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE,
  ALL_SCENES_MARKS_THE_BACK_CATALOGUE,
  PERFORMER_HAS_NO_FUTURE_ONLY_SCOPE,
  SCOPE_ALL_SCENES,
  SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED,
  SCOPE_FUTURE_SCENES,
  UNMONITORING_DOES_NOT_RETRACT,
  WAITING_FOR_WHISPARR,
} from "../common/ui/copy";
import {
  capabilityBehindAction,
  describeMonitorRefusal,
  monitorMenu,
  CAPABILITY_ORDER,
  ENTITY_KINDS,
  GENERATIONS,
  MONITOR_REFUSAL_KINDS,
  SCOPE_ORDER,
  SECONDARY_ACTIONS,
  type MonitorMenuItem,
  type SecondaryAction,
} from "./monitorMenuLogic";

/**
 * Every capability the wire enum carries, so a menu built from it offers everything it could offer.
 * The gaps are asserted by taking members away from this list, never by adding one to a short one.
 */
const EVERY_CAPABILITY: WhisparrCapability[] = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "monitorPerformer",
  "registerMissingScenes",
  "reflectOwnedFiles",
  "searchMonitored",
];

function view(
  over: Partial<EntityMonitoringView> & { kind: WhisparrEntityKind },
): EntityMonitoringView {
  return {
    generation: "v3",
    monitored: false,
    refusal: "none",
    capabilities: EVERY_CAPABILITY,
    ...over,
  };
}

function withoutCapability(absent: WhisparrCapability): WhisparrCapability[] {
  return EVERY_CAPABILITY.filter((capability) => capability !== absent);
}

function secondaries(items: readonly MonitorMenuItem[]): readonly SecondaryAction[] {
  return items.flatMap((item) => (item.item === "secondary" ? [item.action] : []));
}

function scopeLabels(items: readonly MonitorMenuItem[]): readonly string[] {
  return items.flatMap((item) => (item.item === "scope" ? [item.label] : []));
}

describe("the item set is written down for every combination the wire enums allow", () => {
  it("names each wire enum's members against a hand-written count", () => {
    expect(CAPABILITY_ORDER).toHaveLength(6);
    expect(ENTITY_KINDS).toHaveLength(2);
    expect(GENERATIONS).toHaveLength(2);
    expect(MONITOR_REFUSAL_KINDS).toHaveLength(8);
    expect(SCOPE_ORDER).toHaveLength(2);
    expect(SECONDARY_ACTIONS).toHaveLength(3);
  });

  it("offers no secondary action at all until the entity is monitored", () => {
    for (const generation of GENERATIONS) {
      for (const kind of ENTITY_KINDS) {
        const menu = monitorMenu(view({ kind, generation, monitored: false }), false);
        expect(secondaries(menu.items), `${generation} ${kind}`).toEqual([]);
      }
    }
  });

  it("offers all three secondary actions on every monitored entity", () => {
    for (const generation of GENERATIONS) {
      for (const kind of ENTITY_KINDS) {
        const menu = monitorMenu(view({ kind, generation, monitored: true }), false);
        expect(secondaries(menu.items), `${generation} ${kind}`).toEqual([
          "addAllMissing",
          "reflectOwned",
          "searchAllMonitored",
        ]);
      }
    }
  });
});

describe("the studio menu", () => {
  it("holds exactly the two scope options while it is not monitored", () => {
    const menu = monitorMenu(view({ kind: "studio", monitored: false }), false);

    expect(menu.available).toBe(true);
    expect(menu.items.map((item) => item.item)).toEqual(["scope", "scope"]);
  });

  it("holds the scope pair, the unmonitor item and the three actions once it is monitored", () => {
    const menu = monitorMenu(view({ kind: "studio", monitored: true }), false);

    expect(menu.items.map((item) => item.item)).toEqual([
      "scope",
      "scope",
      "unmonitor",
      "secondary",
      "secondary",
      "secondary",
    ]);
  });

  it("names the three actions against hand-written literals", () => {
    const menu = monitorMenu(view({ kind: "studio", generation: "v3", monitored: true }), false);

    expect(secondaries(menu.items)).toEqual([
      "addAllMissing",
      "reflectOwned",
      "searchAllMonitored",
    ]);
    expect(menu.items.flatMap((item) => (item.item === "secondary" ? [item.label] : []))).toEqual([
      "Add all missing",
      "Reflect owned",
      "Search all monitored",
    ]);
  });

  it("states at the unmonitor item what unmonitoring does not undo", () => {
    const menu = monitorMenu(view({ kind: "studio", monitored: true }), false);
    const unmonitor = menu.items.find((item) => item.item === "unmonitor");

    expect(unmonitor?.sentences).toContain(UNMONITORING_DOES_NOT_RETRACT);
  });
});

describe("the scope pair", () => {
  it("reads Future Scenes first with Future Scenes taken, whatever the generation and the state", () => {
    for (const generation of GENERATIONS) {
      for (const monitored of [false, true]) {
        const menu = monitorMenu(view({ kind: "studio", generation, monitored }), false);
        const scopes = menu.items.flatMap((item) => (item.item === "scope" ? [item] : []));

        expect(scopeLabels(menu.items), `${generation} ${String(monitored)}`).toEqual([
          SCOPE_FUTURE_SCENES,
          SCOPE_ALL_SCENES,
        ]);
        expect(scopes.map((scope) => scope.selected)).toEqual([true, false]);
      }
    }
  });

  it("tells the reader beside both options that the choice does not decide what is monitored", () => {
    const menu = monitorMenu(view({ kind: "studio" }), false);

    for (const item of menu.items) {
      if (item.item === "scope") {
        expect(item.sentences, item.label).toContain(SCOPE_DOES_NOT_LIMIT_WHAT_IS_MONITORED);
      }
    }
  });

  it("states the back-catalogue cost beside All Scenes and nowhere else", () => {
    const menu = monitorMenu(view({ kind: "studio" }), false);
    const carrying = menu.items.filter((item) =>
      item.sentences.includes(ALL_SCENES_MARKS_THE_BACK_CATALOGUE),
    );

    expect(carrying.map((item) => item.label)).toEqual([SCOPE_ALL_SCENES]);
  });

  it("calls All Scenes a one-way door only where a scope change leaves what is already monitored", () => {
    const carriedOn = (generation: NonNullable<WhisparrGeneration>) =>
      monitorMenu(view({ kind: "studio", generation }), false).items.some((item) =>
        item.sentences.includes(ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE),
      );

    expect(carriedOn("v3")).toBe(true);
    expect(carriedOn("v2")).toBe(false);
  });
});

describe("a performer", () => {
  it("is offered one plain monitor item and no scope option at all", () => {
    const menu = monitorMenu(view({ kind: "performer", monitored: false }), false);

    expect(scopeLabels(menu.items)).toEqual([]);
    expect(menu.items.map((item) => item.item)).toEqual(["monitor"]);
  });

  it("carries the All-Scenes consequence on that one item, and why there is no choice", () => {
    const menu = monitorMenu(view({ kind: "performer", monitored: false }), false);
    const monitor = menu.items.find((item) => item.item === "monitor");

    expect(monitor?.sentences).toContain(ALL_SCENES_MARKS_THE_BACK_CATALOGUE);
    expect(monitor?.sentences).toContain(PERFORMER_HAS_NO_FUTURE_ONLY_SCOPE);
  });

  it("leaves the control itself unavailable, with the menu empty, where the generation cannot monitor one", () => {
    const menu = monitorMenu(
      view({
        kind: "performer",
        generation: "v2",
        capabilities: withoutCapability("monitorPerformer"),
      }),
      false,
    );

    expect(menu.available).toBe(false);
    expect(menu.reason).toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
    expect(menu.items).toEqual([]);
  });
});

describe("a capability the connected generation does not hold", () => {
  it("leaves add all missing present, disabled, and saying why", () => {
    const menu = monitorMenu(
      view({
        kind: "studio",
        monitored: true,
        capabilities: withoutCapability("registerMissingScenes"),
      }),
      false,
    );
    const addAllMissing = menu.items.find(
      (item) => item.item === "secondary" && item.action === "addAllMissing",
    );

    expect(addAllMissing).toBeDefined();
    expect(addAllMissing?.enabled).toBe(false);
    expect(addAllMissing?.reason).toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
  });

  it("applies the same rule to every secondary action, one at a time", () => {
    for (const action of SECONDARY_ACTIONS) {
      const menu = monitorMenu(
        view({
          kind: "studio",
          monitored: true,
          capabilities: withoutCapability(capabilityBehindAction(action)),
        }),
        false,
      );
      const item = menu.items.find(
        (entry) => entry.item === "secondary" && entry.action === action,
      );

      expect(item?.enabled, action).toBe(false);
      expect(item?.reason, action).toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
    }
  });

  it("gates each action on the capability the capability table names", () => {
    for (const action of SECONDARY_ACTIONS) {
      expect(CAPABILITY_ORDER, action).toContain(capabilityBehindAction(action));
    }
    expect(SECONDARY_ACTIONS.map(capabilityBehindAction)).toEqual([
      "registerMissingScenes",
      "reflectOwnedFiles",
      "searchMonitored",
    ]);
  });
});

describe("one refusal, one sentence", () => {
  it("gives every kind the wire enum carries exactly one sentence, or none for a refusal that is not one", () => {
    for (const kind of MONITOR_REFUSAL_KINDS) {
      const sentence = describeMonitorRefusal(kind).sentence;
      if (kind === "none") {
        expect(sentence).toBeNull();
      } else {
        expect(typeof sentence, kind).toBe("string");
        expect((sentence ?? "").length, kind).toBeGreaterThan(0);
      }
    }
  });

  it("states the kind the server chose and never a second reason beside it", () => {
    for (const kind of MONITOR_REFUSAL_KINDS) {
      const menu = monitorMenu(view({ kind: "studio", refusal: kind }), false);
      expect(menu.reason, kind).toBe(describeMonitorRefusal(kind).sentence);
    }
  });

  it("takes the server's kind over the browser's own reading of the held list", () => {
    const menu = monitorMenu(
      view({
        kind: "performer",
        refusal: "noIdentityInThisNamespace",
        capabilities: withoutCapability("monitorPerformer"),
      }),
      false,
    );

    expect(menu.reason).toBe(describeMonitorRefusal("noIdentityInThisNamespace").sentence);
    expect(menu.reason).not.toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
  });

  it("keeps the control usable for a refusal that was one attempt failing", () => {
    for (const kind of ["noQualityProfile", "noRootFolder", "instanceRefused"] as const) {
      const menu = monitorMenu(view({ kind: "studio", refusal: kind }), false);
      expect(menu.available, kind).toBe(true);
      expect(menu.items.length, kind).toBeGreaterThan(0);
    }
  });

  it("empties the menu for a refusal that means the entity cannot be monitored here", () => {
    for (const kind of [
      "notConfigured",
      "noIdentityInThisNamespace",
      "capabilityAbsentOnThisGeneration",
    ] as const satisfies readonly MonitorRefusalKind[]) {
      const menu = monitorMenu(view({ kind: "studio", refusal: kind }), false);
      expect(menu.available, kind).toBe(false);
      expect(menu.items, kind).toEqual([]);
    }
  });
});

describe("an action already on its way", () => {
  it("disables every item and says what is being waited for", () => {
    for (const kind of ENTITY_KINDS) {
      for (const monitored of [false, true]) {
        const menu = monitorMenu(view({ kind, monitored }), true);

        expect(menu.items.length, `${kind} ${String(monitored)}`).toBeGreaterThan(0);
        for (const item of menu.items) {
          expect(item.enabled, `${kind} ${item.label}`).toBe(false);
          expect(item.reason, `${kind} ${item.label}`).toBe(WAITING_FOR_WHISPARR);
        }
      }
    }
  });

  it("keeps a permanent reason ahead of the transient one", () => {
    const menu = monitorMenu(
      view({
        kind: "studio",
        monitored: true,
        capabilities: withoutCapability("searchMonitored"),
      }),
      true,
    );
    const search = menu.items.find(
      (item) => item.item === "secondary" && item.action === "searchAllMonitored",
    );

    expect(search?.reason).toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
  });
});

describe("no count reaches this layer", () => {
  it("reads nothing off the view but the five fields the read carries", () => {
    const menu = monitorMenu(view({ kind: "studio", monitored: true }), false);
    const everySentence = menu.items.flatMap((item) => [item.label, ...item.sentences]);

    for (const text of everySentence) {
      expect(/\d/.test(text), text).toBe(false);
    }
  });
});
