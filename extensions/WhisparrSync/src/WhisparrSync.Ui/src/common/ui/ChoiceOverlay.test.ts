// @vitest-environment jsdom
import { createElement } from "react";
import { test, expect } from "vitest";

import { press, render } from "../lib/testRender";
import { ChoiceOverlay, type ChoiceRow } from "./ChoiceOverlay";
import { selectionMenuHeader } from "./copy";

const GLYPH: ChoiceRow["icon"] = ({ className }) =>
  createElement("svg", { className, "data-glyph": "row" });

const ROWS: ChoiceRow[] = [
  { key: "first", label: "First", icon: GLYPH },
  { key: "second", label: "Second", icon: GLYPH },
];

async function draw(
  rows: readonly ChoiceRow[],
  chosen: (row: ChoiceRow | null) => void,
  count = 1,
) {
  await render(
    createElement(ChoiceOverlay<ChoiceRow>, {
      count,
      reason: rows.length === 0 ? "Nothing was sent." : null,
      rows,
      cancelLabel: "Cancel",
      closeLabel: "Close",
      onChoose: chosen,
    }),
  );
}

function buttons(): HTMLButtonElement[] {
  return [...document.querySelectorAll("button")];
}

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

  await press(buttons()[1]);

  expect(answers).toEqual([ROWS[1]]);
});

test("answers the absent value on the way out and on Escape", async () => {
  const answers: (ChoiceRow | null)[] = [];
  await draw(ROWS, (row) => answers.push(row));

  await press(buttons()[2]);
  document.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));

  expect(answers).toEqual([null, null]);
});

test("every row the arrow keys must reach carries a menu role, the way out included", async () => {
  await draw(ROWS, () => undefined);

  expect(document.querySelectorAll('[role^="menuitem"]')).toHaveLength(3);
});
