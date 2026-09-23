// @vitest-environment jsdom
// What the options hook shows from one `GET /options`, and what it sends back. The endpoint decides
// whether a save is allowed, and a hook that ignored it would offer a Save the server refuses.
import { test, expect, vi, beforeEach } from "vitest";
import { act, createElement } from "react";
import { createRoot } from "react-dom/client";

import { useRenamerOptions, type UseRenamerOptions } from "./useRenamerOptions";
import { someOptions } from "./testOptions";
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
    return Promise.resolve(undefined);
  },
}));

// `act` refuses to run without it, and React reads it off the global rather than from an import.
declare global {
  var IS_REACT_ACT_ENVIRONMENT: boolean;
}
globalThis.IS_REACT_ACT_ENVIRONMENT = true;

// Apply a synchronous change and return once React has committed it and the effects it started have
// settled. The yield is what lets a load the change kicks off resolve inside the same `act`, so a
// caller reads committed state rather than whatever a fixed wait happened to catch.
const commit = (change: () => void) =>
  act(async () => {
    change();
    await Promise.resolve();
  });

// Mount the hook and hand back its latest return value plus a teardown.
async function mountHook() {
  let latest: UseRenamerOptions | null = null;
  function Probe() {
    latest = useRenamerOptions();
    return null;
  }

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  await commit(() => {
    root.render(createElement(Probe));
  });

  return {
    get current(): UseRenamerOptions {
      expect(latest, "the probe never rendered").not.toBeNull();
      return latest as unknown as UseRenamerOptions;
    },
    unmount: async () => {
      await commit(() => {
        root.unmount();
      });
      container.remove();
    },
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

  await hook.unmount();
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

    await hook.unmount();
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
  expect(hook.current.canSave).toBe(false);

  await hook.unmount();
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

  await hook.unmount();
});
