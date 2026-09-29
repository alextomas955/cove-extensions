// @vitest-environment jsdom
// Absence is asserted as no element rather than as an empty one, because an empty notice still
// reads as a constraint in force.
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
    StatusText: (props: { children?: ReactNode }) => h("span", null, props.children),
  };
});

const { RefusalNotice } = await import("./RefusalNotice");
const { DisabledControl } = await import("./DisabledControl");

const REASON = CAP_UNAVAILABLE_ON_THIS_GENERATION;

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

async function draw(node: ReactNode) {
  const container = await render(node);
  return {
    notices: container.querySelectorAll('[role="note"]'),
    buttons: container.querySelectorAll("button"),
  };
}

test.each([4, 2])("%i controls sharing one reason produce exactly one notice", async (controls) => {
  const view = await draw(screenWith(controls));

  expect(view.buttons.length).toBe(controls);
  expect(view.notices.length).toBe(1);
  expect(view.notices[0].textContent).toContain(REASON);
});

test("a screen with no affected control has no notice element", async () => {
  const view = await draw(screenWith(0));

  expect(view.buttons.length).toBe(0);
  expect(view.notices.length).toBe(0);
});
