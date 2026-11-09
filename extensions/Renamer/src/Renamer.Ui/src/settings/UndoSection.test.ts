// @vitest-environment jsdom
// What the panel says after an undo whose outcome nobody knows. `/undo` cannot be repeated, so a
// transport failure, where the server may have restored all, part or none of the batch, must never
// read as a confident success. The host's own fetch is the one stand-in.
// Registers the interceptor that turns a refused request into an ApiError carrying its status.
import "../common/lib/wireErrors";
import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { createElement } from "react";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";

import { withQueryClient } from "../common/lib/testQueryClient";
import type { LastBatchSummary, UndoResult } from "../wire";

const server = vi.hoisted(() => ({
  // How the undo POST ends: an Error the request fails with before any reply, a status and body the
  // server answers with, or null for a clean full restore. A status alone does not settle the arm -
  // the panel may say the batch is untouched only where the server refused to act on it.
  undoFailure: null as Error | { status: number; body: string } | null,
  // Refuses every summary read from the undo onward, which is the read the undo itself starts.
  failSummaryAfterUndo: false,
  undone: false,
}));

vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (_path: string, init: RequestInit) => {
    const json = (value: unknown, status = 200) =>
      Promise.resolve(
        new Response(typeof value === "string" ? value : JSON.stringify(value), {
          status,
          headers: { "Content-Type": "application/json" },
        }),
      );

    if (String(init.method).toUpperCase() !== "POST") {
      if (server.failSummaryAfterUndo && server.undone) {
        return json({ detail: "the summary could not be read" }, 503);
      }
      // Written just now, so the panel offers the button rather than an expired line.
      return json({
        hasBatch: true,
        count: 4,
        remainingCount: 4,
        unrestorableCount: 0,
        writtenAtUtcTicks: Date.now() * 10000 + 621355968000000000,
        // A week out, as the server states it: the panel prints this and derives nothing.
        expiresAtUtcTicks: (Date.now() + 604_800_000) * 10000 + 621355968000000000,
        consumed: false,
      } satisfies LastBatchSummary);
    }

    server.undone = true;
    if (server.undoFailure instanceof Error) return Promise.reject(server.undoFailure);
    if (server.undoFailure) return json(server.undoFailure.body, server.undoFailure.status);
    return json({
      undone: 4,
      failedCount: 0,
      failedSample: [],
      skippedCount: 0,
      skippedSample: [],
      warningCount: 0,
      warningSample: [],
    } satisfies UndoResult);
  },
}));

const { UndoSection } = await import("./UndoSection");

// Every arm under test ends on one of these sentences.
const VERDICT = /Undone: |Couldn't confirm the undo|Couldn't undo/;

// Mount the section, run the undo to its verdict, and hand back the text a user would read.
async function undoAndReadFeedback(): Promise<string> {
  const user = userEvent.setup();
  const { container } = render(withQueryClient(createElement(UndoSection)));

  await user.click(await screen.findByRole("button", { name: /Undo last rename/ }));
  await user.click(await screen.findByRole("button", { name: /^Undo \d+ rename/ }));
  // The verdict is a sentence the panel assembles from several elements, so it is waited for over
  // the card's whole text rather than queried as one node.
  await waitFor(() => {
    expect(container.textContent).toMatch(VERDICT);
  });

  return container.textContent;
}

beforeEach(() => {
  server.undoFailure = null;
  server.failSummaryAfterUndo = false;
  server.undone = false;
});

afterEach(cleanup);

// Every arm where the server may already have moved part or all of the batch back. The reply that
// will not parse is sent as a real 200 the client then fails to read, because that is the shape the
// generated client produces: a rejection handed straight to the panel would pass whatever the
// boundary does with a reply it did receive.
test.each([
  [
    "a dropped connection",
    new TypeError("Failed to fetch") as Error | { status: number; body: string },
  ],
  ["a reply that would not parse", { status: 200, body: "{ not json" }],
  ["a server that failed part way", { status: 500, body: "boom" }],
])("an undo lost to %s is not reported as a completed undo", async (_case, failure) => {
  server.undoFailure = failure;
  const text = await undoAndReadFeedback();

  expect(text).not.toContain("Undone: ");
  expect(text).toMatch(/couldn't confirm/i);
  // Not "nothing was changed": that would say there is nothing left to re-check, which is the one
  // claim this arm cannot make. The replay moves the batch back a page at a time, so a 500 can
  // arrive over files that are already restored.
  expect(text).not.toMatch(/nothing was changed/i);
  expect(text).toMatch(/check the batch/i);
});

test("a refusal the server answered still reads as a failure that changed nothing", async () => {
  // 403 is the undo's only refusal, and it is decided before the first file is touched, so the
  // batch really is untouched and saying so is correct here.
  server.undoFailure = { status: 403, body: "forbidden" };
  const text = await undoAndReadFeedback();

  expect(text).toMatch(/couldn't undo/i);
  expect(text).toMatch(/nothing was changed/i);
});

test("a clean undo still reports the restore it performed", async () => {
  const text = await undoAndReadFeedback();

  expect(text).not.toMatch(/couldn't/i);
  expect(text).toContain("Undone: 4 files moved back to their original names.");
});

test("an undo whose summary re-read fails still reports what the undo did", async () => {
  server.failSummaryAfterUndo = true;
  const text = await undoAndReadFeedback();

  expect(text).toContain("Undone: 4 files moved back to their original names.");
  expect(text).toContain("Couldn't check for a recent rename");
});
