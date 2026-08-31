import { describe, expect, it } from "vitest";

import type { WhisparrSyncSettingsView } from "../wire/api";
import { createConnectionStore, INITIAL_CONNECTION_STATE } from "./connectionStore";

/** A settings answer in which nothing at all is stored: the genuinely-empty read. */
const NOTHING_STORED: WhisparrSyncSettingsView = {
  selectedGeneration: "v3",
  v3: {
    address: "",
    keyIsSet: false,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
  v2: {
    address: "",
    keyIsSet: false,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
};

describe("the state before a read and the state after an empty one", () => {
  // The two must never coincide. An initial value equal to the loaded-and-empty value is how a
  // momentary blank comes to read as a confident report that nothing is configured.
  it("are different states", () => {
    const store = createConnectionStore();
    store.beginRead();
    store.loaded(NOTHING_STORED);

    expect(store.getSnapshot()).not.toEqual(INITIAL_CONNECTION_STATE);
    expect(INITIAL_CONNECTION_STATE.settings).toBeNull();
    expect(store.getSnapshot().settings).not.toBeNull();
    expect(INITIAL_CONNECTION_STATE.read.reading).toBe(true);
    expect(store.getSnapshot().read.reading).toBe(false);
  });
});

describe("a read that failed over an answer already on screen", () => {
  it("keeps the answer and raises the failure beside it", () => {
    const store = createConnectionStore();
    store.loaded(NOTHING_STORED);
    store.beginRead();
    store.readFailed("503 unavailable");

    expect(store.getSnapshot().settings).not.toBeNull();
    expect(store.getSnapshot().read).toEqual({ reading: false, failed: true, hasContent: true });
    expect(store.getSnapshot().readError).toBe("503 unavailable");
  });
});

describe("what an address edit does to the result on screen", () => {
  function withAnswer() {
    const store = createConnectionStore();
    store.loaded({
      ...NOTHING_STORED,
      v3: { ...NOTHING_STORED.v3, address: "http://whisparr:6969" },
    });
    store.beginTest("http://whisparr:6969");
    store.answered("http://whisparr:6969", {
      kind: "connected",
      generation: "v3",
      capabilities: null,
      version: "3.3.8.1097",
      branch: "master",
      corroborated: true,
      otherApplication: null,
      address: "http://whisparr:6969",
      missingSetting: null,
    });
    return store;
  }

  it("keeps it when the change does not move the address", () => {
    const store = withAnswer();
    store.editAddress("HTTP://WHISPARR:6969/");

    expect(store.getSnapshot().test.phase).toBe("answered");
  });

  it("retires it when the change does", () => {
    const store = withAnswer();
    store.editAddress("http://whisparr:6970");

    expect(store.getSnapshot().test.phase).toBe("none");
  });

  it("retires it when the field is emptied", () => {
    const store = withAnswer();
    store.editAddress("");

    expect(store.getSnapshot().test.phase).toBe("none");
  });
});

describe("the result an answer that lands late describes", () => {
  it("is the address the test was run against, not the field's current value", () => {
    const store = createConnectionStore();
    store.loaded({
      ...NOTHING_STORED,
      v3: { ...NOTHING_STORED.v3, address: "http://whisparr:6969" },
    });
    store.beginTest("http://whisparr:6969");
    // The field moves while the request is in flight, then the answer lands.
    store.editAddress("http://elsewhere:6969");
    store.answered("http://whisparr:6969", {
      kind: "unreachable",
      generation: null,
      capabilities: null,
      version: null,
      branch: null,
      corroborated: null,
      otherApplication: null,
      address: "http://whisparr:6969",
      missingSetting: null,
    });

    const state = store.getSnapshot();
    expect(state.draft.address).toBe("http://elsewhere:6969");
    expect(state.test.phase === "none" ? null : state.test.address).toBe("http://whisparr:6969");
  });
});

describe("the two contradictory things a user can ask of a stored key", () => {
  it("takes back a pending removal when a new key is typed", () => {
    const store = createConnectionStore();
    store.loaded(NOTHING_STORED);
    store.clearStoredKey(true);
    store.editKey("a-new-key");

    expect(store.getSnapshot().draft).toMatchObject({ apiKey: "a-new-key", keyCleared: false });
  });

  it("drops a typed key when a removal is asked for", () => {
    const store = createConnectionStore();
    store.loaded(NOTHING_STORED);
    store.editKey("a-new-key");
    store.clearStoredKey(true);

    expect(store.getSnapshot().draft).toMatchObject({ apiKey: "", keyCleared: true });
  });
});

describe("a save that failed", () => {
  it("returns the control to its prior state and says why", () => {
    const store = createConnectionStore();
    store.loaded(NOTHING_STORED);
    store.beginSave();
    store.saveFailed("500 boom");

    expect(store.getSnapshot().save).toEqual({ status: "failed", message: "500 boom" });
  });
});
