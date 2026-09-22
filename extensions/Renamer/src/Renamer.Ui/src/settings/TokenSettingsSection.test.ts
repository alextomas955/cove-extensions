// @vitest-environment jsdom
/**
 * The format examples this section shows beside each option, and the one sentence ranking genders.
 * The examples are the only thing telling a user what a format string will produce.
 *
 * Every expectation below was produced by running the engine's own formatter over the reference value
 * (`TimeSpan.ToString(format, InvariantCulture)`, as `MetadataProjector.FormatDuration` calls it) and
 * transcribed by hand. None is derived from the module under test, which would only prove it agrees
 * with itself. The whole list is pinned rather than each entry, so an option added with no example
 * checked here fails too.
 *
 * The gender-order sentence is read off the rendered screen, because it says what
 * `MultiValue.GenderRank` does with a gender the user left out. The shared primitives stand in,
 * because their `react` import resolves only inside a consuming bundle, and the entity adapter
 * stands in whole because `@cove/runtime/*` resolves only inside a running Cove. React arrives as
 * its production build, which has no `act`, so the render is flushed by waiting.
 */
import { test, expect, vi } from "vitest";
import assert from "node:assert/strict";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { someOptions } from "./testOptions";

vi.mock("./EntitySelectField", () => ({ EntitySelectField: () => null }));

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  const text = (v: unknown) => (typeof v === "string" ? v : null);
  const box = (stub: string) => (p: { children?: ReactNode }) =>
    h("div", { "data-stub": stub }, p.children);

  return {
    SectionCard: box("SectionCard"),
    GroupCard: box("GroupCard"),
    Badge: box("Badge"),
    Chip: box("Chip"),
    Field: (p: { label?: string; helper?: string; children?: ReactNode }) =>
      h(
        "label",
        { "data-stub": "Field" },
        h("span", null, text(p.label)),
        p.children,
        h("span", null, text(p.helper)),
      ),
    FieldGroup: (p: { label?: string; helper?: string; children?: ReactNode }) =>
      h(
        "div",
        { "data-stub": "FieldGroup", role: "group" },
        h("span", null, text(p.label)),
        p.children,
        h("span", null, text(p.helper)),
      ),
    NumberInput: () => h("input", { type: "number" }),
    Select: () => h("select", null),
    ExampleSelect: () => h("select", null),
    SeparatorChips: () => h("div", null),
    ChipMultiSelect: () => h("div", null),
    OrderedPickToAdd: () => h("div", null),
  };
});

const { DATE_FORMAT_OPTIONS, DURATION_FORMAT_OPTIONS, TokenSettingsSection } =
  await import("./TokenSettingsSection");

const pairs = (options: readonly { value: string; example: string }[]) =>
  options.map((o) => [o.value, o.example]);

test("every duration example is what the engine's formatter renders for 1h 23m 45s", () => {
  assert.deepEqual(pairs(DURATION_FORMAT_OPTIONS), [
    [String.raw`hh\-mm\-ss`, "01-23-45"],
    [String.raw`hh\.mm\.ss`, "01.23.45"],
    // `mm` is the minutes component of 01:23:45, never its 83 total minutes.
    [String.raw`mm\-ss`, "23-45"],
  ]);
});

test("every date example is what the engine's formatter renders for 2026-03-12", () => {
  assert.deepEqual(pairs(DATE_FORMAT_OPTIONS), [
    ["yyyy-MM-dd", "2026-03-12"],
    ["yyyy", "2026"],
    ["MM-dd-yyyy", "03-12-2026"],
    ["dd.MM.yyyy", "12.03.2026"],
    ["yyyy.MM.dd", "2026.03.12"],
  ]);
});

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

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

test("the gender order says where a gender the user left out ends up", async () => {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(TokenSettingsSection, {
      // The performers group renders only for a template that uses the token.
      options: { ...someOptions(), filenameTemplate: "$performers - $title" },
      set: () => undefined,
      setMulti: () => undefined,
      insertToken: () => undefined,
    }),
  );
  await sleep(COMMIT_MS);

  expect(textNodes(container, "Most-preferred first. Anyone else sorts last.")).toBe(1);
  expect(textNodes(container, "Most-preferred first.")).toBe(0);

  root.unmount();
  container.remove();
});
