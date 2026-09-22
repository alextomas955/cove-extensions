// @vitest-environment jsdom
/**
 * That the Required fields explanation is grouped with the heading it explains, above the control,
 * and that the control still carries a name of its own.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle.
 *
 * A render commits on React's own schedule, so each step waits for the state its assertion is about rather than for a span.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

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

async function renderSection() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(WhatGetsRenamedSection, { options: someOptions(), set: () => undefined }),
  );
  await waitFor(
    "the section to render",
    () => container.querySelector('[data-stub="TagListInput"]') !== null,
  );

  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

/** The last element whose whole text is `text`. */
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
