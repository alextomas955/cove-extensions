import { describe, expect, it } from "vitest";

import type { WhisparrSyncSettingsView } from "../wire/api";
import { createSettingsDraftStore, INITIAL_SETTINGS_STATE } from "./settingsDraftStore";

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
  upgradeBehavior: "add",
};

describe("the state before a read and the state after an empty one", () => {
  // If the two coincided, a momentary blank would read as a report that nothing is configured.
  it("are different states", () => {
    const store = createSettingsDraftStore();
    store.beginRead();
    store.loaded(NOTHING_STORED);

    expect(store.getSnapshot()).not.toEqual(INITIAL_SETTINGS_STATE);
    expect(INITIAL_SETTINGS_STATE.settings).toBeNull();
    expect(store.getSnapshot().settings).not.toBeNull();
    expect(INITIAL_SETTINGS_STATE.read.reading).toBe(true);
    expect(store.getSnapshot().read.reading).toBe(false);
  });
});

describe("a read that answers after the operator has started typing", () => {
  // A first read can land while someone is mid-address. Overwriting the field would look like it
  // clearing itself.
  it("leaves what they entered alone", () => {
    const store = createSettingsDraftStore();
    store.editAddress("http://half-typed");
    store.loaded({
      ...NOTHING_STORED,
      v3: { ...NOTHING_STORED.v3, address: "http://stored:6969" },
    });

    expect(store.getSnapshot().draft.address).toBe("http://half-typed");
    expect(store.getSnapshot().settings?.v3.address).toBe("http://stored:6969");
  });

  it("seeds the form from the answer when nothing has been entered", () => {
    const store = createSettingsDraftStore();
    store.loaded({
      ...NOTHING_STORED,
      v3: { ...NOTHING_STORED.v3, address: "http://stored:6969" },
    });

    expect(store.getSnapshot().draft.address).toBe("http://stored:6969");
  });
});

describe("a read that failed over an answer already on screen", () => {
  it("keeps the answer and raises the failure beside it", () => {
    const store = createSettingsDraftStore();
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
    const store = createSettingsDraftStore();
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
    const store = createSettingsDraftStore();
    store.loaded({
      ...NOTHING_STORED,
      v3: { ...NOTHING_STORED.v3, address: "http://whisparr:6969" },
    });
    store.beginTest("http://whisparr:6969");
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
    const store = createSettingsDraftStore();
    store.loaded(NOTHING_STORED);
    store.clearStoredKey(true);
    store.editKey("a-new-key");

    expect(store.getSnapshot().draft).toMatchObject({ apiKey: "a-new-key", keyCleared: false });
  });

  it("drops a typed key when a removal is asked for", () => {
    const store = createSettingsDraftStore();
    store.loaded(NOTHING_STORED);
    store.editKey("a-new-key");
    store.clearStoredKey(true);

    expect(store.getSnapshot().draft).toMatchObject({ apiKey: "", keyCleared: true });
  });
});

describe("choosing the other generation", () => {
  function bothStored() {
    const store = createSettingsDraftStore();
    store.loaded({
      selectedGeneration: "v3",
      v3: { ...NOTHING_STORED.v3, address: "http://three:6969", keyIsSet: true },
      v2: { ...NOTHING_STORED.v2, address: "http://two:6969" },
      upgradeBehavior: "add",
    });
    return store;
  }

  it("shows the other generation's stored values and never the first's", () => {
    const store = bothStored();
    store.chooseGeneration("v2");

    expect(store.getSnapshot().draft.generation).toBe("v2");
    expect(store.getSnapshot().draft.address).toBe("http://two:6969");
  });

  // No dialog and no save. The form is re-seeded from the card being shown, so an unsaved edit is
  // dropped rather than carried onto the other generation.
  it("discards an unsaved edit silently", () => {
    const store = bothStored();
    store.editAddress("http://edited-but-never-saved:6969");
    store.editKey("a-key-never-saved");
    store.chooseGeneration("v2");

    expect(store.getSnapshot().draft).toEqual({
      generation: "v2",
      address: "http://two:6969",
      apiKey: "",
      keyCleared: false,
      upgradeBehavior: "add",
    });
  });

  // The replacement behaviour is one setting for the page, not one per generation.
  it("leaves an unsaved replacement behaviour alone", () => {
    const store = bothStored();
    store.editBehavior("replace");
    store.chooseGeneration("v2");

    expect(store.getSnapshot().draft.upgradeBehavior).toBe("replace");
  });

  it("retires a result taken against the generation being left", () => {
    const store = bothStored();
    store.beginTest("http://three:6969");
    store.answered("http://three:6969", {
      kind: "connected",
      generation: "v3",
      capabilities: null,
      version: "3.3.8.1097",
      branch: "master",
      corroborated: true,
      otherApplication: null,
      address: "http://three:6969",
      missingSetting: null,
    });
    store.chooseGeneration("v2");

    expect(store.getSnapshot().test.phase).toBe("none");
  });

  it("changes nothing when it is already the one drafted", () => {
    const store = bothStored();
    store.editAddress("http://edited:6969");
    store.chooseGeneration("v3");

    expect(store.getSnapshot().draft.address).toBe("http://edited:6969");
  });

  // Only a save changes which generation is in use. Switching moves what is being edited.
  it("leaves the stored selection alone", () => {
    const store = bothStored();
    store.chooseGeneration("v2");

    expect(store.getSnapshot().settings?.selectedGeneration).toBe("v3");
  });
});

describe("a save that failed", () => {
  it("returns the control to its prior state and says why", () => {
    const store = createSettingsDraftStore();
    store.loaded(NOTHING_STORED);
    store.beginSave();
    store.saveFailed("500 boom");

    expect(store.getSnapshot().save).toEqual({ status: "failed", message: "500 boom" });
  });
});

describe("discarding", () => {
  function edited() {
    const store = createSettingsDraftStore();
    store.loaded({
      selectedGeneration: "v3",
      v3: { ...NOTHING_STORED.v3, address: "http://three:6969", keyIsSet: true },
      v2: { ...NOTHING_STORED.v2, address: "http://two:6969" },
      upgradeBehavior: "add",
    });
    store.editAddress("http://edited:6969");
    store.editKey("a-key-never-saved");
    store.editBehavior("replace");
    store.chooseGeneration("v2");
    return store;
  }

  it("returns every member of the draft to what is stored", () => {
    const store = edited();
    store.discard();

    expect(store.getSnapshot().draft).toEqual({
      generation: "v3",
      address: "http://three:6969",
      apiKey: "",
      keyCleared: false,
      upgradeBehavior: "add",
    });
  });

  it("takes back a pending key removal", () => {
    const store = edited();
    store.clearStoredKey(true);
    store.discard();

    expect(store.getSnapshot().draft.keyCleared).toBe(false);
  });

  it("leaves the stored settings alone", () => {
    const store = edited();
    store.discard();

    expect(store.getSnapshot().settings?.v3.address).toBe("http://three:6969");
  });
});

describe("what an edit does to the outcome of the last save", () => {
  function saved() {
    const store = createSettingsDraftStore();
    store.loaded(NOTHING_STORED);
    store.beginSave();
    store.saved(NOTHING_STORED);
    return store;
  }

  // The bar reports a save until something is unsaved again, and it cannot report both at once.
  it("clears it, whichever control was used", () => {
    for (const edit of [
      (store: ReturnType<typeof saved>) => {
        store.editAddress("http://elsewhere:6969");
      },
      (store: ReturnType<typeof saved>) => {
        store.editKey("a-new-key");
      },
      (store: ReturnType<typeof saved>) => {
        store.clearStoredKey(true);
      },
      (store: ReturnType<typeof saved>) => {
        store.editBehavior("replace");
      },
      (store: ReturnType<typeof saved>) => {
        store.chooseGeneration("v2");
      },
    ]) {
      const store = saved();
      expect(store.getSnapshot().save).toEqual({ status: "saved" });

      edit(store);

      expect(store.getSnapshot().save).toEqual({ status: "idle" });
    }
  });
});

describe("the replacement behaviour before the settings have arrived", () => {
  // Null is "not read yet", which is what keeps the control disabled. A form touched before the
  // read answers must still take the stored value, or the control never becomes usable.
  it("is taken from a read that answers after the form was touched", () => {
    const store = createSettingsDraftStore();
    store.editAddress("http://half-typed");
    store.loaded({ ...NOTHING_STORED, upgradeBehavior: "replace" });

    expect(store.getSnapshot().draft.address).toBe("http://half-typed");
    expect(store.getSnapshot().draft.upgradeBehavior).toBe("replace");
  });

  it("is not overwritten once it has been chosen", () => {
    const store = createSettingsDraftStore();
    store.loaded(NOTHING_STORED);
    store.editBehavior("replace");
    store.loaded(NOTHING_STORED);

    expect(store.getSnapshot().draft.upgradeBehavior).toBe("replace");
  });
});
