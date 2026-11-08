// @vitest-environment jsdom
// A refused save says so on screen. The hook's suite proves the write was refused; only the rendered
// text shows a user why the Save button is dead.
import { test, expect, afterEach } from "vitest";
import { createElement, createRef } from "react";
import { render, cleanup } from "@testing-library/react";

import { FilenameSection, type FilenameSectionProps } from "./FilenameSection";
import { type LibraryPathsState } from "./options";
import { someOptions } from "./testOptions";

const LIBRARY: LibraryPathsState = { paths: ["D:/library"], loading: false, failed: false };

afterEach(cleanup);

// The card's whole text. Each claim below spans several elements, so it is read as one string.
function sectionText(overrides: Partial<FilenameSectionProps>): string {
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

  return render(createElement(FilenameSection, props)).container.textContent;
}

test("a pending conversion says why saving is off and whose move it is", () => {
  const text = sectionText({ pendingNameMigration: true });

  expect(text).toContain("still stored by name");
  expect(text).toContain("waiting for a one-time conversion that runs when Cove starts");
  // Naming the cause is not enough: without the remedy a user reads the dead Save button as their own
  // mistake and reconfigures the panel, which is the save this refusal exists to stop.
  expect(text).toContain("Saving is disabled until then");
  expect(text).toContain("Restart Cove, then reload this page.");
});

test("a pending destination conversion says what unblocks it, which is not a restart alone", () => {
  const text = sectionText({ pendingDestinationMigration: true });

  expect(text).toContain("still stored as plain paths");
  expect(text).toContain("Saving is disabled until then");
  // The two halves wait on different things. Telling this user only to restart sends them round the
  // loop forever, because the conversion has no root to place a folder under until Cove has one.
  expect(text).toContain("needs at least one library path configured in Cove");
  expect(text).toContain("then restart Cove and reload this page");
  // And it must not borrow the other half's diagnosis: nothing here is stored by name.
  expect(text).not.toContain("still stored by name");
});

test("a converted install is told none of that", () => {
  const text = sectionText({});

  expect(text).not.toContain("Saving is disabled");
  expect(text).not.toContain("still stored by name");
  expect(text).not.toContain("still stored as plain paths");
});
