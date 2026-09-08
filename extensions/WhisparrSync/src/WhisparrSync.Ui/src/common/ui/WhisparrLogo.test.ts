// @vitest-environment jsdom
/**
 * The two properties that make this mark usable where the two-tone disc is not: it takes its colour
 * from whatever draws it, and two instances do not collide.
 *
 * A DOM is needed because both are about the rendered tree. A fixed mask id would have the second
 * instance overwrite the first's mask in the document, which is only observable once two are on the
 * page.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping.
 */
import { test, expect } from "vitest";
import { createElement, Fragment, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { WhisparrLogo } from "./WhisparrLogo";

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

async function render(node: ReactNode) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(node);
  await sleep(COMMIT_MS);
  return {
    container,
    teardown: () => {
      root.unmount();
      container.remove();
    },
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
  rendered.teardown();
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
  rendered.teardown();
});

test("the caller's class reaches the element, so the control decides the size", async () => {
  const rendered = await render(createElement(WhisparrLogo, { className: "h-4 w-4" }));

  expect(rendered.container.querySelector("svg")?.getAttribute("class")).toBe("h-4 w-4");
  rendered.teardown();
});
