/**
 * The store's transitions, and the one property it has that the banner store does not: a read that
 * settles for an entity no longer on screen is dropped.
 *
 * The out-of-order case is the reason this store exists rather than a copy of the banner's. The host
 * keeps one slot component across a navigation between two entity pages, so the first entity's read
 * can settle after the second has mounted, and a store without the guard paints one studio's state
 * onto another studio's page.
 */
import { test, expect } from "vitest";

import type { EntityMonitoringView } from "../wire/api";
import {
  createMonitoringStore,
  INITIAL_MONITORING_STATE,
  sameEntity,
  type MonitoredEntity,
} from "./monitoringStore";

const FIRST: MonitoredEntity = { kind: "studio", coveId: 1 };
const SECOND: MonitoredEntity = { kind: "studio", coveId: 2 };

function view(monitored: boolean): EntityMonitoringView {
  return {
    kind: "studio",
    generation: "v3",
    monitored,
    refusal: "none",
    capabilities: ["monitorStudio"],
  };
}

test("nothing has answered yet, so the answer is absent rather than empty", () => {
  const store = createMonitoringStore();

  expect(store.getSnapshot()).toEqual(INITIAL_MONITORING_STATE);
  expect(store.getSnapshot().view).toBeNull();
  expect(store.getSnapshot().read.reading).toBe(true);
});

test("a read that answers puts the view on screen", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view(true));

  const state = store.getSnapshot();
  expect(state.view?.monitored).toBe(true);
  expect(state.read).toEqual({ reading: false, failed: false, hasContent: true });
  expect(state.readError).toBeNull();
});

test("a failed re-read keeps the content it had and raises the failure", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view(true));
  store.beginRead(FIRST);
  store.readFailed(FIRST, "503 unavailable");

  const state = store.getSnapshot();
  expect(state.view?.monitored).toBe(true);
  expect(state.read).toEqual({ reading: false, failed: true, hasContent: true });
  expect(state.readError).toBe("503 unavailable");
});

test("a first read that fails leaves no content, so nothing paints a state", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.readFailed(FIRST, "500 nope");

  expect(store.getSnapshot().view).toBeNull();
  expect(store.getSnapshot().read.hasContent).toBe(false);
});

test("an action in flight is reported, and finishing it paints no answer of its own", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(false));

  store.beginAction(FIRST);
  expect(store.getSnapshot().acting).toBe(true);

  store.actionSucceeded(FIRST);
  expect(store.getSnapshot().acting).toBe(false);
  // What the entity now is comes from the read that follows, never from the action's own answer.
  expect(store.getSnapshot().view?.monitored).toBe(false);

  store.loaded(FIRST, view(true));
  expect(store.getSnapshot().view?.monitored).toBe(true);
});

test("an action that fails releases the control and says why", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(false));
  store.beginAction(FIRST);
  store.actionFailed(FIRST, "403 forbidden");

  expect(store.getSnapshot().acting).toBe(false);
  expect(store.getSnapshot().actionError).toBe("403 forbidden");
  expect(store.getSnapshot().view?.monitored).toBe(false);
});

test("two reads settling in reverse order leave the mounted entity's state on screen", () => {
  const store = createMonitoringStore();

  store.mounted(FIRST);
  store.beginRead(FIRST);

  // The page moves on before the first read answers.
  store.mounted(SECOND);
  store.beginRead(SECOND);

  // The second entity answers first, then the first entity's read settles late.
  store.loaded(SECOND, view(false));
  store.loaded(FIRST, view(true));

  expect(store.getSnapshot().view?.monitored).toBe(false);
});

test("a late failure for an entity that has left the screen changes nothing", () => {
  const store = createMonitoringStore();

  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.mounted(SECOND);
  store.loaded(SECOND, view(true));
  store.readFailed(FIRST, "504 gateway timeout");

  const state = store.getSnapshot();
  expect(state.readError).toBeNull();
  expect(state.read.failed).toBe(false);
  expect(state.view?.monitored).toBe(true);
});

test("mounting a different entity clears the previous entity's answer", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(true));

  store.mounted(SECOND);

  expect(store.getSnapshot()).toEqual(INITIAL_MONITORING_STATE);
});

test("mounting the same entity again does not restart it", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(true));

  store.mounted({ kind: "studio", coveId: 1 });

  expect(store.getSnapshot().view?.monitored).toBe(true);
});

test("two kinds sharing a Cove id are different entities", () => {
  expect(sameEntity({ kind: "studio", coveId: 7 }, { kind: "performer", coveId: 7 })).toBe(false);
  expect(sameEntity({ kind: "studio", coveId: 7 }, { kind: "studio", coveId: 7 })).toBe(true);
  expect(sameEntity(null, { kind: "studio", coveId: 7 })).toBe(false);
});

test("a settle for an entity nothing mounted is dropped", () => {
  const store = createMonitoringStore();

  store.loaded(FIRST, view(true));

  expect(store.getSnapshot().view).toBeNull();
});
