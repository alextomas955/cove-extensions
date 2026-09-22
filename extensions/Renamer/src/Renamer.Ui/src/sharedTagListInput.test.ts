// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
/**
 * The shared TagListInput driven as a combobox: what the box reads, which chips exist and what the
 * list offers after each way of committing a value.
 *
 * The real component is the subject, so nothing is mocked. React arrives as its production build,
 * which has no `act`, so each render is flushed by waiting.
 */
import { test, expect, vi } from "vitest";
import { createElement, useState } from "react";
import { createRoot } from "react-dom/client";

import { TagListInput } from "@cove-extensions/ui-shared";

const SUGGESTIONS = ["title", "studio", "performers", "resolution"];

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

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
  await sleep(COMMIT_MS);

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

async function type(container: HTMLElement, text: string) {
  const el = input(container);
  // React tracks the input's own `value` property, so assigning to it looks like no change at all.
  // Going through the prototype's setter writes past that tracker, which is what makes the
  // dispatched event read as typing.
  Reflect.set(HTMLInputElement.prototype, "value", text, el);
  el.dispatchEvent(new Event("input", { bubbles: true }));
  await sleep(COMMIT_MS);
}

async function press(container: HTMLElement, key: string) {
  input(container).dispatchEvent(new KeyboardEvent("keydown", { key, bubbles: true }));
  await sleep(COMMIT_MS);
}

async function focus(container: HTMLElement) {
  input(container).focus();
  await sleep(COMMIT_MS);
}

async function blur(container: HTMLElement) {
  input(container).blur();
  await sleep(COMMIT_MS);
}

test("free text committed with Enter empties the box and offers the whole list again", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "tit");
  await press(view.container, "Enter");

  expect(chips(view.container)).toEqual(["tit"]);
  expect(input(view.container).value).toBe("");
  expect(offered(view.container)).toEqual(SUGGESTIONS);

  view.unmount();
});

test("Escape hides the list and typing on brings it back with no click away and back", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "tit");
  expect(offered(view.container)).toEqual(["title"]);

  await press(view.container, "Escape");
  expect(offered(view.container)).toEqual([]);

  await type(view.container, "titl");
  expect(offered(view.container)).toEqual(["title"]);

  view.unmount();
});

test("a value committed by leaving the field offers the whole list on the next visit", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "tit");
  await blur(view.container);

  expect(chips(view.container)).toEqual(["tit"]);

  await focus(view.container);
  expect(offered(view.container)).toEqual(SUGGESTIONS);

  view.unmount();
});

test("arrowing to a suggestion and pressing Enter adds that suggestion and empties the box", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "stu");
  await press(view.container, "ArrowDown");
  await press(view.container, "Enter");

  expect(chips(view.container)).toEqual(["studio"]);
  expect(input(view.container).value).toBe("");

  view.unmount();
});

test("the sidecar advisory is told the box is empty once free text commits", async () => {
  const onLiveChange = vi.fn<(raw: string) => void>();
  const view = await render({ onLiveChange });
  await focus(view.container);
  await type(view.container, "tit");
  await press(view.container, "Enter");

  expect(onLiveChange.mock.calls.at(-1)?.[0]).toBe("");

  view.unmount();
});

test("Enter on spaces alone adds nothing and leaves what was typed on screen", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "   ");
  await press(view.container, "Enter");

  expect(chips(view.container)).toEqual([]);
  expect(input(view.container).value).toBe("   ");

  view.unmount();
});

test("Enter on a rejected entry adds nothing and leaves the text there to correct", async () => {
  const view = await render({ onReject: (candidate) => candidate === "nope" });
  await focus(view.container);
  await type(view.container, "nope");
  await press(view.container, "Enter");

  expect(chips(view.container)).toEqual([]);
  expect(input(view.container).value).toBe("nope");

  view.unmount();
});

test("a token already picked is not accepted again in a different case", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "studio");
  await press(view.container, "Enter");
  expect(chips(view.container)).toEqual(["studio"]);

  await type(view.container, "Studio");
  await press(view.container, "Enter");

  // The rename engine resolves a token case-insensitively, so this is the token already on screen.
  expect(chips(view.container)).toEqual(["studio"]);

  view.unmount();
});

test("a token already picked is not offered again in a different case", async () => {
  const view = await render();
  await focus(view.container);
  await type(view.container, "STUDIO");
  await press(view.container, "Enter");

  expect(offered(view.container)).toEqual(["title", "performers", "resolution"]);

  view.unmount();
});
