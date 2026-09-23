// @vitest-environment jsdom
// The bar's two verbs are the same verbs the entity menu and the batch overlay offer, so they draw
// the shared table's marks. Before the table the bar named its own icons and drew the monitor verb
// as a bookmark while every other surface drew a radar.
import { expect, test, vi } from "vitest";
import { createElement } from "react";

import { render } from "../common/lib/testRender";
import { VERB_GLYPH } from "../common/ui/verbGlyphs";

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

  expect(markIn(verbNamed(bar, "Monitor"))).toBe(await tableMark("monitor"));
});

test("the bar draws the unmonitor verb's own mark", async () => {
  const bar = await barWithOneTicked();

  expect(markIn(verbNamed(bar, "Unmonitor"))).toBe(await tableMark("unmonitor"));
});

test("the bar's two verbs are not the same mark", async () => {
  const bar = await barWithOneTicked();

  expect(markIn(verbNamed(bar, "Monitor"))).not.toBe(markIn(verbNamed(bar, "Unmonitor")));
});
