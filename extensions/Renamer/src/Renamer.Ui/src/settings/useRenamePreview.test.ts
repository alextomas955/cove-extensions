// @vitest-environment jsdom
/**
 * That the pane shows the preview for the options the user is ON, when two requests overlap.
 *
 * The pure decision has its own suite, and a green one there proves nothing on its own - a hook that
 * never consults it repaints from whichever request answers last however correct the decision is. So
 * this renders the REAL hook and holds two POSTs open at once, then settles them in reverse issue
 * order, which is the ordering the debounce cannot prevent.
 *
 * One seam is stubbed, and it is not the subject: the host request helper, because it reaches
 * `@cove/runtime/api`, which exists only inside Cove. Its stand-in hands each call's resolver back to
 * the test so settle order is the test's to choose. React arrives as its PRODUCTION build (the
 * bundle's `process.env.NODE_ENV` define applies here too), which has no `act`, so renders are flushed
 * by waiting rather than by wrapping.
 */
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { useRenamePreview, type UseRenamePreview } from "./useRenamePreview";
import { cloneDefaults, type RenamerOptions } from "./options";
import type { PreviewSampleResult } from "../wire/api";

/** Every POST the hook issued, each holding its own settle handles. */
const host = vi.hoisted(() => ({
  calls: [] as {
    aborted: () => boolean;
    resolve: (rows: unknown) => void;
    reject: (err: unknown) => void;
  }[],
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  requestJson: (_path: string, init: RequestInit) =>
    new Promise((resolve, reject) => {
      host.calls.push({
        aborted: () => init.signal?.aborted === true,
        resolve,
        reject,
      });
    }),
}));

// The shared barrel re-exports the React primitives, whose `react`/`lucide-react` imports resolve only
// inside a consuming bundle. This hook reaches the barrel for one route builder, so the stand-in
// re-exports the REAL one from the pure module that defines it.
vi.mock("@cove-extensions/ui-shared", async () => ({
  extensionApi: (await import("../../../../../../shared/ui-shared/src/actions")).extensionApi,
}));

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Past the hook's 250ms debounce, with room for React to commit on the default lane without `act`. */
const PAST_DEBOUNCE_MS = 400;

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

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

/** Mount the hook and hand back its latest return value, a way to change its options, and a teardown. */
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
  const first = { ...cloneDefaults(), filenameTemplate: "$title" };
  const second = { ...cloneDefaults(), filenameTemplate: "$title - $studio" };

  const hook = mountHook(first);
  await sleep(PAST_DEBOUNCE_MS);
  expect(host.calls.length, "the first POST was never issued").toBe(1);

  // A second edit AFTER the first POST is already in flight. Clearing the debounce timer can no longer
  // recall it, so both requests are open at once.
  hook.retarget(second);
  await sleep(PAST_DEBOUNCE_MS);
  expect(host.calls.length, "the second POST was never issued").toBe(2);

  host.calls[1].resolve(sample("second"));
  await sleep(COMMIT_MS);
  expect(hook.current.preview?.[0].sampleLabel).toBe("second");

  // The older request answers LAST, which is the ordering the debounce cannot prevent.
  host.calls[0].resolve(sample("first"));
  await sleep(COMMIT_MS);

  expect(hook.current.preview?.[0].sampleLabel, "the superseded response repainted the pane").toBe(
    "second",
  );
  expect(hook.current.previewError).toBe(false);

  hook.unmount();
}, 30_000);

test("superseding a request aborts it, and that abort is not reported as a failure", async () => {
  const first = { ...cloneDefaults(), filenameTemplate: "$title" };
  const second = { ...cloneDefaults(), filenameTemplate: "$title - $studio" };

  const hook = mountHook(first);
  await sleep(PAST_DEBOUNCE_MS);
  hook.retarget(second);
  await sleep(PAST_DEBOUNCE_MS);
  expect(host.calls.length).toBe(2);

  expect(host.calls[0].aborted(), "the superseded request was not aborted").toBe(true);
  expect(host.calls[1].aborted()).toBe(false);

  // The host's fetch rejects an aborted request. Reporting that would be an error the hook caused
  // itself, while the request the user is waiting on is still on its way.
  host.calls[0].reject(new Error("aborted"));
  await sleep(COMMIT_MS);
  expect(hook.current.previewError, "an abort was surfaced as a preview failure").toBe(false);

  host.calls[1].resolve(sample("second"));
  await sleep(COMMIT_MS);
  expect(hook.current.preview?.[0].sampleLabel).toBe("second");
  expect(hook.current.previewError).toBe(false);

  hook.unmount();
}, 30_000);
