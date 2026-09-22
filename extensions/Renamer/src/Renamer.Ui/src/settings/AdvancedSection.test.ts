// @vitest-environment jsdom
/**
 * That each Advanced control is named once, that its explanation sits with the heading it explains,
 * and that every helper sentence reads as the engine behaves.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle. Each stand-in keeps the part of its real shape an assertion here depends on: `Field`
 * renders label, children then helper in that order; `SegmentedReplace` shows its replace helper
 * only for a non-empty value; `CollapsibleSection` renders its children, standing for a panel the
 * user has opened.
 *
 * React arrives as its production build, which has no `act`, so the render is flushed by waiting.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { someOptions } from "./testOptions";

// The host selector's module specifier resolves only inside a running Cove, so the adapter stands in
// whole. The host control takes the name itself, and its `Field` is therefore not a label element.
vi.mock("./EntitySelectField", async () => {
  const { createElement: h } = await import("react");
  const { Field } = await import("@cove-extensions/ui-shared");
  return {
    EntitySelectField: (p: {
      label: string;
      helper?: string;
      labelStyle?: "micro" | "group";
      placeholder?: string;
    }) =>
      h(Field, {
        label: p.label,
        helper: p.helper,
        labelStyle: p.labelStyle,
        controlNamesItself: true,
        children: h("input", {
          "data-stub": "EntitySelector",
          "aria-label": p.label,
          placeholder: p.placeholder,
        }),
      }),
  };
});

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  const text = (v: unknown) => (typeof v === "string" ? v : null);

  return {
    INPUT_CLASS: "stub-input",
    SectionCard: (p: { children?: ReactNode }) =>
      h("div", { "data-stub": "SectionCard" }, p.children),
    CollapsibleSection: (p: { title?: string; summary?: string; children?: ReactNode }) =>
      h(
        "div",
        { "data-stub": "CollapsibleSection" },
        h("span", { "data-stub": "CollapsibleSection-title" }, text(p.title)),
        h("span", null, text(p.summary)),
        p.children,
      ),
    GroupCard: (p: { title?: string; description?: string; children?: ReactNode }) =>
      h(
        "div",
        { "data-stub": "GroupCard" },
        h("h3", { "data-stub": "GroupCard-title" }, text(p.title)),
        h("p", null, text(p.description)),
        p.children,
      ),
    Field: (p: {
      label?: string;
      helper?: string;
      labelStyle?: string;
      controlNamesItself?: boolean;
      children?: ReactNode;
    }) =>
      h(
        p.controlNamesItself ? "div" : "label",
        {
          "data-stub": "Field",
          "data-label-style": p.labelStyle ?? "micro",
          role: p.controlNamesItself ? "group" : undefined,
        },
        h("span", { "data-stub": "Field-label" }, text(p.label)),
        p.children,
        h("span", { "data-stub": "Field-helper" }, text(p.helper)),
      ),
    Toggle: (p: { label?: string; helper?: string }) =>
      h(
        "div",
        { "data-stub": "Toggle" },
        h("span", null, text(p.label)),
        h("p", null, text(p.helper)),
      ),
    TagListInput: (p: { ariaLabel?: string; placeholder?: string }) =>
      h("input", {
        "data-stub": "TagListInput",
        "aria-label": p.ariaLabel,
        placeholder: p.placeholder,
      }),
    TextInput: (p: { placeholder?: string }) =>
      h("input", { "data-stub": "TextInput", placeholder: p.placeholder }),
    NumberInput: () => h("input", { "data-stub": "NumberInput", type: "number" }),
    Select: (p: { options?: readonly { label: string }[] }) =>
      h(
        "select",
        { "data-stub": "Select" },
        (p.options ?? []).map((o, i) => h("option", { key: i }, o.label)),
      ),
    ExampleSelect: (p: { options?: readonly { value: string; example: string }[] }) =>
      h(
        "select",
        { "data-stub": "ExampleSelect" },
        (p.options ?? []).map((o, i) => h("option", { key: i }, `${o.value} → ${o.example}`)),
      ),
    // The real control reveals its replacement input, and with it the replace helper, only once the
    // value is non-empty.
    SegmentedReplace: (p: {
      value?: string;
      stripLabel?: string;
      replaceLabel?: string;
      stripHelper?: string;
      replaceHelper?: string;
      inputPlaceholder?: string;
    }) =>
      h(
        "div",
        { "data-stub": "SegmentedReplace" },
        h("span", null, text(p.stripLabel)),
        h("span", null, text(p.replaceLabel)),
        p.value
          ? [
              h("input", { key: "i", placeholder: p.inputPlaceholder }),
              h("span", { key: "h" }, text(p.replaceHelper)),
            ]
          : h("span", null, text(p.stripHelper)),
      ),
    ObjectArrayEditor: (p: {
      rows?: unknown[];
      renderRow?: (row: unknown, i: number, update: () => void) => ReactNode;
      addLabel?: string;
    }) =>
      h(
        "div",
        { "data-stub": "ObjectArrayEditor" },
        (p.rows ?? []).map((row, i) =>
          h(
            "div",
            { key: i },
            p.renderRow?.(row, i, () => undefined),
          ),
        ),
        h("button", { type: "button" }, text(p.addLabel)),
      ),
    RegexValidity: () => null,
  };
});

const { AdvancedSection } = await import("./AdvancedSection");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

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
  await sleep(COMMIT_MS);

  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

/** How many text nodes read exactly `text`. */
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

const SUPERSEDED = [
  "Tags",
  "Studios",
  "Articles",
  "An exact match or a regex.",
  "Each illegal character becomes this.",
  "Deleted before illegal-character handling, e.g. ,#",
  "{n} = a counter added only when a name already exists, e.g. name (1).mp4.",
  "Case-insensitive, and only a whole word at the start.",
  "So one studio renders to one stable folder name.",
  "Only when the whole name appears in the title.",
  "lower case",
];

/** Every sentence and its count, so a run names each one that disagrees. */
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

test("no superseded label or sentence survives", async () => {
  const view = await renderAdvanced();

  expect(counts(view.container, SUPERSEDED)).toEqual(expected(SUPERSEDED, 0));

  view.unmount();
});

test("neither exclude selector carries a helper below its control", async () => {
  const view = await renderAdvanced();

  expect(textNodes(view.container, "A child studio counts too.")).toBe(0);

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

test("each exclude names its control once, on a block its control does not depend on", async () => {
  const view = await renderAdvanced();

  const excludeLabels = [...view.container.querySelectorAll('[data-stub="Field-label"]')]
    .map((e) => e.textContent.trim())
    .filter((t) => t.startsWith("Exclude by"));
  expect(excludeLabels).toEqual(["Exclude by tag", "Exclude by studio"]);

  for (const label of excludeLabels) {
    const field = [...view.container.querySelectorAll('[data-stub="Field"]')].find(
      (f) => f.querySelector('[data-stub="Field-label"]')?.textContent.trim() === label,
    );
    expect(field?.getAttribute("data-label-style"), label).toBe("group");
    expect(field?.tagName, label).not.toBe("LABEL");
    expect(
      field?.querySelector('[data-stub="EntitySelector"]')?.getAttribute("aria-label"),
      label,
    ).toBe(label);
  }

  expect(textNodes(view.container, "Exclude by source path")).toBe(1);

  view.unmount();
});
