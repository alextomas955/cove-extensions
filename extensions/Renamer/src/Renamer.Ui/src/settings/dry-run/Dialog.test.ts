// @vitest-environment jsdom
// The Dry Run shell's own cancel paths. The overlay hook's keys are covered by its own suite; what is
// this component's is the scrim, and holding the dialog open while an operation is in flight.
import { test, expect, vi } from "vitest";
import { act, createElement } from "react";
import { createRoot } from "react-dom/client";

import { Dialog } from "./Dialog";

// `act` refuses to run without it, and React reads it off the global rather than from an import.
declare global {
  var IS_REACT_ACT_ENVIRONMENT: boolean;
}
globalThis.IS_REACT_ACT_ENVIRONMENT = true;

function mount(pending: boolean) {
  const onCancel = vi.fn();
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  act(() => {
    root.render(
      createElement(Dialog, {
        titleId: "t",
        pending,
        onCancel,
        children: [
          createElement("h2", { id: "t", key: "title" }, "Dry run"),
          createElement("button", { type: "button", key: "close" }, "Close"),
        ],
      }),
    );
  });

  return {
    onCancel,
    // The backdrop is the one element the dialog hides from assistive technology.
    scrim: () => {
      const found = container.querySelector<HTMLElement>('[aria-hidden="true"]');
      if (!found) throw new Error("no scrim");
      return found;
    },
    unmount: () => {
      act(() => {
        root.unmount();
      });
      container.remove();
    },
  };
}

function pressEscape() {
  const event = new KeyboardEvent("keydown", { key: "Escape", bubbles: true, cancelable: true });
  (document.activeElement ?? document.body).dispatchEvent(event);
}

test("a click on the scrim cancels", () => {
  const view = mount(false);

  view.scrim().click();
  expect(view.onCancel).toHaveBeenCalledTimes(1);

  view.unmount();
});

test("while an operation is pending neither Escape nor the scrim cancels", () => {
  const view = mount(true);

  pressEscape();
  view.scrim().click();
  expect(view.onCancel, "the dialog closed over an operation in flight").not.toHaveBeenCalled();

  view.unmount();
});
