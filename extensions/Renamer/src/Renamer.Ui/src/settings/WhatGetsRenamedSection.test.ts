// @vitest-environment jsdom
// The Required fields explanation sits under its heading and above the control, and the control
// is named by that heading.
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { WhatGetsRenamedSection } from "./WhatGetsRenamedSection";
import { someOptions } from "./testOptions";

const HEADING = "Required fields";
const HELPER = "An item missing any of these is skipped.";

async function renderSection() {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(WhatGetsRenamedSection, { options: someOptions(), set: () => undefined }),
  );
  await waitFor("the section to render", () => container.querySelector("input") !== null);

  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

// The last element whose whole text is `text`.
function innermost(container: HTMLElement, text: string): Element | undefined {
  return [...container.querySelectorAll("*")].filter((e) => e.textContent.trim() === text).at(-1);
}

test("the explanation is grouped under its heading, above the control", async () => {
  const view = await renderSection();

  const helper = innermost(view.container, HELPER);
  const control = view.container.querySelector("input");
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
  const control = view.container.querySelector("input");
  expect(heading).toBeDefined();
  expect(control).not.toBeNull();
  expect(control!.getAttribute("aria-label")).toBe(heading!.textContent);

  view.unmount();
});
