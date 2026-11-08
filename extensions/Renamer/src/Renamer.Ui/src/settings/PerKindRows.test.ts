// @vitest-environment jsdom
// What each row says about a kind, and what its two buttons do to the stored map, read off a real
// render. The destination editor stands in, so the rows read only this component's text.
import { test, expect, vi, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, within, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { PerKindRows } from "./PerKindRows";
import { RENAMABLE_KINDS, type LibraryPathsState, type RenamerOptions } from "./options";
import { someOptions } from "./testOptions";

vi.mock("./DestinationField", () => ({
  DestinationField: () => null,
}));

const LIBRARY: LibraryPathsState = { paths: ["D:/library"], loading: false, failed: false };

afterEach(cleanup);

function renderRows(kinds: RenamerOptions["kinds"]) {
  const options = { ...someOptions(), kinds };
  const set = vi.fn();
  const user = userEvent.setup();

  const { container } = render(createElement(PerKindRows, { options, set, library: LIBRARY }));

  // A row is the element holding the kind's name; its buttons are the ones that act on that kind.
  const row = (label: string) => {
    const element = screen.getByText(label).parentElement;
    if (!element) throw new Error(`No row for ${label}`);
    const button = (name: string) =>
      within(element).getByRole<HTMLButtonElement>("button", { name });
    return {
      text: () => element.textContent,
      button,
      click: (name: string) => user.click(button(name)),
    };
  };

  return { set, row, text: () => container.textContent };
}

test("a kind with nothing stored reads as following the default", () => {
  const view = renderRows({});

  expect(view.row("Videos").text()).toContain("Follows the default");
  expect(view.text()).toContain(`All ${RENAMABLE_KINDS.length} kinds follow this default.`);
});

test("excluding a kind turns off renaming, without touching the others", async () => {
  const image = { enabled: true, destination: { root: "D:/library", template: "$studio" } };
  const view = renderRows({ image });

  await view.row("Videos").click("Exclude");

  expect(view.set).toHaveBeenCalledWith("kinds", {
    image,
    video: { enabled: false, destination: null },
  });
});

test("an excluded kind reads as not renamed, and offers the way back", async () => {
  const view = renderRows({ video: { enabled: false, destination: null } });

  expect(view.row("Videos").text()).toContain("Not renamed");
  expect(view.text()).toContain("1 excluded");

  await view.row("Videos").click("Include");

  // Including drops the entry rather than storing an enabled kind with no folder, which is what the
  // absent entry already means.
  expect(view.set).toHaveBeenCalledWith("kinds", {});
});

test("an excluded kind cannot be sent back to the default without being included first", async () => {
  const view = renderRows({ video: { enabled: false, destination: null } });

  expect(view.row("Videos").button("Use default").disabled).toBe(true);
  await view.row("Videos").click("Use default");

  // Pressing it must not quietly start renaming a kind the user excluded on purpose.
  expect(view.set).not.toHaveBeenCalled();
});

test("excluding a kind that has its own folder keeps that folder for its return", async () => {
  const destination = { root: "D:/library", template: "$studio" };
  const view = renderRows({ video: { enabled: true, destination } });

  expect(view.text()).toContain("1 with their own folder");
  await view.row("Videos").click("Exclude");

  expect(view.set).toHaveBeenCalledWith("kinds", {
    video: { enabled: false, destination },
  });
});

test("a kind with its own folder is sent back to the default by the left button", async () => {
  const view = renderRows({
    video: { enabled: true, destination: { root: "D:/library", template: "$studio" } },
  });

  await view.row("Videos").click("Use default");

  expect(view.set).toHaveBeenCalledWith("kinds", {});
});
