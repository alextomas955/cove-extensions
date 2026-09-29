// @vitest-environment jsdom
// What the preview pane shows after a preview request failed. The hook keeps the last good preview on
// a failed refresh, so a failed first request leaves `preview` null with the error set: a settled
// outcome, not a wait.
import { test, expect } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { LivePreviewPane } from "./LivePreviewPane";
import type { PreviewSampleResult } from "../wire/api";

const SAMPLE: PreviewSampleResult[] = [
  {
    sampleLabel: "Standard",
    oldName: "raw.mkv",
    newName: "Sorted Name.mkv",
    folder: "",
    flags: [],
    droppedFields: [],
  },
];

async function renderPane(preview: PreviewSampleResult[] | null, previewError: boolean) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(LivePreviewPane, { preview, previewError }));
  await waitFor("the pane to render", () => container.childElementCount > 0);

  const text = container.textContent;
  root.unmount();
  container.remove();
  return {
    text,
    // The spinner and its caption render together, so the caption stands for both.
    waiting: /rendering preview/i.test(text),
    showsRows: text.includes("Sorted Name.mkv"),
  };
}

test("a failed first preview says so and does not leave a spinner running under the error", async () => {
  // Nothing further is in flight, so a spinner here would promise a render that never arrives.
  const pane = await renderPane(null, true);

  expect(pane.waiting).toBe(false);
  expect(pane.text).toMatch(/preview unavailable/i);
});

test("a preview still on its way keeps the spinner", async () => {
  // No failure reported yet and no rows: the request genuinely is in flight, which is the one state
  // the spinner is for.
  const pane = await renderPane(null, false);

  expect(pane.waiting).toBe(true);
});

test("a failed refresh over a good preview keeps the rows and drops the spinner", async () => {
  const pane = await renderPane(SAMPLE, true);

  expect(pane.showsRows).toBe(true);
  expect(pane.waiting).toBe(false);
  expect(pane.text).toMatch(/preview unavailable/i);
});

test("a healthy preview shows its rows and nothing else", async () => {
  const pane = await renderPane(SAMPLE, false);

  expect(pane.showsRows).toBe(true);
  expect(pane.waiting).toBe(false);
  expect(pane.text).not.toMatch(/preview unavailable/i);
});
