// @vitest-environment jsdom
// A refused save says so on screen. The hook's suite proves the write was refused; only the rendered
// text shows a user why the Save button is dead.
import { test, expect } from "vitest";
import { createElement, createRef } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { FilenameSection, type FilenameSectionProps } from "./FilenameSection";
import { type LibraryPathsState } from "./options";
import { someOptions } from "./testOptions";

const LIBRARY: LibraryPathsState = { paths: ["D:/library"], loading: false, failed: false };

async function renderSection(overrides: Partial<FilenameSectionProps>) {
  const props: FilenameSectionProps = {
    options: someOptions(),
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
  await waitFor("the card to render", () => container.textContent.includes("Presets"));

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
