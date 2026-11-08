// @vitest-environment jsdom
// The page's own decisions over the hooks it composes: what a failed first load shows, and when the
// save bar is on screen. The data hooks and the two request-owning views stand in.
import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import type { UseRenamerOptions } from "./useRenamerOptions";
import type { UseRenameLibrary } from "./useRenameLibrary";
import type { UseRenamePreview } from "./useRenamePreview";
import type { LibraryPathsState } from "./options";

const page = vi.hoisted(() => ({
  load: vi.fn(() => Promise.resolve()),
  loadError: null as string | null,
  dirty: false,
  dryRunOpen: false,
}));

vi.mock("./useRenamerOptions", async () => {
  const { someOptions } = await import("./testOptions");
  const noop = () => undefined;
  return {
    useRenamerOptions: (): UseRenamerOptions => ({
      options: page.loadError === null ? someOptions() : null,
      loading: false,
      loadError: page.loadError,
      saving: false,
      saveError: null,
      savedFlash: false,
      recoveredFromBadBlob: false,
      pendingNameMigration: false,
      pendingDestinationMigration: false,
      dirty: page.dirty,
      canSave: page.dirty,
      load: page.load,
      onSave: () => Promise.resolve(),
      discard: noop,
      set: noop,
      setMulti: noop,
    }),
  };
});
vi.mock("./useRenamePreview", () => ({
  useRenamePreview: (): UseRenamePreview => ({ preview: null, previewError: false }),
}));
vi.mock("./useRenameLibrary", () => ({
  useRenameLibrary: (): UseRenameLibrary => ({
    dryRunOpen: page.dryRunOpen,
    setDryRunOpen: () => undefined,
    renamingLibrary: false,
    runLibraryFeedback: null,
    renameProgress: null,
    renameLibrary: () => Promise.resolve(),
  }),
}));
vi.mock("./useLibraryPaths", () => ({
  useLibraryPaths: (): LibraryPathsState => ({ paths: [], loading: false, failed: false }),
}));
vi.mock("./useOrphanedRules", () => ({
  useOrphanedRules: () => ({ studios: new Set<number>(), tags: new Set<number>() }),
}));
vi.mock("./UndoSection", () => ({ UndoSection: () => null }));
vi.mock("./dry-run/DryRunModal", () => ({ DryRunModal: () => null }));

const { RenamePage } = await import("./RenamePage");

function renderPage() {
  const { container } = render(createElement(RenamePage));
  return {
    text: () => container.textContent,
    saveButton: () => screen.queryByRole("button", { name: "Save changes" }),
  };
}

beforeEach(() => {
  page.load.mockClear();
  page.loadError = null;
  page.dirty = false;
  page.dryRunOpen = false;
});

afterEach(cleanup);

test("a failed first load shows the error and a Retry that loads again, not a spinner", async () => {
  const user = userEvent.setup();
  page.loadError = "500 boom";
  const view = renderPage();

  expect(view.text()).toContain("500 boom");
  expect(view.text()).not.toContain("Loading settings");

  await user.click(screen.getByRole("button", { name: "Retry" }));

  expect(page.load).toHaveBeenCalledTimes(1);
});

test("unsaved edits put the save bar on screen", () => {
  page.dirty = true;
  const view = renderPage();

  expect(view.saveButton()).not.toBeNull();
  expect(view.text()).toContain("Unsaved changes");
});

test("the save bar steps aside while the dry run is open, even with unsaved edits", () => {
  // The bar is the later sibling at the dialog's layer, so on screen it would paint over the modal.
  page.dirty = true;
  page.dryRunOpen = true;
  const view = renderPage();

  expect(view.saveButton()).toBeNull();
  expect(view.text()).not.toContain("Unsaved changes");
});

test("a page with nothing edited shows no save bar", () => {
  const view = renderPage();

  // The Live preview card renders whenever options have loaded, so its heading marks a settled page.
  expect(view.text()).toContain("Live preview");
  expect(view.saveButton()).toBeNull();
});
