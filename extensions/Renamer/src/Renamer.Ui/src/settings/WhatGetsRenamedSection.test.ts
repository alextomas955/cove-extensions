// @vitest-environment jsdom
/**
 * That the Required fields explanation is grouped with the heading it explains, above the control,
 * and that the control still carries a name of its own. Both claims are about rendered position and
 * rendered attributes, which no text assertion can see.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle. `Field` stands in with its real order — label, children, helper — so wrapping this block
 * in one goes red here instead of passing against a stub that dropped the helper.
 *
 * React arrives as its production build, which has no `act`, so the render is flushed by waiting.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { WhatGetsRenamedSection } from "./WhatGetsRenamedSection";
import { someOptions } from "./testOptions";

const HEADING = "Required fields";
const HELPER = "An item missing any of these is skipped.";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");

  return {
    SectionCard: function SectionCard(props: Record<string, unknown>) {
      return h("div", { "data-stub": "SectionCard" }, props.children as ReactNode);
    },
    Field: function Field(props: Record<string, unknown>) {
      return h(
        "label",
        { "data-stub": "Field" },
        h("span", { "data-stub": "Field-label" }, props.label as string),
        props.children as ReactNode,
        h("span", { "data-stub": "Field-helper" }, props.helper as string),
      );
    },
    Toggle: function Toggle(props: Record<string, unknown>) {
      return h(
        "div",
        { "data-stub": "Toggle" },
        h("span", null, props.label as string),
        h("p", null, props.helper as string),
      );
    },
    TagListInput: function TagListInput(props: Record<string, unknown>) {
      return h("input", {
        "data-stub": "TagListInput",
        "aria-label": props.ariaLabel as string | undefined,
      });
    },
  };
});

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

async function renderSection() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(WhatGetsRenamedSection, { options: someOptions(), set: () => undefined }),
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

/**
 * The last element whose whole text is `text`. Every ancestor of a text node also contains it, so a
 * `contains` match would return a wrapper and compare it against its own descendant.
 */
function innermost(container: HTMLElement, text: string): Element | undefined {
  return [...container.querySelectorAll("*")].filter((e) => e.textContent.trim() === text).at(-1);
}

test("the explanation is grouped under its heading, above the control", async () => {
  const view = await renderSection();

  const helper = innermost(view.container, HELPER);
  const control = view.container.querySelector('[data-stub="TagListInput"]');
  expect(helper).toBeDefined();
  expect(control).not.toBeNull();

  const helperPrecedesControl = Boolean(
    helper!.compareDocumentPosition(control!) & Node.DOCUMENT_POSITION_FOLLOWING,
  );
  expect(helperPrecedesControl).toBe(true);

  view.unmount();
});

test("the control is named by its heading and nothing else", async () => {
  const view = await renderSection();

  const heading = innermost(view.container, HEADING);
  const control = view.container.querySelector('[data-stub="TagListInput"]');
  expect(heading).toBeDefined();
  expect(control).not.toBeNull();
  expect(control!.getAttribute("aria-label")).toBe(heading!.textContent);

  view.unmount();
});
