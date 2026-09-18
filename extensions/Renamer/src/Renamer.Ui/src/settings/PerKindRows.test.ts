// @vitest-environment jsdom
/**
 * What each row says about a kind, and what the two buttons do to the stored map. The state line and
 * the button labels are the whole of what a user reads here, so they are read off a real render
 * rather than inferred from the pure map helper.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle. `Button` stands in as a real <button> so a click reaches the handler; DestinationField
 * stands in whole, so what the assertions read is this component's own output.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here
 * too), which has no `act`, so the render is flushed by waiting rather than by wrapping.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { PerKindRows } from "./PerKindRows";
import { cloneDefaults, type LibraryPathsState, type RenamerOptions } from "./options";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    StatusText: ({ children }: { children: ReactNode }) => h("span", null, children),
    Button: ({ children, onClick }: { children: ReactNode; onClick: () => void }) =>
      h("button", { type: "button", onClick }, children),
  };
});

vi.mock("./DestinationField", () => ({
  DestinationField: () => null,
}));

const LIBRARY: LibraryPathsState = { paths: ["D:/library"], loading: false, failed: false };

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

async function renderRows(kinds: RenamerOptions["Kinds"]) {
  const options = { ...cloneDefaults(), Kinds: kinds };
  const set = vi.fn();

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(PerKindRows, { options, set, library: LIBRARY }));
  await sleep(COMMIT_MS);

  const row = (label: string) => {
    const cell = [...container.querySelectorAll("span")].find((s) => s.textContent === label);
    const element = cell?.parentElement;
    if (!element) throw new Error(`No row for ${label}`);
    return {
      text: () => element.textContent,
      click: (button: string) => {
        const target = [...element.querySelectorAll("button")].find(
          (b) => b.textContent === button,
        );
        if (!target) throw new Error(`No "${button}" button in the ${label} row`);
        target.click();
      },
    };
  };

  return {
    set,
    row,
    text: () => container.textContent,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("a kind with nothing stored reads as following the default", async () => {
  const view = await renderRows({});

  expect(view.row("Videos").text()).toContain("Follows the default");
  expect(view.text()).toContain("Every kind follows the settings above");

  view.unmount();
});

test("excluding a kind turns off renaming and says so, without touching the others", async () => {
  const view = await renderRows({});
  view.row("Videos").click("Exclude");

  expect(view.set).toHaveBeenCalledWith("Kinds", {
    Video: { Enabled: false, Destination: null },
  });

  view.unmount();
});

test("an excluded kind reads as not renamed, and offers the way back", async () => {
  const view = await renderRows({ Video: { Enabled: false, Destination: null } });

  expect(view.row("Videos").text()).toContain("Not renamed");
  expect(view.text()).toContain("1 excluded");
  view.row("Videos").click("Include");

  // Including drops the entry rather than storing an enabled kind with no folder, which is what the
  // absent entry already means.
  expect(view.set).toHaveBeenCalledWith("Kinds", {});

  view.unmount();
});

test("excluding a kind that has its own folder keeps that folder for its return", async () => {
  const destination = { Root: "D:/library", Template: "$studio" };
  const view = await renderRows({ Video: { Enabled: true, Destination: destination } });

  expect(view.text()).toContain("1 with their own folder");
  view.row("Videos").click("Exclude");

  expect(view.set).toHaveBeenCalledWith("Kinds", {
    Video: { Enabled: false, Destination: destination },
  });

  view.unmount();
});

test("a kind with its own folder is sent back to the default by the left button", async () => {
  const view = await renderRows({
    Video: { Enabled: true, Destination: { Root: "D:/library", Template: "$studio" } },
  });

  view.row("Videos").click("Use default");

  expect(view.set).toHaveBeenCalledWith("Kinds", {});

  view.unmount();
});
