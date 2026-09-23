// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
// The shared TagListInput driven as a combobox: what the box reads, which chips exist and what the
// list offers after each way of committing a value. React writes a controlled input's committed value
// onto its `value` attribute, so each step waits for that attribute before the next key press.
import { test, expect, vi } from "vitest";
import { createElement, useState } from "react";
import { createRoot } from "react-dom/client";

import { TagListInput } from "@cove-extensions/ui-shared";

import { waitFor } from "./common/lib/flushRender";

const SUGGESTIONS = ["title", "studio", "performers", "resolution"];

function Host(props: {
  onLiveChange?: (raw: string) => void;
  onReject?: (candidate: string) => boolean;
}) {
  const [values, setValues] = useState<string[]>([]);
  return createElement(TagListInput, {
    values,
    onChange: setValues,
    suggestions: SUGGESTIONS,
    ariaLabel: "Tokens",
    placeholder: "Add a token",
    onLiveChange: props.onLiveChange,
    onReject: props.onReject,
  });
}

async function render(props: Parameters<typeof Host>[0] = {}) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(Host, props));
  await waitFor("the combobox to render", () => container.querySelector("input") !== null);

  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

function input(container: HTMLElement): HTMLInputElement {
  const el = container.querySelector<HTMLInputElement>('input[role="combobox"]');
  if (!el) throw new Error("no combobox input");
  return el;
}

function offered(container: HTMLElement): string[] {
  return [...container.querySelectorAll('[role="option"]')].map((o) => o.textContent.trim());
}

function chips(container: HTMLElement): string[] {
  return [...container.querySelectorAll('button[aria-label^="Remove "]')].map((b) =>
    (b.getAttribute("aria-label") ?? "").slice("Remove ".length),
  );
}

// Types into the box. Nothing to wait for: React flushes the work an event scheduled before it
// dispatches the next one, so the key press after this already runs a handler that has the text.
// What the test then reads off the screen is what the test waits for.
function type(container: HTMLElement, text: string) {
  const el = input(container);
  // React tracks the input's own `value` property, so assigning to it looks like no change at all.
  // Going through the prototype's setter writes past that tracker, which is what makes the
  // dispatched event read as typing.
  Reflect.set(HTMLInputElement.prototype, "value", text, el);
  el.dispatchEvent(new Event("input", { bubbles: true }));
}

// Presses a key and reports whether the component handled it, which it signals by suppressing it.
async function press(container: HTMLElement, key: string): Promise<boolean> {
  const el = input(container);
  const activeBefore = el.getAttribute("aria-activedescendant");
  const event = new KeyboardEvent("keydown", { key, bubbles: true, cancelable: true });
  el.dispatchEvent(event);

  if (key === "ArrowDown" || key === "ArrowUp") {
    await waitFor(
      "the active option to move",
      () => input(container).getAttribute("aria-activedescendant") !== activeBefore,
    );
  } else if (key === "Escape") {
    await waitFor(
      "the list to close",
      () => input(container).getAttribute("aria-expanded") === "false",
    );
  }
  // Enter has no one outcome to wait on - it commits an option, commits free text, or is refused -
  // so each test waits for the one it is about.
  return event.defaultPrevented;
}

async function focus(container: HTMLElement) {
  input(container).focus();
  await waitFor(
    "the list to open",
    () => input(container).getAttribute("aria-expanded") === "true",
  );
}

// Picks a suggestion with the mouse, as a browser drives it: `mousedown`, then `click`. Reports
// whether the list suppressed the `mousedown`, which is what keeps the caret in the box.
//
// jsdom runs no default action for `mousedown`, so the focus move a real browser performs is
// performed here, and only when the component did not suppress the event. A browser also flushes
// React's pending work before dispatching the click, so the blur's commit is settled first - which is
// what turns "the box lost focus" into "the half-typed query became a chip".
async function clickOption(container: HTMLElement, label: string): Promise<boolean> {
  const option = [...container.querySelectorAll('[role="option"]')].find(
    (o) => o.textContent.trim() === label,
  );
  if (!option) throw new Error(`no option ${label}`);

  const down = new MouseEvent("mousedown", { bubbles: true, cancelable: true });
  option.dispatchEvent(down);
  if (!down.defaultPrevented) {
    input(container).blur();
    await waitFor("the blur to commit what was in the box", () => chips(container).length > 0);
  }
  option.dispatchEvent(new MouseEvent("click", { bubbles: true, cancelable: true }));
  return down.defaultPrevented;
}

async function blur(container: HTMLElement) {
  input(container).blur();
  await waitFor(
    "the list to close",
    () => input(container).getAttribute("aria-expanded") === "false",
  );
}

test("free text committed with Enter empties the box and offers the whole list again", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "tit");
  await press(view.container, "Enter");
  await waitFor("the typed text to become a chip", () => chips(view.container).length === 1);

  expect(chips(view.container)).toEqual(["tit"]);
  expect(input(view.container).value).toBe("");
  expect(offered(view.container)).toEqual(SUGGESTIONS);

  view.unmount();
});

test("Escape hides the list and typing on brings it back with no click away and back", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "tit");
  await waitFor("the list to narrow to one option", () => offered(view.container).length === 1);
  expect(offered(view.container)).toEqual(["title"]);

  await press(view.container, "Escape");
  expect(offered(view.container)).toEqual([]);

  type(view.container, "titl");
  await waitFor("the list to come back", () => offered(view.container).length === 1);
  expect(offered(view.container)).toEqual(["title"]);

  view.unmount();
});

test("a value committed by leaving the field offers the whole list on the next visit", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "tit");
  await blur(view.container);
  await waitFor("leaving the field to commit the text", () => chips(view.container).length === 1);

  expect(chips(view.container)).toEqual(["tit"]);

  await focus(view.container);
  expect(offered(view.container)).toEqual(SUGGESTIONS);

  view.unmount();
});

test("arrowing to a suggestion and pressing Enter adds that suggestion and empties the box", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "stu");
  await press(view.container, "ArrowDown");
  await press(view.container, "Enter");
  await waitFor("the chosen option to become a chip", () => chips(view.container).length === 1);

  expect(chips(view.container)).toEqual(["studio"]);
  expect(input(view.container).value).toBe("");

  view.unmount();
});

test("the sidecar advisory is told the box is empty once free text commits", async () => {
  const onLiveChange = vi.fn<(raw: string) => void>();
  const view = await render({ onLiveChange });
  await focus(view.container);
  type(view.container, "tit");
  await press(view.container, "Enter");
  await waitFor("the advisory to be told the box emptied", () =>
    onLiveChange.mock.calls.some((call) => call[0] === ""),
  );

  expect(onLiveChange.mock.calls.at(-1)?.[0]).toBe("");

  view.unmount();
});

test("Enter on spaces alone adds nothing and leaves what was typed on screen", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "   ");
  // Nothing happens, so there is nothing to wait for. The suppressed key is what says the component
  // saw the Enter and chose to do nothing, rather than that the assertion arrived too early.
  const handled = await press(view.container, "Enter");

  expect(handled, "the component never saw the Enter").toBe(true);
  expect(chips(view.container)).toEqual([]);
  expect(input(view.container).value).toBe("   ");

  view.unmount();
});

test("Enter on a rejected entry adds nothing and leaves the text there to correct", async () => {
  const view = await render({ onReject: (candidate) => candidate === "nope" });
  await focus(view.container);
  type(view.container, "nope");
  const handled = await press(view.container, "Enter");

  expect(handled, "the component never saw the Enter").toBe(true);
  expect(chips(view.container)).toEqual([]);
  expect(input(view.container).value).toBe("nope");

  view.unmount();
});

test("a token already picked is not accepted again in a different case", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "studio");
  await press(view.container, "Enter");
  await waitFor("the first token to become a chip", () => chips(view.container).length === 1);
  expect(chips(view.container)).toEqual(["studio"]);

  type(view.container, "Studio");
  const handled = await press(view.container, "Enter");

  // The rename engine resolves a token case-insensitively, so this is the token already on screen.
  expect(handled, "the component never saw the second Enter").toBe(true);
  expect(chips(view.container)).toEqual(["studio"]);

  view.unmount();
});

test("a token already picked is not offered again in a different case", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "STUDIO");
  await press(view.container, "Enter");
  await waitFor("the token to leave the offer list", () => offered(view.container).length === 3);

  expect(offered(view.container)).toEqual(["title", "performers", "resolution"]);

  view.unmount();
});

test("clicking a suggestion adds that option and not the half-typed query", async () => {
  const view = await render();
  await focus(view.container);
  type(view.container, "stu");
  await waitFor("the list to narrow to one option", () => offered(view.container).length === 1);

  const suppressed = await clickOption(view.container, "studio");
  await waitFor("the option to become a chip", () => chips(view.container).length > 0);

  expect(suppressed, "the list let the mousedown take the caret out of the box").toBe(true);
  // Exactly one chip, and the option's own spelling: "stu" alongside it would be the query committed
  // by a blur the click caused.
  expect(chips(view.container)).toEqual(["studio"]);
  expect(input(view.container).value).toBe("");

  view.unmount();
});
