// @vitest-environment jsdom
// What the preview pane shows after a preview request failed. The hook keeps the last good preview on
// a failed refresh, so a failed first request leaves `preview` null with the error set: a settled
// outcome, not a wait.
import { test, expect, vi } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { LivePreviewPane } from "./LivePreviewPane";
import type { PreviewSampleResult } from "../wire/api";

vi.mock("./PreviewCard", async () => {
  const { createElement: h } = await import("react");
  return {
    PreviewCard: (props: { result: { sampleLabel: string } }) =>
      h("div", { "data-stub": "PreviewCard" }, props.result.sampleLabel),
  };
});

async function renderPane(preview: PreviewSampleResult[] | null, previewError: boolean) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(LivePreviewPane, { preview, previewError }));
  await waitFor("the pane to render", () => container.childElementCount > 0);

  return {
    text: container.textContent,
    spinners: container.querySelectorAll(".animate-spin").length,
    cards: container.querySelectorAll('[data-stub="PreviewCard"]').length,
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

const SAMPLE = [{ sampleLabel: "Standard" }] as unknown as PreviewSampleResult[];

test("a failed first preview does not leave a spinner running under the error", async () => {
  // Nothing further is in flight, so a spinner here would promise a render that never arrives.
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
