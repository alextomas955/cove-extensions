// @vitest-environment jsdom
/**
 * That a reason shared by several controls is stated once on the screen, and that a screen with no
 * affected control has no notice element at all.
 *
 * Absence is asserted as ELEMENT NOT PRESENT rather than as element empty: an empty notice still
 * occupies the screen and still reads as a constraint in force.
 *
 * React arrives as its PRODUCTION build, which has no `act`, so a render is flushed by waiting. The
 * shared primitives stand in, because their `react` import resolves only inside a consuming bundle.
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
    StatusText: (props: { children?: ReactNode }) => h("span", null, props.children),
  };
});

const { RefusalNotice } = await import("./RefusalNotice");
const { DisabledControl } = await import("./DisabledControl");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

const REASON = CAP_UNAVAILABLE_ON_THIS_GENERATION;

/** A screen holding `count` controls that all share one reason. */
function screenWith(count: number) {
  const names = Array.from({ length: count }, (_, i) => `Control ${String(i + 1)}`);
  return createElement(
    "div",
    null,
    createElement(RefusalNotice, { reason: REASON, affectedControls: count }),
    ...names.map((name) =>
      createElement(DisabledControl, {
        key: name,
        name,
        reason: REASON,
        disabled: true,
        onClick: () => undefined,
      }),
    ),
  );
}

async function render(node: ReactNode) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(node);
  await sleep(COMMIT_MS);
  return {
    notices: container.querySelectorAll('[role="note"]'),
    buttons: container.querySelectorAll("button"),
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("four controls sharing one reason produce exactly one notice", async () => {
  const view = await render(screenWith(4));

  expect(view.buttons.length).toBe(4);
  expect(view.notices.length).toBe(1);
  expect(view.notices[0].textContent).toContain(REASON);
  view.teardown();
});

test("two controls sharing one reason still produce exactly one notice", async () => {
  const view = await render(screenWith(2));

  expect(view.buttons.length).toBe(2);
  expect(view.notices.length).toBe(1);
  view.teardown();
});

test("a screen with no affected control has no notice element", async () => {
  const view = await render(screenWith(0));

  expect(view.buttons.length).toBe(0);
  expect(view.notices.length).toBe(0);
  view.teardown();
});

test("the notice is stated once whichever order the controls render in", async () => {
  const forwards = await render(screenWith(3));
  expect(forwards.notices.length).toBe(1);
  const first = forwards.notices[0].textContent;
  forwards.teardown();

  // The same screen with the notice after its controls rather than before them.
  const reversed = await render(
    createElement(
      "div",
      null,
      createElement(DisabledControl, {
        name: "Control 1",
        reason: REASON,
        disabled: true,
        onClick: () => undefined,
      }),
      createElement(DisabledControl, {
        name: "Control 2",
        reason: REASON,
        disabled: true,
        onClick: () => undefined,
      }),
      createElement(DisabledControl, {
        name: "Control 3",
        reason: REASON,
        disabled: true,
        onClick: () => undefined,
      }),
      createElement(RefusalNotice, { reason: REASON, affectedControls: 3 }),
    ),
  );
  expect(reversed.notices.length).toBe(1);
  expect(reversed.notices[0].textContent).toBe(first);
  reversed.teardown();
});
