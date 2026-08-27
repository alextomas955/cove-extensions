// @vitest-environment jsdom
/**
 * That the destination editor's advice names a control the user can actually see.
 *
 * The root picker is withheld when Cove has no library path to offer — the read failed, or the host has
 * none configured — and the path-shape hint beside the template input is the one line telling the user
 * a typed path is about to become literal folder names. Sending them to "the root beside it" in that
 * state points at nothing, which leaves the only warning they get unactionable.
 *
 * A DOM is needed because the claim is about which of two sentences is on screen, and that depends on
 * a derivation the component runs. React arrives as its PRODUCTION build (the bundle's
 * `process.env.NODE_ENV` define applies here too), which has no `act`, so the render is flushed by
 * waiting rather than by wrapping.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle. PathShapeHint's stand-in reproduces its real gate — it renders only for an absolute-path
 * shape — by calling the REAL predicate, so a hint this test reads is one the user would see.
 */
import { test, expect, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { CONTAINING_ROOT, type Destination, type LibraryPathsState } from "./options";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  const { isAbsolutePathShape } =
    await import("../../../../../../shared/ui-shared/src/primitivesLogic");
  return {
    Field: (props: { label?: string; helper?: string; children?: ReactNode }) =>
      h("div", { "data-stub": "Field" }, props.label, props.helper, props.children),
    Select: (props: { options?: { label: string }[] }) =>
      h(
        "div",
        { "data-stub": "Select" },
        (props.options ?? []).map((o, i) => h("span", { key: i }, o.label)),
      ),
    TextInput: () => h("input", { "data-stub": "TextInput" }),
    StatusText: (props: { children?: ReactNode }) =>
      h("div", { "data-stub": "StatusText" }, props.children),
    // The real gate, so an assertion here reads a hint the user would be shown.
    PathShapeHint: (props: { value: string; message: string }) =>
      isAbsolutePathShape(props.value)
        ? h("div", { "data-stub": "PathShapeHint" }, props.message)
        : null,
  };
});

const { DestinationField } = await import("./DestinationField");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

/** A template a user typed as a path, which is what the hint exists to catch. */
const TYPED_PATH = "D:/Media/Studio";

async function renderField(library: LibraryPathsState, template = TYPED_PATH) {
  const value: Destination = { Root: CONTAINING_ROOT, Template: template };
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(
    createElement(DestinationField, { value, onChange: () => undefined, library, label: "Folder" }),
  );
  await sleep(COMMIT_MS);

  const hint = container.querySelector('[data-stub="PathShapeHint"]');
  return {
    hint: hint?.textContent ?? null,
    hasPicker: container.textContent.includes("Under"),
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("with no root picker on screen the hint does not send the user to one", async () => {
  // THE case: Cove reported no library paths, so `showPicker` is false and no "Under" select renders.
  const field = await renderField({ paths: [], loading: false, failed: false });

  expect(field.hasPicker).toBe(false);
  expect(field.hint).not.toBeNull();
  expect(field.hint).not.toMatch(/beside it/i);
  field.teardown();
});

test("the hint still says the template is not a path", async () => {
  // Rewording, not suppression: a typed path is still about to become literal folder names, and this
  // is the only line that says so.
  const field = await renderField({ paths: [], loading: false, failed: false });

  expect(field.hint).toMatch(/folder template, not a path/i);
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
