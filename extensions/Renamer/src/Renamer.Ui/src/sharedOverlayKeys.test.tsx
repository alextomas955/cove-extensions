// @vitest-environment jsdom
// This test of a shared hook lives in the consuming package because `react` resolves only here.
// The overlay hook's dialog mode on a bare panel, so nothing but the hook decides what a key does.
import { test, expect, vi, afterEach } from "vitest";
import { useRef } from "react";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { useOverlayKeys } from "@cove-extensions/ui-shared";

afterEach(cleanup);

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
  const view = render(<Panel enabled onClose={onClose} />);

  // A host-page key handler, reading each key the panel left for it. Registered after the panel so
  // it runs second: for `nav: "dialog"` the hook listens on the document in the bubble phase, so a
  // listener registered before the overlay opened would see the key ahead of it.
  const unhandledAtHostPage = vi.fn();
  const listener = (e: KeyboardEvent) => {
    if (!e.defaultPrevented) unhandledAtHostPage(e.key);
  };
  document.addEventListener("keydown", listener);

  return {
    opener,
    onClose,
    unhandledAtHostPage,
    named: (label: string) => screen.getByRole("button", { name: label }),
    setEnabled: (enabled: boolean) => {
      view.rerender(<Panel enabled={enabled} onClose={onClose} />);
    },
    // Returns what holds focus once the panel is gone, read before the opener is removed.
    unmount: () => {
      view.unmount();
      const focused = document.activeElement;
      document.removeEventListener("keydown", listener);
      opener.remove();
      return focused;
    },
  };
}

test("opening focuses the first control, and closing hands focus back to the opener", () => {
  const view = mount();
  expect(document.activeElement).toBe(view.named("First"));

  expect(view.unmount()).toBe(view.opener);
});

test("Tab from the last control wraps to the first, and Shift+Tab from the first wraps to the last", async () => {
  const user = userEvent.setup();
  const view = mount();

  view.named("Last").focus();
  await user.tab();
  expect(document.activeElement).toBe(view.named("First"));

  await user.tab({ shift: true });
  expect(document.activeElement).toBe(view.named("Last"));

  view.unmount();
});

test("Tab between two controls inside the panel is left to the browser", async () => {
  const user = userEvent.setup();
  const view = mount();

  view.named("Middle").focus();
  await user.tab();

  // The browser's own move, which runs only because the trap left the key alone.
  expect(document.activeElement).toBe(view.named("Last"));

  view.unmount();
});

test("Escape closes, and is marked handled so the host page leaves it alone", async () => {
  const user = userEvent.setup();
  const view = mount();

  await user.keyboard("{Escape}");

  expect(view.onClose).toHaveBeenCalledTimes(1);
  expect(view.unhandledAtHostPage).not.toHaveBeenCalled();

  view.unmount();
});

test("while suspended Escape closes nothing, and the Tab trap keeps running", async () => {
  const user = userEvent.setup();
  const view = mount();
  view.setEnabled(false);

  await user.keyboard("{Escape}");
  expect(view.onClose, "a suspended panel closed").not.toHaveBeenCalled();
  // The key the suspended panel left unhandled, which is the positive control for the assertion
  // above: without it, an Escape that never reached the panel at all would read the same.
  expect(view.unhandledAtHostPage).toHaveBeenCalledWith("Escape");

  view.named("Last").focus();
  await user.tab();
  expect(document.activeElement).toBe(view.named("First"));

  view.unmount();
});

test("Escape closes again as soon as the panel is re-enabled", async () => {
  const user = userEvent.setup();
  const view = mount();
  view.setEnabled(false);
  view.setEnabled(true);

  await user.keyboard("{Escape}");

  expect(view.onClose).toHaveBeenCalledTimes(1);

  view.unmount();
});
