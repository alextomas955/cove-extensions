// @vitest-environment jsdom
/**
 * What the preview pane shows once a preview request has failed and nothing further is in flight.
 *
 * The hook keeps the last good preview on a failed refresh and raises `previewError`, so after the
 * FIRST request fails there is no preview to keep: `preview` stays null and the error flag is set. That
 * pair is a settled outcome, not a wait — the pane must not also claim a render is still coming.
 *
 * A DOM is needed because the claim is about what is on screen. React arrives as its PRODUCTION build
 * (the bundle's `process.env.NODE_ENV` define applies here too), which has no `act`, so the render is
 * flushed by waiting rather than by wrapping.
 *
 * The shared primitives stand in, because their `react` import resolves only inside a consuming
 * bundle: that package deliberately has no node_modules of its own. Each stand-in marks itself so the
 * assertions read this pane's own output.
 */
import { test, expect, vi } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import type { PreviewSampleResult } from "../wire/api";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    Spinner: () => h("span", { "data-stub": "Spinner" }, "SPINNER"),
    StatusText: (props: { children?: unknown; kind?: string }) =>
      h("div", { "data-stub": "StatusText", "data-kind": props.kind }, props.children as string),
  };
});

vi.mock("./PreviewCard", async () => {
  const { createElement: h } = await import("react");
  return {
    PreviewCard: (props: { result: { sampleLabel: string } }) =>
      h("div", { "data-stub": "PreviewCard" }, props.result.sampleLabel),
  };
});

const { LivePreviewPane } = await import("./LivePreviewPane");

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

async function renderPane(preview: PreviewSampleResult[] | null, previewError: boolean) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(LivePreviewPane, { preview, previewError }));
  await sleep(COMMIT_MS);

  return {
    text: container.textContent,
    spinners: container.querySelectorAll('[data-stub="Spinner"]').length,
    cards: container.querySelectorAll('[data-stub="PreviewCard"]').length,
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

const SAMPLE = [{ sampleLabel: "Standard" }] as unknown as PreviewSampleResult[];

test("a failed first preview does not leave a spinner running under the error", async () => {
  // THE case. Nothing further is in flight, so a spinner here promises a render that will never
  // arrive and the pane never resolves for the rest of the session.
  const pane = await renderPane(null, true);

  expect(pane.spinners).toBe(0);
  expect(pane.text).not.toMatch(/rendering preview/i);
  pane.teardown();
});

test("that failure is still stated, so the pane is not silently empty", async () => {
  const pane = await renderPane(null, true);

  expect(pane.text).toMatch(/preview unavailable/i);
  pane.teardown();
});

test("a preview still on its way keeps the spinner", async () => {
  // No failure reported yet and no rows: the request genuinely is in flight, which is the one state
  // the spinner is for.
  const pane = await renderPane(null, false);

  expect(pane.spinners).toBe(1);
  expect(pane.text).toMatch(/rendering preview/i);
  pane.teardown();
});

test("a failed refresh over a good preview keeps the rows and drops the spinner", async () => {
  // The hook holds the last good preview on a failed refresh, so both are set. The rows are what the
  // user reads; the line above says the refresh failed.
  const pane = await renderPane(SAMPLE, true);

  expect(pane.cards).toBe(1);
  expect(pane.spinners).toBe(0);
  expect(pane.text).toMatch(/preview unavailable/i);
  pane.teardown();
});

test("a healthy preview shows its rows and nothing else", async () => {
  const pane = await renderPane(SAMPLE, false);

  expect(pane.cards).toBe(1);
  expect(pane.spinners).toBe(0);
  expect(pane.text).not.toMatch(/preview unavailable/i);
  pane.teardown();
});
