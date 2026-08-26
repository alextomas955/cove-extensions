// @vitest-environment jsdom
/**
 * That the save refusal is VISIBLE. A dead Save button with no reason reads as the user's own mistake,
 * and the hook's suite can only prove the write was refused — not that anything on screen says so. So
 * this renders the real section and reads the rendered text, which is the only form of the claim a
 * user would recognise.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle: that package deliberately has no node_modules of its own. Each stand-in renders the
 * text-bearing props and the children it is handed and nothing else, so what the assertions read is
 * this section's own output.
 *
 * React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define applies here too),
 * which has no `act`, so the render is flushed by waiting rather than by wrapping.
 */
import { test, expect, vi } from "vitest";
import { createElement, createRef, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import { FilenameSection, type FilenameSectionProps } from "./FilenameSection";
import { cloneDefaults, type LibraryPathsState } from "./options";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  const stub = (name: string) =>
    function Stub(props: Record<string, unknown>) {
      return h(
        "div",
        { "data-stub": name },
        props.label as string,
        props.title as string,
        props.description as string,
        props.children as ReactNode,
      );
    };

  return {
    Field: stub("Field"),
    TextInput: stub("TextInput"),
    Subsection: stub("Subsection"),
    SectionCard: stub("SectionCard"),
    Chip: stub("Chip"),
    StatusText: stub("StatusText"),
    Select: stub("Select"),
    PathShapeHint: stub("PathShapeHint"),
  };
});

const LIBRARY: LibraryPathsState = { paths: ["D:/library"], loading: false, failed: false };

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

async function renderSection(overrides: Partial<FilenameSectionProps>) {
  const props: FilenameSectionProps = {
    options: cloneDefaults(),
    set: () => undefined,
    insertToken: () => undefined,
    filenameRef: createRef<HTMLInputElement>(),
    folderRef: createRef<HTMLInputElement>(),
    activeTemplateRef: { current: "filename" },
    emptySamples: [],
    recoveredFromBadBlob: false,
    pendingNameMigration: false,
    pendingDestinationMigration: false,
    library: LIBRARY,
    ...overrides,
  };

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(FilenameSection, props));
  await sleep(COMMIT_MS);

  return {
    text: () => container.textContent,
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("a pending conversion says why saving is off and whose move it is", async () => {
  const view = await renderSection({ pendingNameMigration: true });

  const text = view.text();
  expect(text).toContain("still stored by name");
  expect(text).toContain("waiting for a one-time conversion that runs when Cove starts");
  // Naming the cause is not enough: without the remedy a user reads the dead Save button as their own
  // mistake and reconfigures the panel, which is the save this refusal exists to stop.
  expect(text).toContain("Saving is disabled until then");
  expect(text).toContain("Restart Cove, then reload this page.");

  view.unmount();
});

test("a pending destination conversion says what unblocks it, which is not a restart alone", async () => {
  const view = await renderSection({ pendingDestinationMigration: true });

  const text = view.text();
  expect(text).toContain("still stored as plain paths");
  expect(text).toContain("Saving is disabled until then");
  // The two halves wait on different things. Telling this user only to restart sends them round the
  // loop forever, because the conversion has no root to place a folder under until Cove has one.
  expect(text).toContain("needs at least one library path configured in Cove");
  expect(text).toContain("then restart Cove and reload this page");
  // And it must not borrow the other half's diagnosis: nothing here is stored by name.
  expect(text).not.toContain("still stored by name");

  view.unmount();
});

test("a converted install is told none of that", async () => {
  const view = await renderSection({});

  expect(view.text()).not.toContain("Saving is disabled");
  expect(view.text()).not.toContain("still stored by name");
  expect(view.text()).not.toContain("still stored as plain paths");

  view.unmount();
});
