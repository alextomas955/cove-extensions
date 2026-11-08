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
  // How the undo POST ends: an Error the request fails with before any reply, a status the server
  // answers with, or null for a clean full restore. The two failures are different arms - only a
  // status means the server was reached and said no.
  undoFailure: null as Error | { status: number; body: string } | null,
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
});

afterEach(cleanup);

// A request that failed before any reply has an unknown fate: the connection dropped, or the body
// would not parse. The server may already have moved part or all of the batch back.
test.each([
  ["a dropped connection", new TypeError("Failed to fetch")],
  ["a malformed body", new SyntaxError("Unexpected token < in JSON at position 0")],
])("an undo lost to %s is not reported as a completed undo", async (_case, rejection) => {
  server.undoFailure = rejection;
  const text = await undoAndReadFeedback();

  expect(text).not.toContain("Undone: ");
  expect(text).toMatch(/couldn't confirm/i);
  // Not "nothing was changed": that would say there is nothing left to re-check, which is the one
  // claim this arm cannot make.
  expect(text).not.toMatch(/nothing was changed/i);
  expect(text).toMatch(/check the batch/i);
});

test("a refusal the server answered still reads as a failure that changed nothing", async () => {
  // The server answered that it refused, so the batch is untouched and saying so is correct here.
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
