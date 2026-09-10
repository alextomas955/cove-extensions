// @vitest-environment jsdom
/**
 * That an unavailable switch does nothing when pressed, and that its reason is part of what names
 * it.
 *
 * The real `Toggle` renders here rather than a stand-in: whether a press does nothing rests on the
 * attribute the primitive sets, and a stand-in reproducing that attribute would assert the test's
 * own markup instead. A `<button>` is a labelable element, so the wrapping label's text in order is
 * the switch's accessible name.
 */
import { test, expect } from "vitest";
import { createElement } from "react";

import { render, press } from "../lib/testRender";
import { CAP_UNAVAILABLE_ON_THIS_GENERATION } from "./copy";
import { DisabledToggle } from "./DisabledToggle";

const LABEL = "Monitor";
const REASON = CAP_UNAVAILABLE_ON_THIS_GENERATION;
const HELPER = "Marking a scene wanted downloads nothing by itself.";

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

function toggle(reason: string | null, calls: boolean[], helper?: string) {
  return createElement(DisabledToggle, {
    label: LABEL,
    checked: false,
    reason,
    helper,
    onChange: (checked: boolean) => {
      calls.push(checked);
    },
  });
}

test("a switch with a reason in force cannot be flipped and reports nothing", async () => {
  const calls: boolean[] = [];
  const host = await render(toggle(REASON, calls));

  const control = host.querySelector('[role="switch"]');
  await press(control ?? undefined);

  expect(calls).toEqual([]);
  expect(control?.getAttribute("aria-checked")).toBe("false");
});

test("the switch is named by its own label and then by the reason", async () => {
  const host = await render(toggle(REASON, []));

  const label = host.querySelector("label");
  expect(label?.textContent).toBe(`${LABEL}${REASON}`);
  expect(visibleText(label as Element)).toBe(LABEL);
  expect(host.querySelector("[title]")?.getAttribute("title")).toBe(REASON);
});

test("a switch with no reason flips and reports the new state once", async () => {
  const calls: boolean[] = [];
  const host = await render(toggle(null, calls));

  const control = host.querySelector('[role="switch"]');
  await press(control ?? undefined);

  expect(calls).toEqual([true]);
  expect(host.querySelector("[title]")).toBeNull();
});

test("a reason in force is the only thing a pointer reads, even beside a helper", async () => {
  const host = await render(toggle(REASON, [], HELPER));

  // A pointer reads the nearest titled ancestor, so a second title anywhere under the reason
  // would win over it and the reader would never see why the switch is unavailable.
  const titled = [...host.querySelectorAll("[title]")].map((node) => node.getAttribute("title"));

  expect(titled).toEqual([REASON]);
  expect(host.textContent).toContain(HELPER);
});

test("an available switch still explains itself on hover", async () => {
  const host = await render(toggle(null, [], HELPER));

  const titled = [...host.querySelectorAll("[title]")].map((node) => node.getAttribute("title"));

  expect(titled).toEqual([HELPER]);
});
