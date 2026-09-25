// @vitest-environment jsdom
// A button with no aria-label takes its accessible name from its contents in document order, so
// the element's text content in order is the accessible name asserted here.
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";

import { render } from "../lib/testRender";
import { CAP_UNAVAILABLE_ON_THIS_GENERATION } from "./copy";

// The shared primitives stand in because their `react` import resolves only inside a consuming
// bundle.
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

const { DisabledControl, OptionallyDisabled } = await import("./DisabledControl");

const NAME = "Monitor";
const REASON = CAP_UNAVAILABLE_ON_THIS_GENERATION;

function visibleText(element: Element): string {
  return [...element.childNodes]
    .map((node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        return node.textContent ?? "";
      }
      if (!(node instanceof Element)) {
        return "";
      }
      const offScreen = node instanceof HTMLElement && node.style.position === "absolute";
      return offScreen ? "" : visibleText(node);
    })
    .join("");
}

async function draw(node: ReactNode) {
  const container = await render(node);
  return {
    container,
    button: container.querySelector("button"),
  };
}

test("a disabled control announces its own name before the reason", async () => {
  const view = await draw(
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
});

test("the reason reaches assistive technology as text, not only as a pointer attribute", async () => {
  const view = await draw(
    createElement(DisabledControl, {
      name: NAME,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  const carriesText = [...view.container.querySelectorAll("span")].some(
    (span) => span.textContent === REASON,
  );
  expect(carriesText).toBe(true);
  expect(view.container.querySelector("[title]")?.getAttribute("title")).toBe(REASON);
});

test("the reason is not drawn on screen beside every control", async () => {
  const view = await draw(
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
});

test("an enabled control announces only its own name", async () => {
  const view = await draw(createElement(DisabledControl, { name: NAME, onClick: () => undefined }));

  expect(view.button?.disabled).toBe(false);
  expect(view.button?.textContent).toBe(NAME);
  expect(view.container.querySelector("[title]")).toBeNull();
});

test("a control with an empty reason still announces its own name", async () => {
  const view = await draw(
    createElement(DisabledControl, {
      name: NAME,
      reason: "",
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button?.textContent).toMatch(new RegExp(`^${NAME}`));
});

test("the name is what is drawn on screen and the reason alone is what hovers", async () => {
  const view = await draw(
    createElement(DisabledControl, {
      name: NAME,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button?.textContent).toBe(`${NAME}${REASON}`);
  expect(visibleText(view.button as Element)).toBe(NAME);
  expect(view.container.querySelector("[title]")?.getAttribute("title")).toBe(REASON);
});

test("an optionally disabled control is enabled without a reason and disabled with one", async () => {
  const available = await draw(
    createElement(OptionallyDisabled, { name: NAME, reason: null, onClick: () => undefined }),
  );

  expect(available.button?.disabled).toBe(false);
  expect(available.button?.textContent).toBe(NAME);
  const unavailable = await draw(
    createElement(OptionallyDisabled, { name: NAME, reason: REASON, onClick: () => undefined }),
  );

  expect(unavailable.button?.disabled).toBe(true);
  expect(unavailable.button?.textContent).toBe(`${NAME}${REASON}`);
});
