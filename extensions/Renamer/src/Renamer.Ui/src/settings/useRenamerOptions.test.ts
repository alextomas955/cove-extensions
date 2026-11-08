// @vitest-environment jsdom
// What the options hook shows from one `GET /options`, and what it sends back. The endpoint decides
// whether a save is allowed, and a hook that ignored it would offer a Save the server refuses.
import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";

import { useRenamerOptions } from "./useRenamerOptions";
import { someOptions } from "./testOptions";
import { withQueryClient } from "../common/lib/testQueryClient";
import type { OptionsView } from "./options";

// The stubbed endpoint's script, hoisted so the module factory below can reach it.
const endpoint = vi.hoisted(() => ({
  // What `GET /options` answers.
  view: null as OptionsView | null,
  // Every non-GET call, in order.
  sent: [] as { path: string; method: string; body: unknown }[],
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  errorText: (err: unknown) => String(err),
  requestJson: () => Promise.resolve(structuredClone(endpoint.view)),
  request: (path: string, init: RequestInit) => {
    endpoint.sent.push({
      path,
      method: String(init.method),
      body: JSON.parse(init.body as string) as unknown,
    });
    // The PUT writes a whole, well-formed document, so the blob a later GET reads is no longer
    // unreadable. Modelled here because the hook asks the server rather than assuming it.
    if (endpoint.view) endpoint.view = { ...endpoint.view, unreadable: false };
    return Promise.resolve(undefined);
  },
}));

// Apply a synchronous change and return once React has committed it and the effects it started have
// settled. The yield is what lets a load the change kicks off resolve inside the same `act`, so a
// caller reads committed state rather than whatever a fixed wait happened to catch.
const commit = (change: () => void) =>
  act(async () => {
    change();
    await Promise.resolve();
  });

afterEach(cleanup);

// Mount the hook and hand back its latest return value.
async function mountHook() {
  const hook = renderHook(() => useRenamerOptions(), {
    wrapper: ({ children }) => withQueryClient(children),
  });

  // The query resolves through its client's own scheduler, so how many turns that takes is not this
  // test's to know.
  await waitFor(() => {
    expect(hook.result.current.loading, "the options never loaded").toBe(false);
  });

  return {
    get current() {
      return hook.result.current;
    },
    /** Waits for a refetch the last action started. */
    settled: (condition: string, holds: () => boolean) =>
      waitFor(() => {
        expect(holds(), `waited for ${condition}, which never became true`).toBe(true);
      }),
  };
}

function answers(overrides: Partial<OptionsView> = {}) {
  endpoint.view = {
    options: someOptions(),
    pendingNameMigration: false,
    pendingDestinationMigration: false,
    unreadable: false,
    ...overrides,
  } satisfies OptionsView;
}

beforeEach(() => {
  endpoint.sent.length = 0;
  answers();
});

test("an edit is sent back as the whole settings document", async () => {
  const hook = await mountHook();

  expect(hook.current.canSave).toBe(false);
  await commit(() => {
    hook.current.set("filenameTemplate", "$studio - $title");
  });
  expect(hook.current.dirty).toBe(true);
  expect(hook.current.canSave).toBe(true);

  await act(async () => {
    await hook.current.onSave();
  });

  expect(endpoint.sent).toHaveLength(1);
  expect(endpoint.sent[0].method).toBe("PUT");
  const sent = endpoint.sent[0].body as Record<string, unknown>;
  expect(sent.filenameTemplate).toBe("$studio - $title");
  // The whole document, not the edited member: the endpoint replaces what it is given.
  expect(Object.keys(sent)).toEqual(Object.keys(someOptions()));
  expect(hook.current.dirty).toBe(false);
});

test.each([["pendingNameMigration" as const], ["pendingDestinationMigration" as const]])(
  "an edit over a blob awaiting %s is never sent",
  async (pending) => {
    answers({ [pending]: true });
    const hook = await mountHook();

    await commit(() => {
      hook.current.set("filenameTemplate", "$studio - $title");
    });

    // Dirty is the usual reason Save lights up, so the refusal has to survive an edit.
    expect(hook.current.dirty).toBe(true);
    expect(hook.current.canSave).toBe(false);

    await act(async () => {
      await hook.current.onSave();
    });

    expect(endpoint.sent, "a save was sent while a conversion was outstanding").toEqual([]);
    expect(hook.current.savedFlash).toBe(false);
  },
);

test("an unreadable stored blob offers a save although nothing is edited", async () => {
  // Nothing is dirty - the endpoint answered with the defaults - but the bad blob is still stored, and
  // a save is what replaces it.
  answers({ unreadable: true });
  const hook = await mountHook();

  expect(hook.current.dirty).toBe(false);
  expect(hook.current.recoveredFromBadBlob).toBe(true);
  expect(hook.current.canSave).toBe(true);

  await act(async () => {
    await hook.current.onSave();
  });

  expect(endpoint.sent).toHaveLength(1);
  // The save re-reads the stored facts rather than assuming them: the blob it just wrote is readable,
  // so the endpoint stops reporting one and the offer to overwrite it goes away.
  await hook.settled(
    "the save to re-read the stored blob",
    () => !hook.current.recoveredFromBadBlob,
  );
  expect(hook.current.canSave).toBe(false);
});

test("discard restores what the endpoint answered with", async () => {
  const hook = await mountHook();

  await commit(() => {
    hook.current.set("filenameTemplate", "$studio");
  });
  await commit(() => {
    hook.current.discard();
  });

  expect(hook.current.options?.filenameTemplate).toBe(someOptions().filenameTemplate);
  expect(hook.current.dirty).toBe(false);
});
