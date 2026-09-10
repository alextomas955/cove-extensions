import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

import {
  RUN_WAS_NOT_STARTED,
  NO_INSTANCE_CONNECTED,
  WHISPARR_KEEPS_NO_SCENE_RECORDS,
} from "../common/ui/copy";
import {
  invertSelection,
  SELECTION_REFUSAL_KINDS,
  selectionActionsFor,
  selectionOutcomeIn,
  selectionOutcomeLine,
  selectionRefusalLine,
  type SelectionOutcome,
} from "./missingSelectionLogic";

const HERE = path.dirname(fileURLToPath(import.meta.url));

const PAGE = ["scene-a", "scene-b", "scene-c"] as const;

const actionNamed = (key: string, selected: ReadonlySet<string>) => {
  const found = selectionActionsFor(PAGE, selected).find((action) => action.key === key);
  if (found === undefined) throw new Error(`no action named ${key}`);
  return found;
};

describe("selectionActionsFor", () => {
  it("offers three gestures and no fourth", () => {
    expect(selectionActionsFor(PAGE, new Set()).map((action) => action.key)).toEqual([
      "selectAll",
      "selectNone",
      "invert",
    ]);
  });

  it("answers only scenes drawn from the loaded page", () => {
    for (const action of selectionActionsFor(PAGE, new Set(["scene-b"]))) {
      for (const id of action.resulting) {
        expect(PAGE, `${action.key} reached beyond the loaded page`).toContain(id);
      }
    }
  });

  it("selects everything on screen and nothing else", () => {
    expect(actionNamed("selectAll", new Set()).resulting).toEqual([...PAGE]);
  });

  it("clears the selection", () => {
    expect(actionNamed("selectNone", new Set(PAGE)).resulting).toEqual([]);
  });

  it("inverts over the loaded page", () => {
    expect(actionNamed("invert", new Set(["scene-b"])).resulting).toEqual(["scene-a", "scene-c"]);
    expect(invertSelection(PAGE, new Set(PAGE))).toEqual([]);
  });

  it("reaches for no whole-result-set identifier walk", () => {
    // Cove's own Select all matching pages the entire result set through these. Named rather than
    // grepped for a phrase: reaching one of them is what would make the walk possible at all.
    const forbidden = ["fetchAllMatchingIds", "selectAllMatching"];
    const shipped = readdirSync(HERE).filter(
      (name) => /\.tsx?$/.test(name) && !name.endsWith(".test.ts"),
    );

    expect(shipped.length, "no module of this surface was read").toBeGreaterThan(10);
    for (const name of shipped) {
      const source = readFileSync(path.join(HERE, name), "utf8");
      for (const helper of forbidden) {
        expect(source, `${name} reaches for ${helper}`).not.toContain(helper);
      }
    }
  });

  it("registers each gesture under the binding id Cove's own list page uses", () => {
    expect(
      selectionActionsFor(PAGE, new Set()).map((action) => [action.shortcutId, action.keys]),
    ).toEqual([
      ["list.select.all", "s a"],
      ["list.select.none", "s n"],
      ["list.select.invert", "s i"],
    ]);
  });
});

describe("selectionRefusalLine", () => {
  it("states the specified sentence for each kind", () => {
    expect(selectionRefusalLine("noInstanceConnected")).toBe(NO_INSTANCE_CONNECTED);
    expect(selectionRefusalLine("whisparrKeepsNoSceneRecords")).toBe(
      WHISPARR_KEEPS_NO_SCENE_RECORDS,
    );
    expect(selectionRefusalLine("notStarted")).toBe(RUN_WAS_NOT_STARTED);
  });

  it("states a sentence for every kind", () => {
    for (const kind of SELECTION_REFUSAL_KINDS) {
      expect(selectionRefusalLine(kind), `${kind} states nothing`).not.toBe("");
    }
  });
});

describe("selectionOutcomeLine", () => {
  it("states nothing until a press has been refused", () => {
    const quiet: SelectionOutcome[] = [
      { kind: "atRest" },
      { kind: "inFlight" },
      { kind: "started" },
    ];
    for (const outcome of quiet) {
      expect(selectionOutcomeLine(outcome)).toBeNull();
    }
  });

  it("states the refusal's own sentence", () => {
    expect(selectionOutcomeLine({ kind: "refused", refusal: "noInstanceConnected" })).toBe(
      NO_INSTANCE_CONNECTED,
    );
  });
});

describe("selectionOutcomeIn", () => {
  it("reads a started run off a job id", () => {
    expect(selectionOutcomeIn({ jobId: "ext:missing-bulk:1", refusal: "none" })).toEqual({
      kind: "started",
    });
  });

  it("reads each refusal the route answers as its own kind", () => {
    expect(selectionOutcomeIn({ jobId: null, refusal: "noInstanceConnected" })).toEqual({
      kind: "refused",
      refusal: "noInstanceConnected",
    });
    expect(selectionOutcomeIn({ jobId: null, refusal: "whisparrKeepsNoSceneRecords" })).toEqual({
      kind: "refused",
      refusal: "whisparrKeepsNoSceneRecords",
    });
  });

  it("reads a body it cannot understand as a run that never started", () => {
    for (const body of [null, undefined, {}, { refusal: "none", jobId: null }, "started"]) {
      expect(selectionOutcomeIn(body)).toEqual({ kind: "refused", refusal: "notStarted" });
    }
  });
});
