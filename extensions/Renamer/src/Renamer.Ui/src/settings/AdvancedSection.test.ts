// @vitest-environment jsdom
// Each Advanced control is named once, its explanation sits above it, and every helper sentence
// reads as the engine behaves.
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { AdvancedSection } from "./AdvancedSection";
import { someOptions } from "./testOptions";

async function renderAdvanced() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(AdvancedSection, {
      // A non-empty illegal replacement is what puts the replace helper on screen at all.
      options: { ...someOptions(), illegalReplacement: "_" },
      set: () => undefined,
    }),
  );
  const collapsed = () => [
    ...container.querySelectorAll<HTMLElement>('button[aria-expanded="false"]'),
  ];
  await waitFor("the advanced panels to render", () => collapsed().length > 0);
  for (const header of collapsed()) header.click();
  await waitFor("every panel to open", () => collapsed().length === 0);

  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

// How many text nodes read exactly `text`.
function textNodes(container: HTMLElement, text: string): number {
  const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT);
  let found = 0;
  let node = walker.nextNode();
  while (node) {
    if ((node.nodeValue ?? "").trim() === text) found += 1;
    node = walker.nextNode();
  }
  return found;
}

function input(container: HTMLElement, placeholder: string): HTMLInputElement {
  const el = container.querySelector<HTMLInputElement>(`input[placeholder="${placeholder}"]`);
  if (!el) throw new Error(`no input with placeholder ${placeholder}`);
  return el;
}

const DROP_ORDER_PLACEHOLDER = "Add a token — type to search";
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

// Every sentence and its count, so a run names each one that disagrees.
function counts(container: HTMLElement, sentences: readonly string[]): Record<string, number> {
  return Object.fromEntries(sentences.map((s) => [s, textNodes(container, s)]));
}

function expected(sentences: readonly string[], n: number): Record<string, number> {
  return Object.fromEntries(sentences.map((s) => [s, n]));
}

test("each shipped helper sentence is on screen exactly once", async () => {
  const view = await renderAdvanced();

  expect(counts(view.container, SHIPS)).toEqual(expected(SHIPS, 1));

  view.unmount();
});

test("the drop-order explanation precedes the control it explains", async () => {
  const view = await renderAdvanced();

  const explanation = [...view.container.querySelectorAll("*")]
    .filter((e) => e.textContent.trim() === DROP_ORDER_EXPLANATION)
    .at(-1);
  expect(explanation, DROP_ORDER_EXPLANATION).toBeDefined();

  const control = input(view.container, DROP_ORDER_PLACEHOLDER);
  const precedes = Boolean(
    explanation!.compareDocumentPosition(control) & Node.DOCUMENT_POSITION_FOLLOWING,
  );
  expect(precedes).toBe(true);

  view.unmount();
});

test("the chip controls are named by their own heading, not by a placeholder", async () => {
  const view = await renderAdvanced();

  expect(input(view.container, DROP_ORDER_PLACEHOLDER).getAttribute("aria-label")).toBe(
    "Drop order",
  );
  expect(input(view.container, ARTICLES_PLACEHOLDER).getAttribute("aria-label")).toBe("Articles");

  view.unmount();
});

test("each exclude control is named once", async () => {
  const view = await renderAdvanced();

  for (const label of ["Exclude by tag", "Exclude by studio", "Exclude by source path"]) {
    expect(textNodes(view.container, label), label).toBe(1);
  }

  view.unmount();
});
