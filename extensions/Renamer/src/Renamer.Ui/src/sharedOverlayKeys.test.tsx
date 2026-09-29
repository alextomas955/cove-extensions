// @vitest-environment jsdom
// This test of a shared hook lives in the consuming package because `react` resolves only here.
// The overlay hook's dialog mode on a bare panel, so nothing but the hook decides what a key does.
// jsdom runs no default action for Tab, so the only focus move a test sees is the one the trap makes.
import { test, expect, vi } from "vitest";
import { act, useRef } from "react";
import { createRoot } from "react-dom/client";

import { useOverlayKeys } from "@cove-extensions/ui-shared";

// `act` refuses to run without it, and React reads it off the global rather than from an import.
declare global {
  var IS_REACT_ACT_ENVIRONMENT: boolean;
}
globalThis.IS_REACT_ACT_ENVIRONMENT = true;

function Panel(props: Readonly<{ enabled: boolean; onClose: () => void }>) {
  const ref = useRef<HTMLDivElement>(null);
  useOverlayKeys(ref, {
    nav: "dialog",
    onClose: props.onClose,
    enabled: props.enabled,
    closeOnOutsideClick: false,
  });
  return (
    <div ref={ref}>
      <button type="button">First</button>
      <button type="button">Middle</button>
      <button type="button">Last</button>
    </div>
  );
}

function mount() {
  const opener = document.createElement("button");
  document.body.append(opener);
  opener.focus();

  const onClose = vi.fn();
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  // Committed inside `act`, so a key pressed after it reads this render's `enabled`.
  const render = (enabled: boolean) => {
    act(() => {
      root.render(<Panel enabled={enabled} onClose={onClose} />);
    });
  };
  render(true);

  const named = (label: string) => {
    const found = [...container.querySelectorAll("button")].find((b) => b.textContent === label);
    if (!found) throw new Error(`no ${label} button`);
    return found;
  };

  return {
    opener,
    onClose,
    named,
    setEnabled: render,
    // Returns what holds focus once the panel is gone, read before the opener is removed.
    unmount: () => {
      act(() => {
        root.unmount();
      });
      const focused = document.activeElement;
      container.remove();
      opener.remove();
      return focused;
    },
  };
}

// Presses a key where focus is, as a browser would, and reports whether the hook suppressed it.
function press(key: string, shiftKey = false): boolean {
  const event = new KeyboardEvent("keydown", { key, shiftKey, bubbles: true, cancelable: true });
  (document.activeElement ?? document.body).dispatchEvent(event);
  return event.defaultPrevented;
}

test("opening focuses the first control, and closing hands focus back to the opener", () => {
  const view = mount();
  expect(document.activeElement).toBe(view.named("First"));

  expect(view.unmount()).toBe(view.opener);
});

test("Tab from the last control wraps to the first, and Shift+Tab from the first wraps to the last", () => {
  const view = mount();

  view.named("Last").focus();
  expect(press("Tab"), "the trap let Tab leave the panel").toBe(true);
  expect(document.activeElement).toBe(view.named("First"));

  expect(press("Tab", true), "the trap let Shift+Tab leave the panel").toBe(true);
  expect(document.activeElement).toBe(view.named("Last"));

  view.unmount();
});

test("Tab between two controls inside the panel is left to the browser", () => {
  const view = mount();

  view.named("Middle").focus();
  expect(press("Tab")).toBe(false);
  expect(document.activeElement).toBe(view.named("Middle"));

  view.unmount();
});

test("Escape closes, and the key does not reach the host page", () => {
  const view = mount();

  expect(press("Escape"), "Escape leaked to the host page").toBe(true);
  expect(view.onClose).toHaveBeenCalledTimes(1);

  view.unmount();
});

test("while suspended Escape closes nothing, and the Tab trap keeps running", () => {
  const view = mount();
  view.setEnabled(false);

  press("Escape");
  expect(view.onClose, "a suspended panel closed").not.toHaveBeenCalled();

  view.named("Last").focus();
  expect(press("Tab")).toBe(true);
  expect(document.activeElement).toBe(view.named("First"));

  view.unmount();
});

test("Escape closes again as soon as the panel is re-enabled", () => {
  const view = mount();
  view.setEnabled(false);
  view.setEnabled(true);

  press("Escape");
  expect(view.onClose).toHaveBeenCalledTimes(1);

  view.unmount();
});
