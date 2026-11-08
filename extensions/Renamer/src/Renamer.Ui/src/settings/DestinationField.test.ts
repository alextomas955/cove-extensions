// @vitest-environment jsdom
// The destination editor's path-shape hint names only a control the user can see. The root picker is
// withheld when Cove has no library path to offer, and the hint is the one line saying a typed path is
// about to become literal folder names.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";

import { DestinationField } from "./DestinationField";
import { CONTAINING_ROOT, type Destination, type LibraryPathsState } from "./options";

// A template a user typed as a path, which is what the hint exists to catch.
const TYPED_PATH = "D:/Media/Studio";

// Both wordings of the hint open with the same sentence.
const HINT = /^This is a folder template, not a path\./;

afterEach(cleanup);

function renderField(library: LibraryPathsState, template = TYPED_PATH) {
  const value: Destination = { root: CONTAINING_ROOT, template };
  const { container } = render(
    createElement(DestinationField, { value, onChange: () => undefined, library, label: "Folder" }),
  );

  return {
    hint: screen.queryByText(HINT)?.textContent ?? null,
    hasPicker: container.textContent.includes("Under"),
  };
}

test("with no root picker on screen the hint still warns, without sending the user to one", () => {
  // Rewording, not suppression: a typed path is still about to become literal folder names, and this
  // is the only line that says so.
  const field = renderField({ paths: [], loading: false, failed: false });

  expect(field.hasPicker).toBe(false);
  expect(field.hint).toMatch(/becomes folder names/i);
  expect(field.hint).not.toMatch(/beside it/i);
});

test("an unreadable library-paths read is the same situation", () => {
  const field = renderField({ paths: [], loading: false, failed: true });

  expect(field.hasPicker).toBe(false);
  expect(field.hint).not.toMatch(/beside it/i);
});

test("with the picker on screen the hint does point at it", () => {
  const field = renderField({ paths: ["D:/library"], loading: false, failed: false });

  expect(field.hasPicker).toBe(true);
  expect(field.hint).toMatch(/beside it/i);
});

test("a template that is not a path shape earns no hint either way", () => {
  const field = renderField({ paths: [], loading: false, failed: false }, "$studio/$year");

  expect(field.hint).toBeNull();
});
