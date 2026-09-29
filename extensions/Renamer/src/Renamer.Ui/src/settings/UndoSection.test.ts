// @vitest-environment jsdom
// What the panel says after an undo whose outcome nobody knows. `/undo` cannot be repeated, so a
// transport failure, where the server may have restored all, part or none of the batch, must never
// read as a confident success. The request module is the one stand-in.
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";
import type { LastBatchSummary, UndoResult } from "../wire/api";

const server = vi.hoisted(() => ({
  // Rejection handed to the undo POST, or null to answer it with a clean full restore.
  undoRejection: null as Error | null,
}));

class FakeApiError extends Error {
  constructor(
    readonly status: number,
    readonly body: string,
  ) {
    super(`${status} ${body}`);
  }
}

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: FakeApiError,
  errorText: (err: unknown) => (err instanceof Error ? err.message : String(err)),
  requestJson: (_path: string, options?: { method?: string }) => {
    if (options?.method !== "POST") {
      // Written just now, so the panel offers the button rather than an expired line.
      return Promise.resolve({
        hasBatch: true,
        count: 4,
        remainingCount: 4,
        unrestorableCount: 0,
        writtenAtUtcTicks: Date.now() * 10000 + 621355968000000000,
        consumed: false,
      } satisfies LastBatchSummary);
    }
    return server.undoRejection === null
      ? Promise.resolve({
          undone: 4,
          failedCount: 0,
          failedSample: [],
          skippedCount: 0,
          skippedSample: [],
          warningCount: 0,
          warningSample: [],
        } satisfies UndoResult)
      : Promise.reject(server.undoRejection);
  },
}));

const { UndoSection } = await import("./UndoSection");

// Every arm under test ends on one of these sentences.
const VERDICT = /Undone: |Couldn't confirm the undo|Couldn't undo/;

// Mount the section, run the undo to its verdict, and hand back the text a user would read.
async function undoAndReadFeedback(): Promise<string> {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(UndoSection, { refreshKey: 0 }));

  const button = (matches: (text: string) => boolean) =>
    [...container.querySelectorAll("button")].find((b) => matches(b.textContent));

  const opensTheDialog = (text: string) => text.includes("Undo last rename");
  await waitFor("the undo button to render", () => button(opensTheDialog) !== undefined);
  button(opensTheDialog)?.click();

  const confirms = (text: string) => /^Undo \d+ rename/.test(text);
  await waitFor("the confirm button to render", () => button(confirms) !== undefined);
  button(confirms)?.click();

  await waitFor("the undo to reach a verdict", () => VERDICT.test(container.textContent));

  const text = container.textContent;
  root.unmount();
  container.remove();
  return text;
}

beforeEach(() => {
  server.undoRejection = null;
  document.body.replaceChildren();
});

// `requestJson` raises its own ApiError for an empty body, so any other rejection is a request whose
// fate is unknown: the connection dropped, or the body would not parse. The server may already have
// moved part or all of the batch back.
test.each([
  ["a dropped connection", new TypeError("Failed to fetch")],
  ["a malformed body", new SyntaxError("Unexpected token < in JSON at position 0")],
])("an undo lost to %s is not reported as a completed undo", async (_case, rejection) => {
  server.undoRejection = rejection;
  const text = await undoAndReadFeedback();

  expect(text).not.toContain("Undone: ");
  expect(text).toMatch(/couldn't confirm/i);
  // Not "nothing was changed": that would say there is nothing left to re-check, which is the one
  // claim this arm cannot make.
  expect(text).not.toMatch(/nothing was changed/i);
  expect(text).toMatch(/check the batch/i);
});

test("a real ApiError still reads as a failure that changed nothing", async () => {
  // The server answered that it refused, so the batch is untouched and saying so is correct here.
  server.undoRejection = new FakeApiError(403, "forbidden");
  const text = await undoAndReadFeedback();

  expect(text).toMatch(/couldn't undo/i);
  expect(text).toMatch(/nothing was changed/i);
});

test("a clean undo still reports the restore it performed", async () => {
  const text = await undoAndReadFeedback();

  expect(text).not.toMatch(/couldn't/i);
  expect(text).toContain("Undone: 4 files moved back to their original names.");
});
