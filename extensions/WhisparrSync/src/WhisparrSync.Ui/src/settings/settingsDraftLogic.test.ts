import { describe, expect, it } from "vitest";

import type { WhisparrSyncSettingsView } from "../wire/api";
import {
  draftFor,
  testsStoredConnection,
  unsavedFields,
  unsavedSummary,
  writeRequestFor,
  type SettingsDraft,
} from "./settingsDraftLogic";

const STORED: WhisparrSyncSettingsView = {
  selectedGeneration: "v3",
  v3: {
    address: "http://whisparr:6969",
    keyIsSet: true,
    recordedVersion: "3.3.8.1097",
    versionVerifiedAtUtc: "2026-06-24T11:56:00Z",
    lastReachableAtUtc: "2026-06-24T11:56:00Z",
  },
  v2: {
    address: "http://two:6969",
    keyIsSet: false,
    recordedVersion: null,
    versionVerifiedAtUtc: null,
    lastReachableAtUtc: null,
  },
  upgradeBehavior: "add",
};

const SAVED_DRAFT = draftFor(STORED, "v3");

describe("which members of a draft are unsaved", () => {
  it("is none of them for a draft seeded from what is stored", () => {
    expect(unsavedFields(STORED, SAVED_DRAFT)).toEqual([]);
  });

  it("is none of them before the settings have arrived, so an unread page shows no bar", () => {
    expect(unsavedFields(null, SAVED_DRAFT)).toEqual([]);
  });

  it("is not the address for a change that does not move it", () => {
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, address: "HTTP://WHISPARR:6969/" })).toEqual([]);
  });

  it("is the address for a change that does", () => {
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, address: "http://elsewhere:6969" })).toEqual([
      "address",
    ]);
  });

  it("is the key for a typed one and for a removal, and never for a blank field", () => {
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, apiKey: "typed" })).toEqual(["apiKey"]);
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, keyCleared: true })).toEqual(["apiKey"]);
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, apiKey: "" })).toEqual([]);
  });

  it("is the generation for one other than the stored selection", () => {
    expect(unsavedFields(STORED, draftFor(STORED, "v2"))).toContain("generation");
  });

  it("is the replacement behaviour for one other than the stored value", () => {
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, upgradeBehavior: "replace" })).toEqual([
      "upgradeBehavior",
    ]);
  });

  it("is not the replacement behaviour before the settings have said what is stored", () => {
    expect(unsavedFields(STORED, { ...SAVED_DRAFT, upgradeBehavior: null })).toEqual([]);
  });
});

describe("the sentence the bar shows", () => {
  it("names every unsaved field and no field that is saved", () => {
    const summary = unsavedSummary(
      unsavedFields(STORED, { ...SAVED_DRAFT, address: "http://elsewhere:6969", apiKey: "typed" }),
    );

    expect(summary).toContain("Whisparr address");
    expect(summary).toContain("API key");
    expect(summary).not.toContain("replacement-file behaviour");
  });

  it("adds the reload sentence when the generation is unsaved", () => {
    const summary = unsavedSummary(unsavedFields(STORED, draftFor(STORED, "v2")));

    expect(summary).toContain("reloads the page");
  });

  it("adds it for no other field", () => {
    const summary = unsavedSummary(
      unsavedFields(STORED, { ...SAVED_DRAFT, upgradeBehavior: "replace" }),
    );

    expect(summary).toContain("replacement-file behaviour");
    expect(summary).not.toContain("reloads the page");
  });

  it("says nothing at all while nothing is unsaved", () => {
    expect(unsavedSummary(unsavedFields(STORED, SAVED_DRAFT))).toBe("");
  });
});

describe("the three things a save can do to the stored key", () => {
  // A blank field is the whole of "keep". A fourth value would be a key state nothing can ask for.
  it("keeps it for a blank field, and sends no key", () => {
    const request = writeRequestFor(STORED, {
      ...SAVED_DRAFT,
      address: "http://elsewhere:6969",
      apiKey: "",
    });

    expect(request.v3).toEqual({
      address: "http://elsewhere:6969",
      keyWrite: "keep",
      apiKey: null,
    });
  });

  it("clears it for a stored key marked for removal, and sends no key", () => {
    const request = writeRequestFor(STORED, { ...SAVED_DRAFT, keyCleared: true });

    expect(request.v3).toEqual({
      address: SAVED_DRAFT.address,
      keyWrite: "clear",
      apiKey: null,
    });
  });

  it("replaces it with a typed key", () => {
    const request = writeRequestFor(STORED, { ...SAVED_DRAFT, apiKey: "a-new-key" });

    expect(request.v3).toEqual({
      address: SAVED_DRAFT.address,
      keyWrite: "replace",
      apiKey: "a-new-key",
    });
  });
});

describe("what one save writes", () => {
  it("names the drafted generation as the selected one on every request", () => {
    expect(writeRequestFor(STORED, SAVED_DRAFT).selectedGeneration).toBe("v3");
    expect(writeRequestFor(STORED, draftFor(STORED, "v2")).selectedGeneration).toBe("v2");
  });

  it("leaves the generation the draft does not name alone", () => {
    const v2Draft: SettingsDraft = { ...draftFor(STORED, "v2"), apiKey: "a-new-key" };
    const request = writeRequestFor(STORED, v2Draft);

    expect(request.v3).toBeNull();
    expect(request.v2).not.toBeNull();
  });

  // The per-control write this replaced could not touch a connection. Neither can this one.
  it("writes neither connection for a save of the replacement behaviour alone", () => {
    const request = writeRequestFor(STORED, { ...SAVED_DRAFT, upgradeBehavior: "replace" });

    expect(request.v3).toBeNull();
    expect(request.v2).toBeNull();
    expect(request.upgradeBehavior).toBe("replace");
  });

  // The address travels in the spelling the field holds, and a spelling the server reads as the
  // same address reports nothing unsaved, so a save of some other field must not carry it.
  it("writes no connection for an address that differs only in ways that do not move it", () => {
    const request = writeRequestFor(STORED, {
      ...SAVED_DRAFT,
      address: "HTTP://WHISPARR:6969/",
      upgradeBehavior: "replace",
    });

    expect(request.v3).toBeNull();
  });

  it("writes the connection for a generation change that carries a typed key", () => {
    const request = writeRequestFor(STORED, { ...draftFor(STORED, "v2"), apiKey: "a-new-key" });

    expect(request.selectedGeneration).toBe("v2");
    expect(request.v2?.keyWrite).toBe("replace");
  });

  // Selecting the other generation is a write of the selection alone. Its stored address and key
  // are already stored under it.
  it("writes no connection for a generation change with nothing else touched", () => {
    const request = writeRequestFor(STORED, draftFor(STORED, "v2"));

    expect(request.v2).toBeNull();
    expect(request.v3).toBeNull();
  });

  it("leaves the replacement behaviour alone while it is not unsaved", () => {
    expect(writeRequestFor(STORED, { ...SAVED_DRAFT, apiKey: "typed" }).upgradeBehavior).toBeNull();
  });
});

describe("whether Test asks about the stored connection", () => {
  it("asks about it when the form still describes what is stored", () => {
    expect(testsStoredConnection(STORED, SAVED_DRAFT)).toBe(true);
  });

  it("asks about the typed pair once the connection differs", () => {
    expect(testsStoredConnection(STORED, { ...SAVED_DRAFT, address: "http://other:1" })).toBe(
      false,
    );
    expect(testsStoredConnection(STORED, { ...SAVED_DRAFT, apiKey: "typed" })).toBe(false);
    // Both generations hold a key here, so the refusal is the unsaved selection and not the
    // absence of a key to ask with.
    const bothKeyed: WhisparrSyncSettingsView = { ...STORED, v2: { ...STORED.v2, keyIsSet: true } };
    expect(testsStoredConnection(bothKeyed, draftFor(bothKeyed, "v2"))).toBe(false);
  });

  // The replacement behaviour is not part of the connection. A test that started asking about a
  // typed pair because a dropdown moved would refuse for want of a key nobody has.
  it("is not changed by an unsaved replacement behaviour", () => {
    expect(testsStoredConnection(STORED, { ...SAVED_DRAFT, upgradeBehavior: "replace" })).toBe(
      true,
    );
  });

  it("never asks about it when no key is stored to ask with", () => {
    const noKey: WhisparrSyncSettingsView = {
      ...STORED,
      v3: { ...STORED.v3, keyIsSet: false },
    };

    expect(testsStoredConnection(noKey, SAVED_DRAFT)).toBe(false);
    expect(testsStoredConnection(null, SAVED_DRAFT)).toBe(false);
  });
});
