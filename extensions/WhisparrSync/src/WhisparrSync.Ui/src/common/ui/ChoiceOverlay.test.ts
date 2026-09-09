// @vitest-environment jsdom
/**
 * What the overlay shell draws for a set of rows, and what it draws when it is given none.
 *
 * A DOM is needed because both properties are about the shape of what renders: whether the footer is
 * there at all, and which way out the reader is offered.
 *
 * React arrives as its PRODUCTION build, which has no `act`, so a render is flushed by waiting.
 */
import { createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { test, expect, afterEach } from "vitest";

import { ChoiceOverlay, type ChoiceRow } from "./ChoiceOverlay";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const ROWS: ChoiceRow[] = [
  { key: "first", label: "First", sentences: ["What the first one does."] },
  { key: "second", label: "Second", sentences: ["What the second one does."] },
];

let root: Root | null = null;

async function draw(rows: readonly ChoiceRow[], chosen: (row: ChoiceRow | null) => void) {
  const host = document.createElement("div");
  document.body.append(host);
  root = createRoot(host);
  root.render(
    createElement(ChoiceOverlay<ChoiceRow>, {
      label: "Choose one.",
      reason: rows.length === 0 ? "Nothing was sent." : null,
      rows,
      footer: "It reports in the job list.",
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

test("draws a row per choice with its sentence outside the button, and the footer beneath", async () => {
  await draw(ROWS, () => undefined);

  expect(buttons().map((button) => button.textContent)).toEqual(["First", "Second", "Cancel"]);
  expect(document.body.textContent).toContain("What the first one does.");
  expect(document.body.textContent).toContain("It reports in the job list.");
});

test("draws no footer when it is given no rows, and its way out reads Close", async () => {
  await draw([], () => undefined);

  expect(buttons().map((button) => button.textContent)).toEqual(["Close"]);
  expect(document.body.textContent).not.toContain("It reports in the job list.");
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

test("names the panel a dialog the reader must answer", async () => {
  await draw(ROWS, () => undefined);
  const panel = document.querySelector("[role='dialog']");

  expect(panel?.getAttribute("aria-modal")).toBe("true");
  expect(panel?.getAttribute("aria-label")).toBe("Choose one.");
});
