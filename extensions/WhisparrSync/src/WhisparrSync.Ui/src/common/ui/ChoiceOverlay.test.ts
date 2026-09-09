// @vitest-environment jsdom
/**
 * What the overlay shell draws for a set of rows, and what it draws when it is given none.
 *
 * A DOM is needed because both properties are about the shape of what renders: whether a row draws
 * anything but its own glyph and name, and which way out the reader is offered.
 *
 * React arrives as its PRODUCTION build, which has no `act`, so a render is flushed by waiting.
 */
import { createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { test, expect, afterEach } from "vitest";

import { ChoiceOverlay, type ChoiceRow } from "./ChoiceOverlay";
import { selectionMenuHeader } from "./copy";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const GLYPH: ChoiceRow["icon"] = ({ className }) =>
  createElement("svg", { className, "data-glyph": "row" });

const ROWS: ChoiceRow[] = [
  { key: "first", label: "First", icon: GLYPH },
  { key: "second", label: "Second", icon: GLYPH },
];

let root: Root | null = null;

async function draw(
  rows: readonly ChoiceRow[],
  chosen: (row: ChoiceRow | null) => void,
  count = 1,
) {
  const host = document.createElement("div");
  document.body.append(host);
  root = createRoot(host);
  root.render(
    createElement(ChoiceOverlay<ChoiceRow>, {
      count,
      reason: rows.length === 0 ? "Nothing was sent." : null,
      rows,
      cancelLabel: "Cancel",
      closeLabel: "Close",
      onChoose: chosen,
    }),
  );
  await sleep(COMMIT_MS);
}

function buttons(): HTMLButtonElement[] {
  return [...document.querySelectorAll("button")];
}

afterEach(() => {
  root?.unmount();
  root = null;
  document.body.innerHTML = "";
});

test("draws a glyph and a name per row, and nothing else inside one", async () => {
  await draw(ROWS, () => undefined);

  expect(buttons().map((button) => button.textContent)).toEqual(["First", "Second", "Cancel"]);
  expect(
    buttons().every((button) => button.querySelectorAll("svg").length === 1),
    "every row carries exactly one glyph",
  ).toBe(true);
  expect(document.querySelectorAll('[role="menu"] p')).toHaveLength(0);
});

test("heads the panel with the mark, the product's name and the count", async () => {
  await draw(ROWS, () => undefined, 12);
  const panel = document.querySelector('[role="menu"]');

  expect(panel?.getAttribute("aria-label")).toBe("Whisparr · 12 selected");
  expect(panel?.textContent).toContain(selectionMenuHeader(12));
});

test("reads the count at one", async () => {
  await draw(ROWS, () => undefined);

  expect(document.querySelector('[role="menu"]')?.getAttribute("aria-label")).toBe(
    "Whisparr · 1 selected",
  );
});

test("states the refusal when it is given no rows, and its way out reads Close", async () => {
  await draw([], () => undefined);

  expect(buttons().map((button) => button.textContent)).toEqual(["Close"]);
  expect(document.body.textContent).toContain("Nothing was sent.");
});

test("answers the chosen row", async () => {
  const answers: (ChoiceRow | null)[] = [];
  await draw(ROWS, (row) => answers.push(row));

  buttons()[1].click();

  expect(answers).toEqual([ROWS[1]]);
});

test("answers the absent value on the way out and on Escape", async () => {
  const answers: (ChoiceRow | null)[] = [];
  await draw(ROWS, (row) => answers.push(row));

  buttons()[2].click();
  document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));

  expect(answers).toEqual([null, null]);
});

test("every row the arrow keys must reach carries a menu role, the way out included", async () => {
  await draw(ROWS, () => undefined);

  expect(document.querySelectorAll('[role^="menuitem"]')).toHaveLength(3);
});
