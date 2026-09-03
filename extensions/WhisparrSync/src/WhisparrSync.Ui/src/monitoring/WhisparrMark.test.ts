// @vitest-environment jsdom
/**
 * What the mark draws for each generation, and what it draws before one is known.
 *
 * A DOM is needed because both properties are about the rendered tree: nothing at all before the read
 * answers is what keeps the wrong product's colour off the screen, and a mark contributing text would
 * put a second name on a control whose accessible name is the only name it has.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping.
 */
import { test, expect } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { WhisparrMark } from "./WhisparrMark";

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
    svg: container.querySelector("svg"),
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("no generation yet draws nothing at all, so no wrong-generation colour is shown", async () => {
  const rendered = await render(createElement(WhisparrMark, { generation: undefined }));

  expect(rendered.container.innerHTML).toBe("");
  rendered.teardown();
});

test("a connection to nothing draws nothing either", async () => {
  const rendered = await render(createElement(WhisparrMark, { generation: null }));

  expect(rendered.container.innerHTML).toBe("");
  rendered.teardown();
});

test("each generation draws its own product's mark", async () => {
  // The two discs differ by their fill, which is the whole reason the mark tracks the generation:
  // the newer product's disc is purple over near-black and the older one's is hot pink.
  const cases: { generation: "v3" | "v2"; disc: string }[] = [
    { generation: "v3", disc: "#241c1f" },
    { generation: "v2", disc: "#ff69b4" },
  ];

  for (const { generation, disc } of cases) {
    const rendered = await render(createElement(WhisparrMark, { generation }));

    expect(rendered.svg, generation).not.toBeNull();
    expect(rendered.svg?.getAttribute("viewBox")).toBe("0 0 1200 1200");
    expect(rendered.container.querySelector("ellipse")?.getAttribute("fill")).toBe(disc);
    // The letterform, which both products draw from the same coordinates through the same matrix.
    expect(
      rendered.container.querySelector("path[transform]")?.getAttribute("transform"),
    ).toContain("matrix(");
    rendered.teardown();
  }
});

test("neither mark contributes any text, so the control keeps the only name it has", async () => {
  for (const generation of ["v3", "v2"] as const) {
    const rendered = await render(createElement(WhisparrMark, { generation }));

    expect(rendered.container.textContent, generation).toBe("");
    expect(rendered.svg?.getAttribute("aria-hidden")).toBe("true");
    expect(rendered.svg?.getAttribute("focusable")).toBe("false");
    rendered.teardown();
  }
});

test("the caller's class reaches the element, so the button decides the size", async () => {
  const rendered = await render(
    createElement(WhisparrMark, { generation: "v3", className: "h-5 w-5" }),
  );

  expect(rendered.svg?.getAttribute("class")).toBe("h-5 w-5");
  rendered.teardown();
});
