// @vitest-environment jsdom
/**
 * What the panel says after an undo whose outcome nobody knows.
 *
 * `/undo` moves files back and cannot be repeated, so the sentence that closes it is the user's only
 * signal about whether to go and check. A transport failure leaves the request's fate unknown — the
 * server may have restored the whole batch, part of it, or none — and the one reading that must not be
 * available is a confident success.
 *
 * A DOM is needed because the subject is the hook's catch arm, and which sentence it picks is
 * observable only once React has committed. React arrives as its PRODUCTION build (the bundle's
 * `process.env.NODE_ENV` define applies here too), which has no `act`, so renders are flushed by
 * waiting rather than by wrapping.
 *
 * The stubs are the host seams and never the subject: the request module, whose real one reaches
 * `@cove/runtime/api`; the shared primitives, whose `react` import resolves only inside a consuming
 * bundle; and the dialog shell, which contributes only the confirm button this test presses.
 */
import { test, expect, vi, beforeEach } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

const server = vi.hoisted(() => ({
  /** Rejection handed to the undo POST, or null to answer it with a clean full restore. */
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
  requestJson: (_path: string, options?: { method?: string }) => {
    if (options?.method !== "POST") {
      // An open batch written just now, so the panel offers the button rather than an expired line.
      // Field names and the tick epoch are transcribed by hand from `LastBatchSummary`; a wrong one
      // would leave the button unrendered and read as the fix having no effect.
      return Promise.resolve({
        hasBatch: true,
        count: 4,
        remainingCount: 4,
        unrestorableCount: 0,
        writtenAtUtcTicks: Date.now() * 10000 + 621355968000000000,
        consumed: false,
      });
    }
    return server.undoRejection === null
      ? // A clean full restore. Every field of `UndoResult` is present and transcribed by hand: a
        // missing one throws inside the feedback builder, which would land in the very catch arm
        // under test and make the control case look like the defect.
        Promise.resolve({
          undone: 4,
          failedCount: 0,
          failedSample: [],
          skippedCount: 0,
          skippedSample: [],
          warningCount: 0,
          warningSample: [],
        })
      : Promise.reject(server.undoRejection);
  },
}));

vi.mock("lucide-react", () => ({ Undo2: () => null }));

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    // The REAL route builder, re-exported rather than restated: a stand-in path shape here could
    // drift from the one the section actually calls.
    extensionApi: (await import("../../../../../../shared/ui-shared/src/actions")).extensionApi,
    Button: (props: { children?: ReactNode; onClick?: () => void }) =>
      h("button", { onClick: props.onClick }, props.children),
    StatusText: (props: { children?: ReactNode; kind?: string }) =>
      h("div", { "data-status": props.kind }, props.children),
    Spinner: () => null,
  };
});

vi.mock("../common/ui/Dialog", async () => {
  const { createElement: h } = await import("react");
  return { Dialog: (props: { children?: ReactNode }) => h("div", null, props.children) };
});

const { UndoSection } = await import("./UndoSection");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

/** Mount the section, run the undo to its verdict, and hand back the text a user would read. */
async function undoAndReadFeedback(): Promise<string> {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(UndoSection, { refreshKey: 0 }));
  await sleep(COMMIT_MS);

  const open = [...container.querySelectorAll("button")].find((b) =>
    b.textContent.includes("Undo last rename"),
  );
  expect(open, "the undo button never rendered").toBeDefined();
  open?.click();
  await sleep(COMMIT_MS);

  const confirm = [...container.querySelectorAll("button")].find((b) =>
    /^Undo \d+ rename/.test(b.textContent),
  );
  expect(confirm, "the confirm button never rendered").toBeDefined();
  confirm?.click();
  await sleep(COMMIT_MS * 4);

  const text = container.textContent;
  root.unmount();
  container.remove();
  return text;
}

beforeEach(() => {
  server.undoRejection = null;
  document.body.replaceChildren();
});

test("an undo whose response never arrived is not reported as a completed undo", async () => {
  // THE case. `requestJson` raises its own ApiError for an empty body, so a non-ApiError rejection is
  // a request whose fate is unknown: the connection dropped, or the body would not parse. The server
  // may already have moved part or all of the batch back.
  server.undoRejection = new TypeError("Failed to fetch");
  const text = await undoAndReadFeedback();

  expect(text).not.toMatch(/your files were moved back/i);
  expect(text).toMatch(/couldn't confirm/i);
});

test("that sentence tells the user the batch still needs checking", async () => {
  server.undoRejection = new TypeError("Failed to fetch");
  const text = await undoAndReadFeedback();

  // Not "nothing was changed": that would say there is nothing left to re-check, which is the one
  // claim this arm cannot make.
  expect(text).not.toMatch(/nothing was changed/i);
  expect(text).toMatch(/check the batch/i);
});

test("a malformed body is the same unknown, not a success", async () => {
  server.undoRejection = new SyntaxError("Unexpected token < in JSON at position 0");
  const text = await undoAndReadFeedback();

  expect(text).not.toMatch(/your files were moved back/i);
  expect(text).toMatch(/couldn't confirm/i);
});

test("a real ApiError still reads as a failure that changed nothing", async () => {
  // The server answered, and it answered that it refused — so the batch is untouched and saying so is
  // correct here. Pinned so the fix above cannot swallow this arm into the unknown one.
  server.undoRejection = new FakeApiError(403, "forbidden");
  const text = await undoAndReadFeedback();

  expect(text).toMatch(/couldn't undo/i);
  expect(text).toMatch(/nothing was changed/i);
});

test("a clean undo still reports the restore it performed", async () => {
  const text = await undoAndReadFeedback();

  expect(text).not.toMatch(/couldn't/i);
  expect(text).toMatch(/4/);
});
