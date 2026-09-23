// @vitest-environment jsdom
// The pane shows the preview for the options the user is on when two requests overlap. The request
// mock hands each call's resolver back to the test, so the test settles them in reverse issue order,
// which the debounce cannot prevent.
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { waitFor } from "../common/lib/flushRender";

import { useRenamePreview, type UseRenamePreview } from "./useRenamePreview";
import { type RenamerOptions } from "./options";
import { someOptions } from "./testOptions";
import type { PreviewSampleResult } from "../wire/api";

// Every POST the hook issued, each holding its own settle handles.
const host = vi.hoisted(() => ({
  calls: [] as {
    aborted: () => boolean;
    resolve: (rows: unknown) => void;
    reject: (err: unknown) => void;
    handled: () => Promise<void>;
  }[],
  noop: () => undefined,
}));
const { noop } = host;

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  errorText: (err: unknown) => String(err),
  requestJson: (_path: string, init: RequestInit) => {
    let settle: { resolve: (rows: unknown) => void; reject: (err: unknown) => void };
    const promise = new Promise((resolve, reject) => {
      settle = { resolve, reject };
    });
    host.calls.push({
      aborted: () => init.signal?.aborted === true,
      resolve: (rows) => {
        settle.resolve(rows);
      },
      reject: (err) => {
        settle.reject(err);
      },
      // The hook registered its handler on this promise first, so a continuation added here runs
      // after it. That is what lets a test read a decision the hook made and then discarded, which
      // changes nothing on screen and so offers nothing to wait for.
      handled: () => promise.then(noop, noop),
    });
    return promise;
  },
}));

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

// Past the hook's 250ms debounce. A duration, not a condition: the thing being waited for here is
// real elapsed time, because a debounce is a timer and nothing renders while it runs.
const PAST_DEBOUNCE_MS = 400;

function sample(label: string): PreviewSampleResult[] {
  return [
    {
      sampleLabel: label,
      oldName: "raw.mkv",
      newName: `${label}.mkv`,
      folder: "Sorted",
      flags: [],
      droppedFields: [],
    },
  ];
}

// Mount the hook and hand back its latest return value, a way to change its options, and a teardown.
function mountHook(initial: RenamerOptions) {
  let latest: UseRenamePreview | null = null;
  function Probe({ options }: { options: RenamerOptions }) {
    latest = useRenamePreview(options, false);
    return null;
  }

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(Probe, { options: initial }));

  return {
    get current(): UseRenamePreview {
      expect(latest, "the probe never rendered").not.toBeNull();
      return latest as unknown as UseRenamePreview;
    },
    retarget: (options: RenamerOptions) => {
      root.render(createElement(Probe, { options }));
    },
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

beforeEach(() => {
  host.calls.length = 0;
});

test("an older preview response cannot repaint the pane over a newer one", async () => {
  const first = { ...someOptions(), filenameTemplate: "$title" };
  const second = { ...someOptions(), filenameTemplate: "$title - $studio" };

  const hook = mountHook(first);
  await sleep(PAST_DEBOUNCE_MS);
  expect(host.calls.length, "the first POST was never issued").toBe(1);

  // A second edit after the first POST is already in flight. Clearing the debounce timer can no longer
  // recall it, so both requests are open at once.
  hook.retarget(second);
  await sleep(PAST_DEBOUNCE_MS);
  expect(host.calls.length, "the second POST was never issued").toBe(2);

  host.calls[1].resolve(sample("second"));
  await waitFor("the newer response to paint", () => hook.current.preview !== null);
  expect(hook.current.preview?.[0].sampleLabel).toBe("second");

  // The older request answers last, which is the ordering the debounce cannot prevent. A response
  // the hook discards paints nothing, so what is waited for is the hook's handler having run.
  host.calls[0].resolve(sample("first"));
  await host.calls[0].handled();

  expect(hook.current.preview?.[0].sampleLabel, "the superseded response repainted the pane").toBe(
    "second",
  );
  expect(hook.current.previewError).toBe(false);

  hook.unmount();
}, 30_000);

test("superseding a request aborts it, and that abort is not reported as a failure", async () => {
  const first = { ...someOptions(), filenameTemplate: "$title" };
  const second = { ...someOptions(), filenameTemplate: "$title - $studio" };

  const hook = mountHook(first);
  await sleep(PAST_DEBOUNCE_MS);
  hook.retarget(second);
  await sleep(PAST_DEBOUNCE_MS);
  expect(host.calls).toHaveLength(2);

  expect(host.calls[0].aborted(), "the superseded request was not aborted").toBe(true);
  expect(host.calls[1].aborted()).toBe(false);

  // The host's fetch rejects an aborted request. Reporting that would be an error the hook caused
  // itself, while the request the user is waiting on is still on its way.
  host.calls[0].reject(new Error("aborted"));
  await host.calls[0].handled();
  expect(hook.current.previewError, "an abort was surfaced as a preview failure").toBe(false);

  host.calls[1].resolve(sample("second"));
  await waitFor("the surviving response to paint", () => hook.current.preview !== null);
  expect(hook.current.preview?.[0].sampleLabel).toBe("second");
  expect(hook.current.previewError).toBe(false);

  hook.unmount();
}, 30_000);
