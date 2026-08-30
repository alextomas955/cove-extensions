// @vitest-environment jsdom
/**
 * That the name-then-reason rule lands on the element, not merely on a string.
 *
 * A DOM is needed because a pure test could assert what a helper returns and still miss the seam: a
 * reason that never reaches the rendered button is a dimmed control with nothing to hear. A button
 * carrying no `aria-label` takes its accessible name from its contents in document order, so the
 * element's text content in order IS the accessible name being asserted here.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so a render is flushed by waiting rather than by wrapping. The shared
 * primitives stand in, because their `react` import resolves only inside a consuming bundle; the
 * stand-in for `Button` reproduces the real element - a `<button>` carrying `disabled` and its
 * children - which is what the accessible name is read from.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { CAP_UNAVAILABLE_ON_THIS_GENERATION } from "./copy";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    Button: (props: { children?: ReactNode; disabled?: boolean; onClick?: () => void }) =>
      h(
        "button",
        { type: "button", disabled: props.disabled, onClick: props.onClick },
        props.children,
      ),
  };
});

const { DisabledControl } = await import("./DisabledControl");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const NAME = "Monitor";
const REASON = CAP_UNAVAILABLE_ON_THIS_GENERATION;

async function render(node: ReactNode) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(node);
  await sleep(COMMIT_MS);
  return {
    container,
    button: container.querySelector("button"),
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("a disabled control announces its own name before the reason", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button).not.toBeNull();
  expect(view.button?.textContent).toMatch(new RegExp(`^${NAME}`));
  expect(view.button?.textContent).toContain(REASON);
  view.teardown();
});

test("the reason reaches assistive technology as text, not only as a pointer attribute", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  // A title alone is pointer-only. The assertion is that a text node carries it as well.
  const carriesText = [...view.container.querySelectorAll("span")].some(
    (span) => span.textContent === REASON,
  );
  expect(carriesText).toBe(true);
  expect(view.container.querySelector("[title]")?.getAttribute("title")).toBe(REASON);
  view.teardown();
});

test("the reason is not drawn on screen beside every control", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  const carrier = [...view.container.querySelectorAll("span")].find(
    (span) => span.textContent === REASON,
  );
  expect(carrier?.style.position).toBe("absolute");
  expect(carrier?.style.width).toBe("1px");
  view.teardown();
});

test("an enabled control announces only its own name", async () => {
  const view = await render(
    createElement(DisabledControl, { name: NAME, onClick: () => undefined }),
  );

  expect(view.button?.disabled).toBe(false);
  expect(view.button?.textContent).toBe(NAME);
  expect(view.container.querySelector("[title]")).toBeNull();
  view.teardown();
});

test("a control with an empty reason still announces its own name", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      reason: "",
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button?.textContent).toMatch(new RegExp(`^${NAME}`));
  view.teardown();
});
