// @vitest-environment jsdom
// The destination editor's path-shape hint names only a control the user can see. The root picker is
// withheld when Cove has no library path to offer, and the hint is the one line saying a typed path is
// about to become literal folder names.
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { DestinationField } from "./DestinationField";
import { CONTAINING_ROOT, type Destination, type LibraryPathsState } from "./options";

// A template a user typed as a path, which is what the hint exists to catch.
const TYPED_PATH = "D:/Media/Studio";

async function renderField(library: LibraryPathsState, template = TYPED_PATH) {
  const value: Destination = { root: CONTAINING_ROOT, template };
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(DestinationField, { value, onChange: () => undefined, library, label: "Folder" }),
  );
  await waitFor("the field to render", () => container.querySelector("input") !== null);

  // Both wordings of the hint open with the same sentence.
  const hint = [...container.querySelectorAll("*")]
    .filter((e) => e.textContent.startsWith("This is a folder template, not a path."))
    .at(-1);
  return {
    hint: hint?.textContent ?? null,
    hasPicker: container.textContent.includes("Under"),
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("with no root picker on screen the hint still warns, without sending the user to one", async () => {
  // Rewording, not suppression: a typed path is still about to become literal folder names, and this
  // is the only line that says so.
  const field = await renderField({ paths: [], loading: false, failed: false });

  expect(field.hasPicker).toBe(false);
  expect(field.hint).toMatch(/becomes folder names/i);
  expect(field.hint).not.toMatch(/beside it/i);
  field.teardown();
});

test("an unreadable library-paths read is the same situation", async () => {
  const field = await renderField({ paths: [], loading: false, failed: true });

  expect(field.hasPicker).toBe(false);
  expect(field.hint).not.toMatch(/beside it/i);
  field.teardown();
});

test("with the picker on screen the hint does point at it", async () => {
  const field = await renderField({ paths: ["D:/library"], loading: false, failed: false });

  expect(field.hasPicker).toBe(true);
  expect(field.hint).toMatch(/beside it/i);
  field.teardown();
});

test("a template that is not a path shape earns no hint either way", async () => {
  const field = await renderField({ paths: [], loading: false, failed: false }, "$studio/$year");

  expect(field.hint).toBeNull();
  field.teardown();
});
