// @vitest-environment jsdom
// The page's own decisions over the hooks it composes: what a failed first load shows, and when the
// save bar is on screen. The data hooks and the two request-owning views stand in.
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
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
    undoRefreshKey: 0,
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

async function renderPage(ready: (host: HTMLElement) => boolean) {
  const host = document.createElement("div");
  document.body.append(host);
  const root = createRoot(host);
  root.render(createElement(RenamePage));
  await waitFor("the page to render", () => ready(host));

  return {
    host,
    saveButton: () =>
      [...host.querySelectorAll("button")].find((b) => b.textContent === "Save changes"),
    unmount: () => {
      root.unmount();
      host.remove();
    },
  };
}

// The Live preview card renders whenever options have loaded, so its heading marks a settled page.
const loaded = (host: HTMLElement) => host.textContent.includes("Live preview");

beforeEach(() => {
  page.load.mockClear();
  page.loadError = null;
  page.dirty = false;
  page.dryRunOpen = false;
});

test("a failed first load shows the error and a Retry that loads again, not a spinner", async () => {
  page.loadError = "500 boom";
  const view = await renderPage((host) => host.textContent.includes("500 boom"));

  expect(view.host.textContent).not.toContain("Loading settings");

  const retry = [...view.host.querySelectorAll("button")].find((b) => b.textContent === "Retry");
  retry?.click();
  expect(page.load).toHaveBeenCalledTimes(1);

  view.unmount();
});

test("unsaved edits put the save bar on screen", async () => {
  page.dirty = true;
  const view = await renderPage(loaded);

  expect(view.saveButton()).toBeDefined();
  expect(view.host.textContent).toContain("Unsaved changes");

  view.unmount();
});

test("the save bar steps aside while the dry run is open, even with unsaved edits", async () => {
  // The bar is the later sibling at the dialog's layer, so on screen it would paint over the modal.
  page.dirty = true;
  page.dryRunOpen = true;
  const view = await renderPage(loaded);

  expect(view.saveButton()).toBeUndefined();
  expect(view.host.textContent).not.toContain("Unsaved changes");

  view.unmount();
});

test("a page with nothing edited shows no save bar", async () => {
  const view = await renderPage(loaded);

  expect(view.saveButton()).toBeUndefined();

  view.unmount();
});
