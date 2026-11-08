// @vitest-environment jsdom
// This test of a shared component lives in the consuming package because `react` resolves only here.
// When the replacement input is on screen. "Replace with" is a mode the user picks, so it survives an
// empty field; a value emptied from outside the control is a different thing and puts it away.
import { test, expect } from "vitest";
import { useState } from "react";
import { createRoot } from "react-dom/client";

import { SegmentedReplace } from "@cove-extensions/ui-shared";

import { waitFor } from "./common/lib/flushRender";

function Host(props: Readonly<{ initial: string }>) {
  const [value, setValue] = useState(props.initial);
  return (
    <div>
      <button
        type="button"
        onClick={() => {
          setValue("");
        }}
      >
        Reset from outside
      </button>
      <SegmentedReplace
        value={value}
        onChange={setValue}
        stripLabel="Strip"
        replaceLabel="Replace with"
        ariaLabel="Replacement"
      />
    </div>
  );
}

function render(initial: string) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(<Host initial={initial} />);
  return {
    container,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

function field(container: HTMLElement): HTMLInputElement | null {
  return container.querySelector<HTMLInputElement>('input[aria-label="Replacement"]');
}

function chip(container: HTMLElement, label: string): HTMLButtonElement {
  const found = [...container.querySelectorAll("button")].find((b) => b.textContent === label);
  if (!found) throw new Error(`no chip labelled ${label}`);
  return found;
}

function selected(container: HTMLElement, label: string): boolean {
  return chip(container, label).getAttribute("aria-pressed") === "true";
}

// Types into the replacement field. React tracks the input's own `value` property, so assigning to it
// looks like no change at all; going through the prototype's setter writes past that tracker.
function type(el: HTMLInputElement, text: string) {
  Reflect.set(HTMLInputElement.prototype, "value", text, el);
  el.dispatchEvent(new Event("input", { bubbles: true }));
}

async function rendered(initial: string) {
  const view = render(initial);
  await waitFor("the control to render", () => view.container.querySelector("button") !== null);
  return view;
}

test("a stored replacement opens in replace mode with the field showing it", async () => {
  const view = await rendered("_");

  expect(selected(view.container, "Replace with")).toBe(true);
  expect(field(view.container)?.value).toBe("_");

  view.unmount();
});

test("choosing Replace with on an empty value reveals the field to type into", async () => {
  const view = await rendered("");
  expect(field(view.container)).toBeNull();

  chip(view.container, "Replace with").click();
  await waitFor("the field to appear", () => field(view.container) !== null);

  expect(selected(view.container, "Replace with")).toBe(true);

  view.unmount();
});

test("emptying the field while typing leaves it on screen", async () => {
  const view = await rendered("ab");
  const el = field(view.container);
  if (!el) throw new Error("no field");

  type(el, "");
  await waitFor("the edit to land", () => field(view.container)?.value === "");

  // Collapsing here would take the caret out of a field the user is still in.
  expect(field(view.container)).not.toBeNull();
  expect(selected(view.container, "Replace with")).toBe(true);

  view.unmount();
});

test("choosing Strip clears the value and puts the field away", async () => {
  const view = await rendered("_");

  chip(view.container, "Strip").click();
  await waitFor("the field to go", () => field(view.container) === null);

  expect(selected(view.container, "Strip")).toBe(true);

  view.unmount();
});

test("a value emptied from outside the control returns it to strip mode", async () => {
  const view = await rendered("_");
  expect(field(view.container)).not.toBeNull();

  const reset = [...view.container.querySelectorAll("button")].find(
    (b) => b.textContent === "Reset from outside",
  );
  reset?.click();
  await waitFor("the field to go", () => field(view.container) === null);

  expect(selected(view.container, "Strip")).toBe(true);

  view.unmount();
});
