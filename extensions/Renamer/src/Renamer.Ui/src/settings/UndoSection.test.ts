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

import { createTestQueryClient, withQueryClient } from "../common/lib/testQueryClient";
import type { LastBatchSummary, UndoResult } from "../wire";
import { jsonResponse } from "../common/lib/testResponses";

// The three counters the summary carries. The server states all three; nothing here derives one
// from the others.
interface Batch {
  count: number;
  remainingCount: number;
  unrestorableCount: number;
}

interface ServerScript {
  // How the undo POST ends: an Error the request fails with before any reply, a status and body the
  // server answers with, or null for a clean full restore. A status alone does not settle the arm -
  // the panel may say the batch is untouched only where the server refused to act on it.
  undoFailure: Error | { status: number; body: string } | null;
  // Refuses every summary read from the undo onward, which is the read the undo itself starts.
  failSummaryAfterUndo: boolean;
  undone: boolean;
  // How many summary reads the panel has made, so a test can tell a cached answer from a re-read.
  summaryReads: number;
  // What POST /undo answers on the arm that is not scripted to fail, and the batch every summary
  // read answers with before and after it. All three are the server's own account, written out
  // rather than worked out from each other.
  undoResult: UndoResult;
  before: Batch;
  after: Batch;
}

const server = vi.hoisted((): ServerScript => ({
  undoFailure: null,
  failSummaryAfterUndo: false,
  undone: false,
  summaryReads: 0,
  undoResult: {
    undone: 4,
    failedCount: 0,
    failedSample: [],
    skippedCount: 0,
    skippedSample: [],
    warningCount: 0,
    warningSample: [],
  },
  before: { count: 4, remainingCount: 4, unrestorableCount: 0 },
  after: { count: 4, remainingCount: 0, unrestorableCount: 0 },
}));

vi.mock("@cove/runtime/api", () => ({
  extensionFetch: (_path: string, init: RequestInit) => {
    if (String(init.method).toUpperCase() !== "POST") {
      server.summaryReads += 1;
      if (server.failSummaryAfterUndo && server.undone) {
        return jsonResponse({ detail: "the summary could not be read" }, 503);
      }
      const batch = server.undone ? server.after : server.before;
      // Written just now, so the panel offers the button rather than an expired line.
      return jsonResponse({
        hasBatch: true,
        ...batch,
        writtenAtUtcTicks: Date.now() * 10000 + 621355968000000000,
        // A week out, as the server states it: the panel prints this and derives nothing.
        expiresAtUtcTicks: (Date.now() + 604_800_000) * 10000 + 621355968000000000,
        consumed: batch.remainingCount === 0,
      } satisfies LastBatchSummary);
    }

    server.undone = true;
    if (server.undoFailure instanceof Error) return Promise.reject(server.undoFailure);
    if (server.undoFailure) return jsonResponse(server.undoFailure.body, server.undoFailure.status);
    return jsonResponse(server.undoResult);
  },
}));

const { UndoSection } = await import("./UndoSection");

// Every arm under test ends on one of these sentences.
const VERDICT = /Undone: |Undo finished with problems|Couldn't confirm the undo|Couldn't undo/;

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
  server.summaryReads = 0;
  server.undoResult = {
    undone: 4,
    failedCount: 0,
    failedSample: [],
    skippedCount: 0,
    skippedSample: [],
    warningCount: 0,
    warningSample: [],
  };
  server.before = { count: 4, remainingCount: 4, unrestorableCount: 0 };
  server.after = { count: 4, remainingCount: 0, unrestorableCount: 0 };
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

test("a clean undo still reports the restore it performed, and leaves nothing to undo", async () => {
  const text = await undoAndReadFeedback();

  expect(text).not.toMatch(/couldn't/i);
  expect(text).toContain("Undone: 4 files moved back to their original names.");
  // The line beside the verdict is the re-read summary, not the one the panel opened with.
  expect(text).toContain("No rename to undo.");
  expect(screen.queryByRole("button", { name: /Undo last rename/ })).toBeNull();
});

test("a partial undo leaves the line offering only what is still to restore", async () => {
  server.undoResult = {
    undone: 3,
    failedCount: 1,
    failedSample: [
      {
        fileId: 7,
        oldPath: "/library/old.mp4",
        newPath: "/library/new.mp4",
        reason: "the file is open in another program",
      },
    ],
    skippedCount: 0,
    skippedSample: [],
    warningCount: 0,
    warningSample: [],
  };
  server.after = { count: 4, remainingCount: 1, unrestorableCount: 0 };

  const text = await undoAndReadFeedback();

  expect(text).toContain("Undo finished with problems");
  expect(text).toContain("3 of 4 restored · 1 remaining");
  // The row still offers the button, and its confirm quotes what is left rather than the four the
  // batch started as.
  await userEvent.setup().click(screen.getByRole("button", { name: /Undo last rename/ }));
  expect(await screen.findByRole("button", { name: "Undo 1 rename" })).toBeTruthy();
});

test("the line is re-read when the footer mounts, not served from the cache", async () => {
  // A rename started anywhere else - "Rename selected" from a library page, or auto-rename in the
  // host - reaches no QueryClient, so a footer that trusted the host's freshness window would open
  // on the batch from before it. One client across both mounts, standing in for the host's.
  const client = createTestQueryClient();

  const first = render(withQueryClient(createElement(UndoSection), client));
  await screen.findByRole("button", { name: /Undo last rename/ });
  expect(server.summaryReads).toBe(1);

  first.unmount();
  server.before = { count: 9, remainingCount: 9, unrestorableCount: 0 };

  render(withQueryClient(createElement(UndoSection), client));

  await waitFor(() => {
    expect(screen.getByText(/9 items renamed/)).toBeTruthy();
  });
  expect(server.summaryReads).toBe(2);
});

test("an undo whose summary re-read fails still reports what the undo did", async () => {
  server.failSummaryAfterUndo = true;
  const text = await undoAndReadFeedback();

  expect(text).toContain("Undone: 4 files moved back to their original names.");
  expect(text).toContain("Couldn't check for a recent rename");
});
