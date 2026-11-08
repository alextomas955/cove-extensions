// @vitest-environment jsdom
// Each Advanced control is named once, its explanation sits above it, and every line of copy
// reads as the engine behaves.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { AdvancedSection } from "./AdvancedSection";
import { someOptions } from "./testOptions";

const DROP_ORDER_PLACEHOLDER = "Type to add a token";
const ARTICLES_PLACEHOLDER = "Add article, press Enter";
const DROP_ORDER_EXPLANATION = "Fields dropped (top first) when the name is too long.";

const SHIPS = [
  "Blank drops them.",
  "Deleted from the name.",
  "{n} is added only when a name already exists.",
  "lowercase",
  "Removed once, from the start of the title.",
  "Only when the name appears as a whole word.",
];

afterEach(cleanup);

async function renderAdvanced() {
  const user = userEvent.setup();
  render(
    createElement(AdvancedSection, {
      // A non-empty illegal replacement is what puts the replace helper on screen at all.
      options: { ...someOptions(), illegalReplacement: "_" },
      set: () => undefined,
    }),
  );

  const collapsed = () => screen.queryAllByRole("button", { expanded: false });
  for (const header of collapsed()) await user.click(header);
  expect(collapsed(), "a panel stayed shut").toEqual([]);
}

// Every sentence and how many elements read exactly that, so a run names each one that disagrees.
function counts(sentences: readonly string[]): Record<string, number> {
  return Object.fromEntries(sentences.map((s) => [s, screen.queryAllByText(s).length]));
}

function expected(sentences: readonly string[], n: number): Record<string, number> {
  return Object.fromEntries(sentences.map((s) => [s, n]));
}

test("each shipped line of helper and option copy is on screen exactly once", async () => {
  await renderAdvanced();

  expect(counts(SHIPS)).toEqual(expected(SHIPS, 1));
});

test("the drop-order explanation precedes the control it explains", async () => {
  await renderAdvanced();

  const explanation = screen.getByText(DROP_ORDER_EXPLANATION);
  const control = screen.getByPlaceholderText(DROP_ORDER_PLACEHOLDER);
  const precedes = Boolean(
    explanation.compareDocumentPosition(control) & Node.DOCUMENT_POSITION_FOLLOWING,
  );

  expect(precedes).toBe(true);
});

test("the chip controls are named by their own heading, not by a placeholder", async () => {
  await renderAdvanced();

  // Queried by accessible name and matched against the control the placeholder identifies, so a name
  // moved onto a different control fails as surely as a name that changed.
  expect(screen.getByRole("combobox", { name: "Drop order" })).toBe(
    screen.getByPlaceholderText(DROP_ORDER_PLACEHOLDER),
  );
  expect(screen.getByRole("textbox", { name: "Articles" })).toBe(
    screen.getByPlaceholderText(ARTICLES_PLACEHOLDER),
  );
});

test("each exclude control is named once", async () => {
  await renderAdvanced();

  for (const label of ["Exclude by tag", "Exclude by studio", "Exclude by source path"]) {
    expect(screen.queryAllByText(label), label).toHaveLength(1);
  }
});

test("a field's helper describes its control rather than joining its name", async () => {
  await renderAdvanced();

  // A control takes its name from the whole text of the label that names it, so a helper rendered
  // inside that label is read out as part of the name. The exact-name query is what catches it:
  // the loose one matches either way.
  const control = screen.getByRole("textbox", { name: "Remove characters" });

  const describedBy = control.getAttribute("aria-describedby");
  expect(describedBy, "the helper reaches the control as nothing at all").not.toBeNull();
  expect(document.getElementById(describedBy ?? "")?.textContent).toBe("Deleted from the name.");
});
