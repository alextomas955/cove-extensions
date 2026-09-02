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
 *
 * Where an icon is drawn the text content and the accessible name part company, because a hidden
 * subtree still contributes text and contributes no name. `accessibleName` below is what the
 * icon-carrying cases assert on.
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

const { DisabledControl, OptionallyDisabled } = await import("./DisabledControl");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const NAME = "Monitor";
const REASON = CAP_UNAVAILABLE_ON_THIS_GENERATION;

/** Hand-written, not composed from the component's own joiner. */
const NAME_THEN_REASON = "Monitor, Currently available on Whisparr v3 (Eros)";

/** A mark with nothing to say, as the product's own is once its title element is dropped. */
const MARK = createElement("svg", null);

/** Text a mark would contribute if it were not hidden, so the hiding is asserted rather than assumed. */
const MARK_TEXT = "Whisparr logo";
const TALKATIVE_MARK = createElement("svg", null, MARK_TEXT);

/**
 * The name assistive technology composes from an element: its text in document order, minus every
 * subtree removed from the accessibility tree. Text content alone counts a hidden mark's text.
 */
function accessibleName(element: Element): string {
  return [...element.childNodes]
    .map((node) => {
      if (node.nodeType === Node.TEXT_NODE) {
        return node.textContent ?? "";
      }
      if (!(node instanceof Element)) {
        return "";
      }
      return node.getAttribute("aria-hidden") === "true" ? "" : accessibleName(node);
    })
    .join("");
}

/** What a sighted reader is shown: the element's text in order, minus the off-screen carriers. */
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

test("a control with no icon renders exactly as it did: name on screen, reason alone on hover", async () => {
  const view = await render(
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
  expect(view.container.querySelector("svg")).toBeNull();
  view.teardown();
});

test("an icon-only control announces its own name and then its reason", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      icon: MARK,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button?.textContent).toBe(NAME_THEN_REASON);
  view.teardown();
});

test("an icon-only control carries that same string on hover, not the reason alone", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      icon: MARK,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.container.querySelector("[title]")?.getAttribute("title")).toBe(NAME_THEN_REASON);
  view.teardown();
});

test("the icon contributes nothing to the accessible name", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      icon: TALKATIVE_MARK,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button).not.toBeNull();
  expect(view.button?.textContent).toContain(MARK_TEXT);
  expect(accessibleName(view.button as Element)).toBe(NAME_THEN_REASON);
  view.teardown();
});

test("an icon-only control with nothing wrong announces only its own name", async () => {
  const view = await render(
    createElement(DisabledControl, { name: NAME, icon: MARK, onClick: () => undefined }),
  );

  expect(view.button?.disabled).toBe(false);
  expect(view.button?.textContent).toBe(NAME);
  expect(view.container.querySelector("[title]")?.getAttribute("title")).toBe(NAME);
  view.teardown();
});

test("an icon-only control draws its icon rather than its name", async () => {
  const view = await render(
    createElement(DisabledControl, {
      name: NAME,
      icon: MARK,
      reason: REASON,
      disabled: true,
      onClick: () => undefined,
    }),
  );

  expect(view.button?.querySelector("svg")).not.toBeNull();
  expect(visibleText(view.button as Element)).toBe("");
  view.teardown();
});

test("an optionally disabled icon control is enabled without a reason and disabled with one", async () => {
  const available = await render(
    createElement(OptionallyDisabled, {
      name: NAME,
      icon: MARK,
      reason: null,
      onClick: () => undefined,
    }),
  );

  expect(available.button?.disabled).toBe(false);
  expect(available.button?.querySelector("svg")).not.toBeNull();
  expect(available.button?.textContent).toBe(NAME);
  available.teardown();

  const unavailable = await render(
    createElement(OptionallyDisabled, {
      name: NAME,
      icon: MARK,
      reason: REASON,
      onClick: () => undefined,
    }),
  );

  expect(unavailable.button?.disabled).toBe(true);
  expect(unavailable.button?.querySelector("svg")).not.toBeNull();
  expect(unavailable.button?.textContent).toBe(NAME_THEN_REASON);
  unavailable.teardown();
});
