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
    scope: null,
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
});

test("a failed re-read keeps the content it had and raises the failure", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.loaded(FIRST, view(true));
  store.beginRead(FIRST);
  store.readFailed(FIRST);

  const state = store.getSnapshot();
  expect(state.view?.monitored).toBe(true);
  expect(state.read).toEqual({ reading: false, failed: true, hasContent: true });
  expect(state.actionFailed).toBe(false);
});

test("a first read that fails leaves no content, so nothing paints a state", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.readFailed(FIRST);

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

test("an action that fails releases the control and is recorded as failed", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(false));
  store.beginAction(FIRST);
  store.actionFailed(FIRST);

  expect(store.getSnapshot().acting).toBe(false);
  expect(store.getSnapshot().actionFailed).toBe(true);
  expect(store.getSnapshot().view?.monitored).toBe(false);
});

// The two failure entries take the entity and nothing else, so there is no channel through which an
// instance's own response text could enter this state at all. Asserted on the arity rather than on
// the stored value: a value nothing supplied cannot be found, so an assertion looking for one would
// pass whatever the signature admitted.
test("neither failure entry admits a message, so instance text has no way in", () => {
  const store = createMonitoringStore();

  expect(store.readFailed.length).toBe(1);
  expect(store.actionFailed.length).toBe(1);

  store.mounted(FIRST);
  store.beginRead(FIRST);
  store.readFailed(FIRST);
  store.beginAction(FIRST);
  store.actionFailed(FIRST);

  const state = store.getSnapshot();
  expect(typeof state.actionFailed).toBe("boolean");
  expect(state.read).toEqual({ reading: false, failed: true, hasContent: false });
});

test("an action the server answered and skipped is neither a success nor a failure", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(true));
  store.beginAction(FIRST);
  store.actionSkipped(FIRST, "hardLinksOff");

  const state = store.getSnapshot();
  expect(state.acting).toBe(false);
  expect(state.actionFailed).toBe(false);
  expect(state.actionSkip).toBe("hardLinksOff");
});

test("the next gesture clears the skip the previous one recorded", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginAction(FIRST);
  store.actionSkipped(FIRST, "hardLinksOff");
  store.beginAction(FIRST);

  expect(store.getSnapshot().actionSkip).toBeNull();

  store.actionSucceeded(FIRST);
  expect(store.getSnapshot().actionSkip).toBeNull();
});

test("an action the instance refused is neither a success, a failure nor a skip", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.loaded(FIRST, view(false));
  store.beginAction(FIRST);
  store.actionSkipped(FIRST, "hardLinksOff");
  store.actionRefused(FIRST, "noQualityProfile");

  const state = store.getSnapshot();
  expect(state.acting).toBe(false);
  expect(state.actionFailed).toBe(false);
  expect(state.actionSkip).toBeNull();
  expect(state.actionRefusal).toBe("noQualityProfile");
});

// The hook reads the state back after every press, so a `loaded` that cleared the field would erase
// the sentence in the same frame it was set.
test("the read that follows a refused action leaves the reason on screen", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);
  store.beginAction(FIRST);
  store.actionRefused(FIRST, "instanceRefused");
  store.loaded(FIRST, view(false));

  expect(store.getSnapshot().actionRefusal).toBe("instanceRefused");
  expect(store.getSnapshot().view?.monitored).toBe(false);
});

test("the next gesture clears the refusal the previous one recorded", () => {
  const store = createMonitoringStore();
  store.mounted(FIRST);

  store.actionRefused(FIRST, "noRootFolder");
  store.beginAction(FIRST);
  expect(store.getSnapshot().actionRefusal).toBeNull();

  store.actionRefused(FIRST, "noRootFolder");
  store.actionSucceeded(FIRST);
  expect(store.getSnapshot().actionRefusal).toBeNull();

  store.actionRefused(FIRST, "noRootFolder");
  store.actionSkipped(FIRST, "hardLinksOff");
  expect(store.getSnapshot().actionRefusal).toBeNull();

  store.actionRefused(FIRST, "noRootFolder");
  store.actionFailed(FIRST);
  expect(store.getSnapshot().actionRefusal).toBeNull();
});

test("a refusal for an entity that has left the screen changes nothing", () => {
  const store = createMonitoringStore();

  store.mounted(FIRST);
  store.mounted(SECOND);
  store.loaded(SECOND, view(true));
  store.actionRefused(FIRST, "instanceRefused");

  expect(store.getSnapshot().actionRefusal).toBeNull();
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
  store.readFailed(FIRST);

  const state = store.getSnapshot();
  expect(state.actionFailed).toBe(false);
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
