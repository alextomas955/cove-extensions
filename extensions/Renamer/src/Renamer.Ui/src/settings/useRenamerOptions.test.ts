// @vitest-environment jsdom
// What the options hook shows from one `GET /options`, and what it sends back. The endpoint decides
// whether a save is allowed, and a hook that ignored it would offer a Save the server refuses.
import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";

import { useRenamerOptions } from "./useRenamerOptions";
import { someOptions } from "./testOptions";
import { withQueryClient } from "../common/lib/testQueryClient";
import type { OptionsView, RenamerOptions } from "./options";

// The stubbed endpoint's script, hoisted so the module factory below can reach it.
const endpoint = vi.hoisted(() => ({
  // What `GET /options` answers.
  view: null as OptionsView | null,
  // Every non-GET call, in order.
  sent: [] as { path: string; method: string; body: unknown }[],
  // Holds the next read, so a test can look at the panel while one is in flight. Cleared as that
  // read begins, so only the one read is held.
  holdNextRead: false,
  releaseRead: (): void => undefined,
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
  errorText: (err: unknown) => String(err),
  requestJson: async () => {
    if (endpoint.holdNextRead) {
      endpoint.holdNextRead = false;
      await new Promise<void>((resolve) => {
        endpoint.releaseRead = resolve;
      });
    }
    return structuredClone(endpoint.view);
  },
  request: (path: string, init: RequestInit) => {
    const body = JSON.parse(init.body as string) as RenamerOptions;
    endpoint.sent.push({ path, method: String(init.method), body });
    // The PUT replaces the whole document, so a later GET answers with what was written and no
    // longer reports the blob unreadable. Modelled here because the hook asks the server rather
    // than assuming either.
    if (endpoint.view) endpoint.view = { ...endpoint.view, options: body, unreadable: false };
    return Promise.resolve(undefined);
  },
}));

// Apply a synchronous change and return once React has committed it. `set` and `discard` are plain
// state writes, so the commit is all there is to wait for; anything a change starts is waited for by
// the condition it settles, not by a yield.
const commit = (change: () => void) => {
  act(change);
};

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
    // Waits for a refetch the last action started.
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
  commit(() => {
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

    commit(() => {
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

  commit(() => {
    hook.current.set("filenameTemplate", "$studio");
  });
  commit(() => {
    hook.current.discard();
  });

  expect(hook.current.options?.filenameTemplate).toBe(someOptions().filenameTemplate);
  expect(hook.current.dirty).toBe(false);
});

test("a saved edit stays on screen while the re-read it starts is still in flight", async () => {
  // A save is a write and then a re-read, and between the two the cache still holds the document
  // from before the save. Falling back to it there puts the previous values back under the user's
  // caret for a round trip, and whatever they typed in that gap is overwritten when the read lands.
  const hook = await mountHook();
  const edited = "$studio - $title";

  commit(() => {
    hook.current.set("filenameTemplate", edited);
  });
  expect(hook.current.options?.filenameTemplate).toBe(edited);

  endpoint.holdNextRead = true;
  let settled = false;
  const save = hook.current.onSave().then(() => {
    settled = true;
  });
  // The re-read clears the flag as it begins, so waiting for that is waiting for the moment the two
  // assertions below are about: the write has landed and the read it started has not answered.
  await hook.settled("the save's re-read to reach the endpoint", () => !endpoint.holdNextRead);

  // The positive control for the assertion below: without a read still outstanding, the panel would
  // be past this moment and reading the saved document either way.
  expect(settled, "the save resolved before its re-read answered").toBe(false);
  expect(
    hook.current.options?.filenameTemplate,
    "the panel put the pre-save document back while its own re-read was in flight",
  ).toBe(edited);

  endpoint.releaseRead();
  await act(async () => {
    await save;
  });

  expect(hook.current.options?.filenameTemplate).toBe(edited);
  expect(hook.current.dirty, "the saved edit still reads as unsaved").toBe(false);
});

test("an edit made while the save is in flight survives it", async () => {
  // Only the save bar's own two buttons go disabled for a save, so every field stays editable
  // across both of its round trips. An edit made in that window is newer than the document being
  // written, and the save must not take it down with the draft it does clear.
  const hook = await mountHook();

  commit(() => {
    hook.current.set("filenameTemplate", "$studio - $title");
  });

  endpoint.holdNextRead = true;
  const save = hook.current.onSave();
  await hook.settled("the save's re-read to reach the endpoint", () => !endpoint.holdNextRead);

  const typedDuringSave = "$studio - $date - $title";
  commit(() => {
    hook.current.set("filenameTemplate", typedDuringSave);
  });

  endpoint.releaseRead();
  await act(async () => {
    await save;
  });

  // The write carried the document as it stood when Save was pressed, which is the half of this
  // that already held: nothing about the later edit belongs in the request.
  expect((endpoint.sent[0].body as RenamerOptions).filenameTemplate).toBe("$studio - $title");
  expect(
    hook.current.options?.filenameTemplate,
    "the save discarded the edit typed while it was in flight",
  ).toBe(typedDuringSave);
  expect(
    hook.current.dirty,
    "the surviving edit reads as saved, so nothing offers to save it",
  ).toBe(true);
});
