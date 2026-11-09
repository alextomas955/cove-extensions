// @vitest-environment jsdom
// What the preview pane shows after a preview request failed. The hook keeps the last good preview on
// a failed refresh, so a failed first request leaves `preview` null with the error set: a settled
// outcome, not a wait.
import { test, expect, afterEach } from "vitest";
import { createElement } from "react";
import { render, cleanup } from "@testing-library/react";

import { LivePreviewPane } from "./LivePreviewPane";
import type { PreviewSampleResult } from "../wire";

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

afterEach(cleanup);

function renderPane(preview: PreviewSampleResult[] | null, previewError: boolean) {
  const text = render(createElement(LivePreviewPane, { preview, previewError })).container
    .textContent;
  return {
    text,
    // The spinner and its caption render together, so the caption stands for both.
    waiting: /rendering preview/i.test(text),
    showsRows: text.includes("Sorted Name.mkv"),
  };
}

test("a failed first preview says so and does not leave a spinner running under the error", () => {
  // Nothing further is in flight, so a spinner here would promise a render that never arrives.
  const pane = renderPane(null, true);

  expect(pane.waiting).toBe(false);
  expect(pane.text).toMatch(/preview unavailable/i);
});

test("a preview still on its way keeps the spinner", () => {
  // No failure reported yet and no rows: the request genuinely is in flight, which is the one state
  // the spinner is for.
  const pane = renderPane(null, false);

  expect(pane.waiting).toBe(true);
});

test("a failed refresh over a good preview keeps the rows and drops the spinner", () => {
  const pane = renderPane(SAMPLE, true);

  expect(pane.showsRows).toBe(true);
  expect(pane.waiting).toBe(false);
  expect(pane.text).toMatch(/preview unavailable/i);
});

test("a healthy preview shows its rows and nothing else", () => {
  const pane = renderPane(SAMPLE, false);

  expect(pane.showsRows).toBe(true);
  expect(pane.waiting).toBe(false);
  expect(pane.text).not.toMatch(/preview unavailable/i);
});
