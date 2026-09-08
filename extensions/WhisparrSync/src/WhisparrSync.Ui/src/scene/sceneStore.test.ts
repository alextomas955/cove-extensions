/**
 * The store's transitions, and the property the tab's read states depend on: no successful read can
 * leave the region in its empty state.
 *
 * The out-of-order case is why this store exists rather than a bare hook. The host keeps the tab
 * component across a navigation between two video pages, so the first video's read can settle after
 * the second has mounted, and a store without the guard paints one scene's facts onto another.
 */
import { test, expect } from "vitest";

import type { SceneDetailView } from "../wire/api";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { createSceneStore, INITIAL_SCENE_STATE } from "./sceneStore";

const FIRST = 1;
const SECOND = 2;

function view(overrides: Partial<SceneDetailView> = {}): SceneDetailView {
  return {
    refusal: "none",
    excluded: false,
    present: true,
    monitored: true,
    qualityName: null,
    qualityProfileName: null,
    cutoffName: null,
    profileReadDidNotComplete: false,
    ...overrides,
  };
}

test("nothing has answered yet, so the answer is absent rather than empty", () => {
  const store = createSceneStore();

  expect(store.getSnapshot()).toEqual(INITIAL_SCENE_STATE);
  expect(store.getSnapshot().view).toBeNull();
  expect(deriveAsyncRegionState(store.getSnapshot().read).status).toBe("reading");
});

test("a read that answers puts the facts on screen", () => {
  const store = createSceneStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view({ monitored: false }));

  const state = store.getSnapshot();
  expect(state.view?.monitored).toBe(false);
  expect(state.read).toEqual({ reading: false, failed: false, hasContent: true });
});

test("the empty region state is unreachable for a read that answered", () => {
  const store = createSceneStore();
  store.mounted(FIRST);

  // Every answer a successful read can carry, including the one naming nothing about the instance.
  // Each names its absent values instead of leaving them out, so each is content.
  for (const answered of [
    view(),
    view({ present: false, monitored: null }),
    view({ refusal: "noIdentityInThisNamespace", present: null, monitored: null }),
    view({ profileReadDidNotComplete: true }),
  ]) {
    store.beginRead(FIRST);
    store.loaded(FIRST, answered);
    expect(deriveAsyncRegionState(store.getSnapshot().read).status).toBe("content");
  }

  // And once content is on screen nothing takes it back off, so no later transition reaches empty.
  store.beginRead(FIRST);
  expect(deriveAsyncRegionState(store.getSnapshot().read).status).toBe("content");
  store.readFailed(FIRST);
  expect(deriveAsyncRegionState(store.getSnapshot().read).status).toBe("content");
});

test("a failed first read is a failure rather than an empty answer", () => {
  const store = createSceneStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.readFailed(FIRST);

  expect(deriveAsyncRegionState(store.getSnapshot().read).status).toBe("failed");
  expect(store.getSnapshot().view).toBeNull();
});

test("a failed re-read keeps the facts it had and raises the outage", () => {
  const store = createSceneStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view());
  store.beginRead(FIRST);
  store.readFailed(FIRST);

  const region = deriveAsyncRegionState(store.getSnapshot().read);
  expect(region.status).toBe("content");
  expect(region.outage).toBe(true);
  expect(store.getSnapshot().view).not.toBeNull();
});

test("a read settling for a video no longer on screen is dropped", () => {
  const store = createSceneStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.mounted(SECOND);

  store.loaded(FIRST, view({ monitored: true }));

  expect(store.getSnapshot()).toEqual(INITIAL_SCENE_STATE);
});

test("mounting the same video twice does not discard its answer", () => {
  const store = createSceneStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view());
  store.mounted(FIRST);

  expect(store.getSnapshot().view).not.toBeNull();
});

test("a subscriber hears every settle and stops hearing once it unsubscribes", () => {
  const store = createSceneStore();
  let heard = 0;
  const stop = store.subscribe(() => {
    heard += 1;
  });

  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view());
  const whileSubscribed = heard;
  stop();
  store.beginRead(FIRST);

  expect(whileSubscribed).toBeGreaterThan(0);
  expect(heard).toBe(whileSubscribed);
});
