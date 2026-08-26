// @vitest-environment jsdom
/**
 * Wiring contract for the options save guard: that a stored blob the backend has not converted yet
 * cannot be written over from this panel.
 *
 * The predicate has its own suite, and a green one there proves nothing on its own — a hook that never
 * consults it saves the names away however correct the predicate is. So this renders the REAL hook over
 * a stubbed extension data store and asserts what a user would lose: no write reaches the store, and
 * the panel's own view of those rules is already empty, which is what the write would have carried.
 *
 * Two seams are stubbed, and neither is the subject. The data store, because it reaches
 * `@cove/runtime/api`, which exists only inside Cove. And the shared barrel, which this hook reaches
 * transitively for one route builder; the stand-in re-exports the REAL one rather than restating a
 * path shape that could then drift.
 *
 * A DOM is needed because the subject is a hook and the refusal is observable only once React has run
 * its effects. React arrives as its PRODUCTION build (the bundle's `process.env.NODE_ENV` define
 * applies here too), which has no `act`, so renders are flushed by waiting rather than by wrapping,
 * and `node:assert` is unreachable, so the assertions are vitest's `expect`.
 */
import { test, expect, vi, beforeEach } from "vitest";
import { createElement } from "react";
import { createRoot } from "react-dom/client";

import { useRenamerOptions, type UseRenamerOptions } from "./useRenamerOptions";

/** The stubbed store's script, hoisted so the module factory below can reach it. */
const store = vi.hoisted(() => ({
  /** The stored "options" blob every load reads, verbatim. */
  blob: "",
  /** Every key/value pair a save wrote, in order. */
  writes: [] as [string, unknown][],
}));

vi.mock("@cove-extensions/ui-shared/extensionStore", () => ({
  createExtensionDataStore: () => ({
    getAll: () => Promise.resolve({ options: store.blob }),
    set: (key: string, value: unknown) => {
      store.writes.push([key, value]);
      return Promise.resolve();
    },
  }),
}));

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  ApiError: class ApiError extends Error {},
}));

vi.mock("@cove-extensions/ui-shared", async () => ({
  extensionApi: (await import("../../../../../../shared/ui-shared/src/actions")).extensionApi,
}));

const sleep = (ms: number) =>
  new Promise((resolve) => {
    setTimeout(resolve, ms);
  });

/** Long enough for React to commit a render on the default lane without `act` to force it. */
const COMMIT_MS = 50;

/** Mount the hook and hand back its latest return value plus a teardown. */
async function mountHook() {
  let latest: UseRenamerOptions | null = null;
  function Probe() {
    latest = useRenamerOptions();
    return null;
  }

  const container = document.createElement("div");
  document.body.append(container);
  const root = createRoot(container);
  root.render(createElement(Probe));
  await sleep(COMMIT_MS);

  return {
    get current(): UseRenamerOptions {
      expect(latest, "the probe never rendered").not.toBeNull();
      return latest as unknown as UseRenamerOptions;
    },
    unmount: () => {
      root.unmount();
      container.remove();
    },
  };
}

/** A blob whose tag rules are still keyed and valued by name, as an install predating ids stored it. */
const LEGACY_BLOB = JSON.stringify({
  FilenameTemplate: "$title",
  Tags: { Whitelist: ["anime"], Blacklist: ["raw"] },
  Performers: { Whitelist: ["Jane Doe"] },
  ExcludeTags: ["spoiler"],
  TagDestinations: { Anime: "D:/anime" },
});

/** The same install after a host start converted it: the same rules, keyed by id. */
const CONVERTED_BLOB = JSON.stringify({
  FilenameTemplate: "$title",
  Tags: { WhitelistIds: [11], BlacklistIds: [22] },
  Performers: { WhitelistIds: [33] },
  ExcludeTagIds: [44],
  TagDestinations: { 55: { Root: "D:/anime", Template: "" } },
});

beforeEach(() => {
  store.writes.length = 0;
  store.blob = "";
});

test("an unconverted blob refuses the save that would erase its name-keyed rules", async () => {
  store.blob = LEGACY_BLOB;
  const hook = await mountHook();

  expect(hook.current.pendingNameMigration).toBe(true);
  // What the refusal is protecting: the panel's own state already holds none of those rules, so this
  // is what a save would have written over them.
  expect(hook.current.options.Tags.WhitelistIds).toEqual([]);
  expect(hook.current.options.Performers.WhitelistIds).toEqual([]);
  expect(hook.current.options.ExcludeTagIds).toEqual([]);
  expect(hook.current.options.TagDestinations).toEqual({});

  // Editing must not unblock it: `dirty` is the usual reason Save lights up.
  hook.current.set("FilenameTemplate", "$studio - $title");
  await sleep(COMMIT_MS);
  expect(hook.current.dirty).toBe(true);
  expect(hook.current.canSave).toBe(false);

  // And the store write is refused at the hook, not only at the button.
  await hook.current.onSave();
  await sleep(COMMIT_MS);

  expect(store.writes, "a save reached the store over an unconverted blob").toEqual([]);
  expect(hook.current.savedFlash).toBe(false);

  hook.unmount();
}, 30_000);

test("the same install saves normally once the conversion has run", async () => {
  // The refusal above must not be reachable by refusing everything, so the converted blob is driven
  // through the same wiring to the other outcome.
  store.blob = CONVERTED_BLOB;
  const hook = await mountHook();

  expect(hook.current.pendingNameMigration).toBe(false);
  expect(hook.current.options.Tags.WhitelistIds).toEqual([11]);

  hook.current.set("FilenameTemplate", "$studio - $title");
  await sleep(COMMIT_MS);
  expect(hook.current.canSave).toBe(true);

  await hook.current.onSave();
  await sleep(COMMIT_MS);

  expect(store.writes.length).toBe(1);
  const [key, value] = store.writes[0];
  expect(key).toBe("options");
  const written = value as Record<string, unknown>;
  expect(written.FilenameTemplate).toBe("$studio - $title");
  expect(written.ExcludeTagIds).toEqual([44]);

  hook.unmount();
}, 30_000);

test("a blob storing the empty legacy keys is not held back by them", async () => {
  // The pre-migration panel serialised its whole defaults object, so an install that configured
  // neither group still stores these keys empty. The backend stamps such a blob done without
  // converting anything, so refusing here would lock the panel out permanently.
  store.blob = JSON.stringify({
    FilenameTemplate: "$title",
    Tags: { Whitelist: [], Blacklist: [] },
    Performers: { Whitelist: [], Blacklist: [] },
    ExcludeTags: [],
    TagDestinations: {},
  });
  const hook = await mountHook();

  expect(hook.current.pendingNameMigration).toBe(false);

  hook.current.set("FilenameTemplate", "$studio");
  await sleep(COMMIT_MS);
  await hook.current.onSave();
  await sleep(COMMIT_MS);

  expect(store.writes.length).toBe(1);

  hook.unmount();
}, 30_000);
