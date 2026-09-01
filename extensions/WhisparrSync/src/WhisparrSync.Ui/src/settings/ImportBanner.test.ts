// @vitest-environment jsdom
/**
 * What the banner puts on the screen: nothing at all when there is nothing to report, one line per
 * root when there is, at most three paths under each, a readable heading for the line no root was
 * reported for, and the sentence for the files the catch-up passed over — which either half can
 * raise on its own.
 *
 * Absence is asserted as ELEMENT NOT PRESENT rather than as element empty: an empty red block still
 * occupies the screen and still reads as a problem in force.
 *
 * React arrives as its PRODUCTION build, which has no `act`, so a render is flushed by waiting. The
 * shared primitives stand in, because their `react` import resolves only inside a consuming bundle.
 */
import { expect, test, vi } from "vitest";
import { createElement, type ReactNode } from "react";
import { createRoot } from "react-dom/client";

import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import type { ImportBannerRootLine, ImportBannerView } from "../wire/api";
import {
  IMPORT_CAUSE_AMBIGUOUS,
  IMPORT_CAUSE_NOT_FOUND,
  IMPORT_CAUSE_UNREADABLE,
  importRefusalsWithNoReportedRootSentence,
  IMPORTS_UNREADABLE,
} from "../common/ui/copy";

vi.mock("@cove-extensions/ui-shared", async () => {
  const { createElement: h } = await import("react");
  return {
    Spinner: () => h("span", null, "…"),
    StatusText: (props: { children?: ReactNode }) => h("span", null, props.children),
  };
});

const { ImportBanner } = await import("./ImportBanner");

const ANSWERED: AsyncRead = { reading: false, failed: false, hasContent: true };
const STILL_READING: AsyncRead = { reading: true, failed: false, hasContent: false };

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

function lineFor(root: string, count: number, paths: number): ImportBannerRootLine {
  return {
    root,
    countSinceLastSuccess: count,
    newestPaths: Array.from({ length: paths }, (_, index) => ({
      path: `${root}/${String(index)}.mp4`,
      cause: "notFoundUnderAnyRoot" as const,
    })),
  };
}

function viewOf(...roots: ImportBannerRootLine[]): ImportBannerView {
  return { roots, recordsContained: 0, lastContainedAtUtc: null };
}

/** The instant the recorded ages below are measured against. */
const NOW_MS = Date.parse("2026-08-31T09:00:00Z");

async function render(read: AsyncRead, view: ImportBannerView | null) {
  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(ImportBanner, { read, view, now: NOW_MS }));
  await sleep(COMMIT_MS);
  return {
    blocks: container.querySelectorAll('[role="alert"]'),
    rootLines: container.querySelectorAll('[role="alert"] > ul > li'),
    text: container.textContent,
    pathsUnder: (index: number) =>
      container.querySelectorAll(`[role="alert"] > ul > li:nth-child(${String(index + 1)}) ul li`),
    teardown: () => {
      root.unmount();
      container.remove();
    },
  };
}

test("an answer with no roots puts nothing on the screen", async () => {
  const view = await render(ANSWERED, viewOf());

  expect(view.blocks.length).toBe(0);
  expect(view.text).toBe("");
  view.teardown();
});

test("a read that has not answered yet puts nothing on the screen", async () => {
  const view = await render(STILL_READING, null);

  expect(view.blocks.length).toBe(0);
  expect(view.text).toBe("");
  view.teardown();
});

test("two roots produce ONE block holding one line each", async () => {
  const view = await render(
    ANSWERED,
    viewOf(lineFor("/whisparr-media", 4, 2), lineFor("/whisparr-elsewhere", 1, 1)),
  );

  expect(view.blocks.length).toBe(1);
  expect(view.rootLines.length).toBe(2);
  expect(view.text).toContain(IMPORTS_UNREADABLE);
  expect(view.text).toContain("/whisparr-media");
  expect(view.text).toContain("/whisparr-elsewhere");
  view.teardown();
});

test("a root with more recorded paths than the bound still lists three", async () => {
  const view = await render(ANSWERED, viewOf(lineFor("/whisparr-media", 9, 6)));

  expect(view.pathsUnder(0).length).toBe(3);
  expect(view.text).toContain("/whisparr-media/0.mp4");
  expect(view.text).not.toContain("/whisparr-media/3.mp4");
  view.teardown();
});

test("each path names its own cause", async () => {
  const view = await render(
    ANSWERED,
    viewOf({
      root: "/whisparr-media",
      countSinceLastSuccess: 3,
      newestPaths: [
        { path: "/whisparr-media/a.mp4", cause: "notFoundUnderAnyRoot" },
        { path: "/whisparr-media/b.mp4", cause: "ambiguousCandidates" },
        { path: "/whisparr-media/c.mp4", cause: "unreadable" },
      ],
    }),
  );

  expect(view.text).toContain(IMPORT_CAUSE_NOT_FOUND);
  expect(view.text).toContain(IMPORT_CAUSE_AMBIGUOUS);
  expect(view.text).toContain(IMPORT_CAUSE_UNREADABLE);
  view.teardown();
});

test("the line no root was reported for reads as a sentence rather than a gap", async () => {
  const view = await render(ANSWERED, viewOf(lineFor("", 5, 1)));

  expect(view.rootLines.length).toBe(1);
  const heading = view.rootLines[0].querySelector("p")?.textContent ?? "";
  expect(heading).toBe(importRefusalsWithNoReportedRootSentence(5));
  // The blank key reaching the reader as itself leaves a hole where a folder should be.
  expect(heading).not.toContain("  ");
  view.teardown();
});

test("the count is the stored integer rather than the number of paths listed", async () => {
  const view = await render(ANSWERED, viewOf(lineFor("/whisparr-media", 412, 3)));

  expect(view.text).toContain("412");
  view.teardown();
});

test("a containment with no root refused at all still puts a block on the screen", async () => {
  const view = await render(ANSWERED, {
    roots: [],
    recordsContained: 3,
    lastContainedAtUtc: "2026-08-31T08:30:00Z",
  });

  expect(view.blocks.length).toBe(1);
  expect(view.rootLines.length).toBe(0);
  expect(view.text).toContain("3 files");
  expect(view.text).toContain("30 min ago");
  // The heading belongs to the root list, and there is no root list here.
  expect(view.text).not.toContain(IMPORTS_UNREADABLE);
  view.teardown();
});

test("a containment and a refused root share ONE block", async () => {
  const view = await render(ANSWERED, {
    roots: [lineFor("/whisparr-media", 4, 1)],
    recordsContained: 2,
    lastContainedAtUtc: null,
  });

  expect(view.blocks.length).toBe(1);
  expect(view.rootLines.length).toBe(1);
  expect(view.text).toContain(IMPORTS_UNREADABLE);
  expect(view.text).toContain("2 files");
  view.teardown();
});
