// @vitest-environment jsdom
// What the settings page offers in each state the save bar can be in, driven through the real
// options hook over a scripted `/options` endpoint. Only the views that own a request of their own
// stand in, so the bar's visibility, its wording and whether Save acts are decided here by the same
// code that decides them in the browser.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { withQueryClient } from "../common/lib/testQueryClient";
import type { UseRenamePreview } from "./useRenamePreview";
import type { LibraryPathsState } from "./options";
import type { OptionsView, RenamerOptions } from "./options";

const endpoint = vi.hoisted(() => ({
  view: null as OptionsView | null,
  // Every document the page sent, in order.
  sent: [] as RenamerOptions[],
  failNextRead: false,
  failNextWrite: false,
  // Whether the dry-run modal is open, which the bar has to step aside for.
  dryRunOpen: false,
}));

vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (_path: string, init: RequestInit) => {
    const json = (value: unknown, status = 200) =>
      Promise.resolve(
        new Response(value === undefined ? null : JSON.stringify(value), {
          status,
          headers: { "Content-Type": "application/json" },
        }),
      );

    if (String(init.method).toUpperCase() === "PUT") {
      if (endpoint.failNextWrite) {
        endpoint.failNextWrite = false;
        return json({ detail: "the settings could not be stored" }, 503);
      }
      const body = JSON.parse(init.body as string) as RenamerOptions;
      endpoint.sent.push(body);
      // The PUT replaces the whole document, so a later read answers with what was written and no
      // longer reports the blob unreadable.
      if (endpoint.view) endpoint.view = { ...endpoint.view, options: body, unreadable: false };
      return json(undefined, 204);
    }

    if (endpoint.failNextRead) {
      endpoint.failNextRead = false;
      return json({ detail: "the settings could not be read" }, 503);
    }
    return json(structuredClone(endpoint.view));
  },
}));

// The three views that issue requests of their own. Each is a separate concern with its own tests,
// and leaving them live would make every case here wait on routes it is not about.
vi.mock("./useRenamePreview", () => ({
  useRenamePreview: (): UseRenamePreview => ({ preview: null, previewError: false }),
}));
vi.mock("./useLibraryPaths", () => ({
  useLibraryPaths: (): LibraryPathsState => ({ paths: [], loading: false, failed: false }),
}));
vi.mock("./useOrphanedRules", () => ({
  useOrphanedRules: () => ({ studios: new Set<number>(), tags: new Set<number>() }),
}));
vi.mock("./UndoSection", () => ({ UndoSection: () => null }));
vi.mock("./useRenameLibrary", () => ({
  useRenameLibrary: () => ({
    dryRunOpen: endpoint.dryRunOpen,
    setDryRunOpen: () => undefined,
    renamingLibrary: false,
    runLibraryFeedback: null,
    renameProgress: null,
    renameLibrary: () => Promise.resolve(),
  }),
}));
vi.mock("./dry-run/DryRunModal", () => ({ DryRunModal: () => null }));

const { RenamePage } = await import("./RenamePage");
const { someOptions } = await import("./testOptions");

function answers(overrides: Partial<OptionsView> = {}) {
  endpoint.view = {
    options: someOptions(),
    pendingNameMigration: false,
    pendingDestinationMigration: false,
    unreadable: false,
    ...overrides,
  } satisfies OptionsView;
}

// Render the page and wait for the first read, so every case starts from a settled panel. The
// filename template input is the field the edits below go through.
async function renderPage() {
  const user = userEvent.setup();
  const { container } = render(withQueryClient(createElement(RenamePage)));
  const template = await screen.findByLabelText("Filename template");

  return {
    user,
    text: () => container.textContent,
    saveButton: () => screen.queryByRole<HTMLButtonElement>("button", { name: "Save changes" }),
    discardButton: () => screen.queryByRole("button", { name: "Discard" }),
    template: () => screen.getByLabelText<HTMLInputElement>("Filename template"),
    edit: (value: string) => user.clear(template).then(() => user.type(template, value)),
    // Presses Save. What the save then does is waited for by the sentence each case is about, so
    // this does not guess at a settled state the bar may never reach.
    save: () => user.click(screen.getByRole("button", { name: "Save changes" })),
    // The bar's own sentence, waited for because a save is two round trips.
    says: (text: string) =>
      waitFor(() => {
        expect(container.textContent).toContain(text);
      }),
  };
}

beforeEach(() => {
  endpoint.sent.length = 0;
  endpoint.failNextRead = false;
  endpoint.failNextWrite = false;
  endpoint.dryRunOpen = false;
  answers();
});

afterEach(cleanup);

test("a failed first load shows the error and a Retry that loads again, not a spinner", async () => {
  endpoint.view = null;
  endpoint.failNextRead = true;
  const user = userEvent.setup();
  const { container } = render(withQueryClient(createElement(RenamePage)));

  await waitFor(() => {
    expect(container.textContent).toContain("503");
  });
  expect(container.textContent).not.toContain("Loading settings");

  answers();
  await user.click(screen.getByRole("button", { name: "Retry" }));

  // Retry reaches the endpoint, which now answers, so the panel arrives.
  expect(await screen.findByLabelText("Filename template")).toBeTruthy();
});

test("a settled page with nothing to save shows no save bar", async () => {
  const view = await renderPage();

  expect(view.saveButton()).toBeNull();
});

test("unsaved edits put the save bar on screen", async () => {
  const view = await renderPage();

  await view.edit("$studio - $title");

  expect(view.saveButton()).not.toBeNull();
  expect(view.text()).toContain("Unsaved changes");
});

test("the save bar steps aside while the dry run is open, even with unsaved edits", async () => {
  // Both are fixed at the same layer and the bar is the later sibling, so on screen it would paint
  // over the modal.
  endpoint.dryRunOpen = true;
  const view = await renderPage();

  await view.edit("$studio - $title");

  expect(view.saveButton()).toBeNull();
  expect(view.text()).not.toContain("Unsaved changes");
});

// The bug this covers: the advisory told the user to save the recovered defaults while the page
// offered no Save, because the bar was shown on an unsaved edit alone.
test("an unreadable stored document offers Save with no edit, and says why", async () => {
  answers({ unreadable: true });
  const view = await renderPage();

  expect(view.saveButton()).not.toBeNull();
  expect(view.saveButton()?.disabled, "Save is not available").toBe(false);
  expect(view.text()).toContain("Recovered settings not stored yet");
  // Not the ordinary unsaved-edit wording: there is no edit, and the stored document is the problem.
  expect(view.text()).not.toContain("Unsaved changes");
  // Nothing to take back, so the bar does not offer to.
  expect(view.discardButton()).toBeNull();
});

test("saving the recovered defaults stores them and retires the recovery notice", async () => {
  answers({ unreadable: true });
  const view = await renderPage();

  await view.save();

  expect(endpoint.sent, "the recovered defaults were never written").toHaveLength(1);
  await waitFor(() => {
    expect(view.text()).toContain("Settings saved.");
  });
  expect(view.text()).not.toContain("Recovered settings not stored yet");
  expect(view.text()).not.toContain("couldn't be read and have been reset");
});

// The write is what replaces the bad document. Before the fix the page read `unreadable` off a
// cached response the write had already superseded, so it kept asking for the same save.
test("a recovery save whose re-read fails still retires the recovery", async () => {
  answers({ unreadable: true });
  const view = await renderPage();

  endpoint.failNextRead = true;
  await view.save();

  await waitFor(() => {
    expect(view.text()).toContain(
      "Settings saved. The page couldn't read them back, so what's shown is what was sent.",
    );
  });
  expect(view.text()).not.toContain("Recovered settings not stored yet");
  expect(view.text()).not.toContain("couldn't be read and have been reset");
});

test.each([
  ["pendingNameMigration" as const, "tag and performer rules"],
  ["pendingDestinationMigration" as const, "destination folders"],
])("a document awaiting %s refuses the save and the bar says so", async (pending, subject) => {
  // Recovery would otherwise offer a Save, and the endpoint answers a conversion that is still
  // outstanding with a refusal, so the bar has to carry the refusal rather than a dead button.
  answers({ [pending]: true, unreadable: true });
  const view = await renderPage();

  expect(view.text()).toContain("Saving is paused");
  expect(view.text()).toContain(subject);
  expect(view.saveButton()?.disabled, "Save is offered where the endpoint refuses it").toBe(true);

  // And an edit does not unlock it: the refusal is the server's, not a consequence of having
  // nothing to send.
  await view.edit("$studio");

  expect(view.saveButton()?.disabled, "Save is offered where the endpoint refuses it").toBe(true);
  expect(view.text()).toContain("Saving is paused");
  expect(endpoint.sent).toEqual([]);
});

test("a failed save keeps the edit on screen and offers Save again", async () => {
  const view = await renderPage();

  await view.edit("$studio - $title");
  endpoint.failNextWrite = true;
  await view.save();

  await waitFor(() => {
    expect(view.text()).toContain("Couldn't save settings");
  });
  expect(screen.getByLabelText<HTMLInputElement>("Filename template").value).toBe(
    "$studio - $title",
  );
  expect(view.saveButton()?.disabled, "Save is not available").toBe(false);
});

test("an edit made while a save is in flight is not reported as saved", async () => {
  // The flash and a newer edit can hold at once, and the edit is the later of the two. A bar that
  // let the flash win announced "Settings saved." over changes that had never been sent.
  const view = await renderPage();

  await view.edit("$studio - $title");
  await view.save();
  await waitFor(() => {
    expect(view.text()).toContain("Settings saved.");
  });

  await view.edit("$studio - $date");

  expect(view.text()).not.toContain("Settings saved.");
  expect(view.text()).toContain("Unsaved changes");
  expect(view.saveButton()?.disabled, "Save is not available").toBe(false);
});

test("discard after a save the page could not read back keeps what the server took", async () => {
  const view = await renderPage();
  const written = "$studio - $title";

  await view.edit(written);
  endpoint.failNextRead = true;
  await view.save();

  await view.user.click(screen.getByRole("button", { name: "Discard" }));

  // Discard goes back to what is stored, which is what the write put there, not the older copy the
  // cache still holds.
  expect(screen.getByLabelText<HTMLInputElement>("Filename template").value).toBe(written);
  expect(view.saveButton()).toBeNull();

  // And a later edit rebases on it, so the second write carries both fields.
  await view.user.type(screen.getByLabelText("Folder template"), "x");
  await view.save();

  expect(endpoint.sent).toHaveLength(2);
  expect(endpoint.sent[1].filenameTemplate).toBe(written);
});
