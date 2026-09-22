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
 * A render commits on React's own schedule, so the test waits for the rows to appear rather than
 * for a span.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { PerKindRows } from "./PerKindRows";
import { RENAMABLE_KINDS, type LibraryPathsState, type RenamerOptions } from "./options";
import { someOptions } from "./testOptions";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    StatusText: ({ children }: { children: ReactNode }) => h("span", null, children),
    Button: ({
      children,
      onClick,
      disabled,
    }: {
      children: ReactNode;
      onClick: () => void;
      disabled?: boolean;
    }) => h("button", { type: "button", onClick, disabled }, children),
  };
});

vi.mock("./DestinationField", () => ({
  DestinationField: () => null,
}));

const LIBRARY: LibraryPathsState = { paths: ["D:/library"], loading: false, failed: false };

async function renderRows(kinds: RenamerOptions["kinds"]) {
  const options = { ...someOptions(), kinds };
  const set = vi.fn();

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(PerKindRows, { options, set, library: LIBRARY }));
  await waitFor("the rows to render", () => container.querySelector("button") !== null);

  const row = (label: string) => {
    const cell = [...container.querySelectorAll("span")].find((s) => s.textContent === label);
    const element = cell?.parentElement;
    if (!element) throw new Error(`No row for ${label}`);
    const button = (name: string) => {
      const target = [...element.querySelectorAll("button")].find((b) => b.textContent === name);
      if (!target) throw new Error(`No "${name}" button in the ${label} row`);
      return target;
    };
    return {
      text: () => element.textContent,
      button,
      click: (name: string) => {
        button(name).click();
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
  expect(view.text()).toContain(`All ${RENAMABLE_KINDS.length} kinds follow this default.`);

  view.unmount();
});

test("excluding a kind turns off renaming and says so, without touching the others", async () => {
  const view = await renderRows({});
  view.row("Videos").click("Exclude");

  expect(view.set).toHaveBeenCalledWith("kinds", {
    video: { enabled: false, destination: null },
  });

  view.unmount();
});

test("an excluded kind reads as not renamed, and offers the way back", async () => {
  const view = await renderRows({ video: { enabled: false, destination: null } });

  expect(view.row("Videos").text()).toContain("Not renamed");
  expect(view.text()).toContain("1 excluded");
  view.row("Videos").click("Include");

  // Including drops the entry rather than storing an enabled kind with no folder, which is what the
  // absent entry already means.
  expect(view.set).toHaveBeenCalledWith("kinds", {});

  view.unmount();
});

test("an excluded kind cannot be sent back to the default without being included first", async () => {
  const view = await renderRows({ video: { enabled: false, destination: null } });

  const useDefault = view.row("Videos").button("Use default");
  expect(useDefault.disabled).toBe(true);
  useDefault.click();

  // Pressing it must not quietly start renaming a kind the user excluded on purpose.
  expect(view.set).not.toHaveBeenCalled();

  view.unmount();
});

test("excluding a kind that has its own folder keeps that folder for its return", async () => {
  const destination = { root: "D:/library", template: "$studio" };
  const view = await renderRows({ video: { enabled: true, destination } });

  expect(view.text()).toContain("1 with their own folder");
  view.row("Videos").click("Exclude");

  expect(view.set).toHaveBeenCalledWith("kinds", {
    video: { enabled: false, destination },
  });

  view.unmount();
});

test("a kind with its own folder is sent back to the default by the left button", async () => {
  const view = await renderRows({
    video: { enabled: true, destination: { root: "D:/library", template: "$studio" } },
  });

  view.row("Videos").click("Use default");

  expect(view.set).toHaveBeenCalledWith("kinds", {});

  view.unmount();
});
