// @vitest-environment jsdom
// The bar's two verbs are the same verbs the entity menu and the batch overlay offer, so they draw
// the shared table's marks. Before the table the bar named its own icons and drew the monitor verb
// as a bookmark while every other surface drew a radar.
import { expect, test, vi } from "vitest";
import { act, createElement, useState } from "react";

import { press, render } from "../common/lib/testRender";
import { SELECTION_MONITOR, SELECTION_UNMONITOR } from "../common/ui/copy";
import { VERB_GLYPH } from "../common/ui/verbGlyphs";
import type { SelectionOutcome } from "./missingSelectionLogic";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return { Spinner: () => h("span", null, "working") };
});

vi.mock("./hostComponents", () => ({ useKeySequence: () => undefined }));

const { MissingSelectionBar } = await import("./MissingSelectionBar");

/** The `lucide-<name>` class the drawn svg carries. */
function markIn(element: Element | null | undefined): string {
  const classes = element?.querySelector("svg")?.getAttribute("class") ?? "";
  return classes.split(" ").find((one) => one.startsWith("lucide-")) ?? "no mark";
}

async function barWithOneTicked(): Promise<HTMLElement> {
  return render(
    createElement(MissingSelectionBar, {
      loadedPageIds: ["s-1"],
      selected: new Set(["s-1"]),
      outcome: { kind: "atRest" },
      runUnderWay: false,
      onSelect: () => undefined,
      onMonitorSelection: () => undefined,
      onUnmonitorSelection: () => undefined,
    }),
  );
}

function verbNamed(bar: HTMLElement, label: string): Element | undefined {
  return [...bar.querySelectorAll("button")].find((one) => one.textContent.trim() === label);
}

// Rendered through the table rather than compared to a name, so the assertion follows the table
// when a verb's mark is changed there and fails when a surface stops reading it.
async function tableMark(verb: "monitor" | "unmonitor"): Promise<string> {
  const drawn = await render(createElement(VERB_GLYPH[verb], {}));
  return markIn(drawn);
}

test("the bar draws the monitor verb's own mark", async () => {
  const bar = await barWithOneTicked();

  expect(markIn(verbNamed(bar, SELECTION_MONITOR))).toBe(await tableMark("monitor"));
});

test("the bar draws the unmonitor verb's own mark", async () => {
  const bar = await barWithOneTicked();

  expect(markIn(verbNamed(bar, SELECTION_UNMONITOR))).toBe(await tableMark("unmonitor"));
});

test("the bar's two verbs are not the same mark", async () => {
  const bar = await barWithOneTicked();

  expect(markIn(verbNamed(bar, SELECTION_MONITOR))).not.toBe(
    markIn(verbNamed(bar, SELECTION_UNMONITOR)),
  );
});

// A press disables the verb it was made on, the browser blurs a control it disables, and a refused
// run leaves the reader with nowhere to press again from.
test("focus returns to the verb a refused run was pressed from", async () => {
  let settle: (() => void) | undefined;

  function Harness() {
    const [outcome, setOutcome] = useState<SelectionOutcome>({ kind: "atRest" });
    settle = () => {
      setOutcome({ kind: "refused", refusal: "noInstanceConnected" });
    };
    return createElement(MissingSelectionBar, {
      loadedPageIds: ["s-1"],
      selected: new Set(["s-1"]),
      outcome,
      runUnderWay: false,
      onSelect: () => undefined,
      onMonitorSelection: () => {
        setOutcome({ kind: "inFlight" });
      },
      onUnmonitorSelection: () => undefined,
    });
  }

  const bar = await render(createElement(Harness));
  const monitor = verbNamed(bar, SELECTION_MONITOR) as HTMLButtonElement;
  monitor.focus();

  await press(monitor);
  // Every browser blurs a control the moment it is disabled, leaving focus on the body. jsdom
  // leaves focus where it is and ignores `blur()` on a disabled element, so the state the bar has
  // to repair is reached by dropping focus off an element that is removed.
  const goes = document.createElement("input");
  document.body.append(goes);
  goes.focus();
  goes.remove();
  expect(document.activeElement, "the press was set up with focus still somewhere").toBe(
    document.body,
  );

  await act(async () => {
    settle?.();
    await Promise.resolve();
  });

  expect(document.activeElement).toBe(verbNamed(bar, SELECTION_MONITOR));
});
