// @vitest-environment jsdom
import { test, expect } from "vitest";
import { createElement, Fragment, type ReactNode } from "react";
import { render as renderNode } from "../lib/testRender";

import { WhisparrLogo } from "./WhisparrLogo";

async function render(node: ReactNode) {
  const container = await renderNode(node);
  return {
    container,
  };
}

test("the mark takes its colour from whatever draws it", async () => {
  const rendered = await render(createElement(WhisparrLogo, {}));

  const svg = rendered.container.querySelector("svg");
  expect(svg, "the mark drew no svg at all").not.toBeNull();
  expect(
    svg?.getAttribute("fill"),
    "the mark names a colour of its own, so it cannot carry a control's state",
  ).toBe("currentColor");
});

test("each instance masks through an id of its own", async () => {
  const rendered = await render(
    createElement(Fragment, null, createElement(WhisparrLogo, {}), createElement(WhisparrLogo, {})),
  );

  const ids = [...rendered.container.querySelectorAll("mask")].map((mask) => mask.id);

  expect(ids, "two marks did not draw two masks").toHaveLength(2);
  expect(ids[0], "two marks share one mask id, so the second overwrites the first").not.toBe(
    ids[1],
  );
  expect(new Set(ids.map((id) => id === ""))).toEqual(new Set([false]));
});

test("the caller's class reaches the element, so the control decides the size", async () => {
  const rendered = await render(createElement(WhisparrLogo, { className: "h-4 w-4" }));

  expect(rendered.container.querySelector("svg")?.getAttribute("class")).toBe("h-4 w-4");
});
