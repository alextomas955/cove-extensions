// @vitest-environment jsdom
// What the options hook shows from one `GET /options`, and what it sends back. The endpoint decides
// whether a save is allowed, and a hook that ignored it would offer a Save the server refuses.
// Registers the interceptor that turns a failed request into an ApiError carrying its status.
import "../common/lib/wireErrors";

import { test, expect, vi, beforeEach, afterEach } from "vitest";
import { renderHook, act, cleanup, waitFor } from "@testing-library/react";

import { useRenamerOptions } from "./useRenamerOptions";
import { someOptions } from "./testOptions";
import type { OptionsView, RenamerOptions } from "./options";
import { jsonResponse, withQueryClient } from "@cove-extensions/ui-shared/testing";

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
  // Refuse the next read, or the next write. Each is cleared as it is answered, so a later one
  // succeeds.
  failNextRead: false,
  failNextWrite: false,
}));

// The seam is the host's own fetch, which the generated client reaches through the adapter in
// common/lib/wireClient, so what is scripted here is the request the hook really issues.
vi.mock("@cove/runtime/api", () => ({
  extensionFetch: async (path: string, init: RequestInit) => {
    if (String(init.method).toUpperCase() === "PUT") {
      if (endpoint.failNextWrite) {
        endpoint.failNextWrite = false;
        return jsonResponse({ detail: "the settings could not be stored" }, 503);
      }
      const body = JSON.parse(init.body as string) as RenamerOptions;
      endpoint.sent.push({ path, method: String(init.method), body });
      // The PUT replaces the whole document, so a later GET answers with what was written and no
      // longer reports the blob unreadable. Modelled here because the hook asks the server rather
      // than assuming either.
      if (endpoint.view) endpoint.view = { ...endpoint.view, options: body, unreadable: false };
      return jsonResponse(undefined, 204);
    }

    if (endpoint.holdNextRead) {
      endpoint.holdNextRead = false;
      await new Promise<void>((resolve) => {
        endpoint.releaseRead = resolve;
      });
    }
    if (endpoint.failNextRead) {
      endpoint.failNextRead = false;
      return jsonResponse({ detail: "the settings could not be read" }, 503);
    }
    return jsonResponse(structuredClone(endpoint.view));
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
  endpoint.holdNextRead = false;
  endpoint.failNextRead = false;
  endpoint.failNextWrite = false;
  answers();
});

test("an edit is sent back as the whole settings document", async () => {
  const hook = await mountHook();

  expect(hook.current.saveBar).toBeNull();
  commit(() => {
    hook.current.set("filenameTemplate", "$studio - $title");
  });
  expect(hook.current.dirty).toBe(true);
  expect(hook.current.saveBar).toEqual({ kind: "unsaved" });

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

test.each([
  ["pendingNameMigration" as const, "name" as const],
  ["pendingDestinationMigration" as const, "destination" as const],
])("an edit over a blob awaiting %s is never sent", async (pending, migration) => {
  answers({ [pending]: true });
  const hook = await mountHook();

  commit(() => {
    hook.current.set("filenameTemplate", "$studio - $title");
  });

  // Dirty is the usual reason Save lights up, so the refusal has to survive an edit, and the bar
  // has to carry which conversion is outstanding rather than reading as an ordinary unsaved edit.
  expect(hook.current.dirty).toBe(true);
  expect(hook.current.saveBar).toEqual({ kind: "blocked", migration });

  await act(async () => {
    await hook.current.onSave();
  });

  expect(endpoint.sent, "a save was sent while a conversion was outstanding").toEqual([]);
  // Unchanged: a save that never went out reports neither an outcome nor a failure.
  expect(hook.current.saveBar).toEqual({ kind: "blocked", migration });
});

test("an unreadable stored blob offers a save although nothing is edited", async () => {
  // Nothing is dirty - the endpoint answered with the defaults - but the bad blob is still stored, and
  // a save is what replaces it.
  answers({ unreadable: true });
  const hook = await mountHook();

  expect(hook.current.dirty).toBe(false);
  expect(hook.current.recoveredFromBadBlob).toBe(true);
  expect(hook.current.saveBar).toEqual({ kind: "recovered" });

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
  expect(hook.current.saveBar).toEqual({ kind: "saved", unconfirmed: false });
});

test("a recovery save the page could not read back still ends the recovery", async () => {
  // The write is what replaces the unreadable document. Until a read lands the cached response
  // still reports the blob that write replaced, so reading recovery off it asks for the same save
  // a second time.
  answers({ unreadable: true });
  const hook = await mountHook();

  endpoint.failNextRead = true;
  await act(async () => {
    await hook.current.onSave();
  });

  expect(endpoint.sent).toHaveLength(1);
  expect(hook.current.recoveredFromBadBlob).toBe(false);
  expect(hook.current.saveBar).toEqual({ kind: "saved", unconfirmed: true });
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

test("a save whose re-read fails keeps the saved values on screen and says so", async () => {
  const hook = await mountHook();
  const edited = "$studio - $title";

  commit(() => {
    hook.current.set("filenameTemplate", edited);
  });

  endpoint.failNextRead = true;
  await act(async () => {
    await hook.current.onSave();
  });

  expect(endpoint.sent, "the write never went out").toHaveLength(1);
  expect(
    hook.current.options?.filenameTemplate,
    "the pre-save document replaced the values that were written",
  ).toBe(edited);
  expect(
    hook.current.saveBar,
    "the write that landed is reported as one that failed, or not at all",
  ).toEqual({ kind: "saved", unconfirmed: true });
  expect(
    hook.current.loadError,
    "nothing says why the panel cannot confirm the save",
  ).not.toBeNull();

  // The next read answers, which is what the panel is waiting for: the server's own copy arrives
  // and matches, so there is nothing left unsaved.
  await act(async () => {
    await hook.current.load();
  });
  await hook.settled("the retried read to land", () => hook.current.loadError === null);
  expect(hook.current.options?.filenameTemplate).toBe(edited);
  expect(hook.current.dirty, "the confirmed save still reads as unsaved").toBe(false);
});

// Discard goes back to what is stored, which the two cases disagree about: a write that failed stored
// nothing, so the server's copy is still the panel's; a write that landed past a failed read stored
// what was sent, and the cache's older copy is not what Discard may show.
test.each([
  ["a save that failed", { failWrite: true, restores: someOptions().filenameTemplate }],
  ["a save the page could not read back", { failWrite: false, restores: "$studio - $title" }],
])(
  "discard after %s leaves no message about changes that are gone",
  async (_case, { failWrite, restores }) => {
    // Discard takes the edit off screen. A sentence about it that outlives it describes a state the
    // user can no longer see, and the save bar has no other condition to hide on.
    const hook = await mountHook();

    commit(() => {
      hook.current.set("filenameTemplate", "$studio - $title");
    });

    if (failWrite) endpoint.failNextWrite = true;
    else endpoint.failNextRead = true;
    await act(async () => {
      await hook.current.onSave();
    });
    expect(
      hook.current.saveBar,
      "the save said nothing, so there is no message for discard to clear",
    ).not.toBeNull();

    commit(() => {
      hook.current.discard();
    });

    expect(hook.current.saveBar).toBeNull();
    expect(hook.current.dirty).toBe(false);
    expect(hook.current.options?.filenameTemplate).toBe(restores);
  },
);

test("a later edit builds on a save the page could not read back, not on the copy before it", async () => {
  // The write landed and the read after it did not, so the cache still holds the document from
  // before the save and only the panel knows what the server took. An edit that rebased on the
  // cache would send those older values back and undo a save that worked.
  const hook = await mountHook();
  const stored = "$studio - $title";

  commit(() => {
    hook.current.set("filenameTemplate", stored);
  });

  endpoint.failNextRead = true;
  await act(async () => {
    await hook.current.onSave();
  });

  commit(() => {
    hook.current.discard();
  });
  commit(() => {
    hook.current.set("folderTemplate", "$studio");
  });
  await act(async () => {
    await hook.current.onSave();
  });

  expect(endpoint.sent, "the second save never went out").toHaveLength(2);
  const second = endpoint.sent[1].body as RenamerOptions;
  expect(second.filenameTemplate, "the second save put the pre-save template back").toBe(stored);
  expect(second.folderTemplate).toBe("$studio");
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
