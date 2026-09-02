import { describe, expect, it } from "vitest";

import type { ConnectionTestView, WhisparrSyncGenerationSettingsView } from "../wire/api";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { CAP_UNAVAILABLE_ON_THIS_GENERATION } from "../common/ui/copy";
import {
  affordancesForKind,
  clearsTransientResult,
  detectionOutcome,
  isAddressEdit,
  isGenerationChange,
  isNoOpSave,
  normaliseAddress,
  NO_REFUSAL_VALUES,
  recordedRead,
  REFUSAL_KINDS,
  sentenceForKind,
  testsStoredConnection,
  valuesForCard,
  valuesOf,
  type GenerationDraft,
  type RefusalValues,
} from "./connectLogic";

// The values a real refusal carries, so a sentence that names one is exercised naming it.
const NAMED: RefusalValues = {
  address: "http://whisparr-v3:6969",
  version: "9.9.9.9999",
  otherApplication: null,
};

describe("the four refusals are never collapsed", () => {
  it("gives every kind a sentence with something in it", () => {
    for (const kind of REFUSAL_KINDS) {
      expect(sentenceForKind(kind, NAMED).trim(), kind).not.toBe("");
    }
  });

  it("gives no two kinds the same sentence", () => {
    const sentences = REFUSAL_KINDS.map((kind) => sentenceForKind(kind, NAMED));
    expect(new Set(sentences).size).toBe(REFUSAL_KINDS.length);
  });

  it("still says something when the response named no values", () => {
    for (const kind of REFUSAL_KINDS) {
      expect(sentenceForKind(kind, NO_REFUSAL_VALUES).trim(), kind).not.toBe("");
    }
    expect(
      new Set(REFUSAL_KINDS.map((kind) => sentenceForKind(kind, NO_REFUSAL_VALUES))).size,
    ).toBe(REFUSAL_KINDS.length);
  });
});

describe("what each refusal names", () => {
  it("names the address that was tried when nothing answered", () => {
    expect(sentenceForKind("unreachable", NAMED)).toContain(NAMED.address);
  });

  it("names the address that answered as something else", () => {
    expect(sentenceForKind("notTheWhisparrApi", NAMED)).toContain(NAMED.address);
  });

  it("names the version found on a version it does not manage", () => {
    expect(sentenceForKind("versionNotManaged", NAMED)).toContain("9.9.9.9999");
  });

  it("names the other application from the value that instance sent", () => {
    const sentence = sentenceForKind("versionNotManaged", {
      address: NAMED.address,
      version: "5.14.0.9383",
      otherApplication: "Radarr",
    });

    expect(sentence).toContain("Radarr");
    expect(sentence).toContain("5.14.0.9383");
  });

  it("sends a turned-down key to the key rather than to the address", () => {
    const sentence = sentenceForKind("keyRejected", NAMED);

    expect(sentence).toContain("key");
    expect(sentence).not.toContain(NAMED.address);
  });
});

describe("what each refusal offers", () => {
  // A generation gap is fixed by neither: asking again asks the same instance the same question, and
  // no setting would enable it, so advice to change one sends the user to the wrong place.
  it("offers neither a retry nor a settings link for a version it does not manage", () => {
    expect(affordancesForKind("versionNotManaged")).toEqual({ retry: false, settingsLink: false });
  });

  it("offers a retry only when trying again could answer differently", () => {
    const retryable = REFUSAL_KINDS.filter((kind) => affordancesForKind(kind).retry);
    expect(retryable).toEqual(["unreachable"]);
  });

  it("gives every kind an answer rather than leaving one undefined", () => {
    for (const kind of REFUSAL_KINDS) {
      expect(typeof affordancesForKind(kind).retry, kind).toBe("boolean");
      expect(typeof affordancesForKind(kind).settingsLink, kind).toBe("boolean");
    }
  });
});

describe("the version-gap sentence", () => {
  // Transcribed by hand from the specification, character for character: lower-case v3, a space
  // before the parenthesis, a capital E inside it, and no trailing full stop. An expectation computed
  // from the constant would agree with whatever it said.
  it("reads exactly as specified", () => {
    expect(CAP_UNAVAILABLE_ON_THIS_GENERATION).toBe("Currently available on Whisparr v3 (Eros)");
  });

  it("never suggests migrating and is never a generic refusal", () => {
    const lowered = CAP_UNAVAILABLE_ON_THIS_GENERATION.toLowerCase();
    for (const forbidden of ["unsupported", "upgrade", "not supported", "migrat"]) {
      expect(lowered, forbidden).not.toContain(forbidden);
    }
  });
});

describe("reading the values off a response", () => {
  it("takes them in the spelling the response carries them", () => {
    expect(
      valuesOf({
        kind: "versionNotManaged",
        generation: null,
        capabilities: null,
        version: "5.14.0.9383",
        branch: "master",
        corroborated: null,
        otherApplication: "Radarr",
        address: "http://radarr:7878",
        missingSetting: null,
      }),
    ).toEqual({
      address: "http://radarr:7878",
      version: "5.14.0.9383",
      otherApplication: "Radarr",
    });
  });
});

describe("normalising an address", () => {
  it("drops surrounding space and trailing separators", () => {
    expect(normaliseAddress("  http://whisparr:6969/  ")).toBe("http://whisparr:6969");
    expect(normaliseAddress("http://whisparr:6969///")).toBe("http://whisparr:6969");
  });

  // Nothing is added. An address with no scheme stays without one, so it is refused and named rather
  // than turned into a guess at what the user meant.
  it("adds nothing that was not typed", () => {
    expect(normaliseAddress("whisparr:6969")).toBe("whisparr:6969");
    expect(normaliseAddress("")).toBe("");
  });
});

describe("deciding whether a result still describes the field", () => {
  it("reports no edit for a change that does not move the address", () => {
    expect(isAddressEdit("http://whisparr:6969", "http://whisparr:6969/")).toBe(false);
    expect(isAddressEdit("http://whisparr:6969", "  http://whisparr:6969 ")).toBe(false);
  });

  // Letter case does not move an address, and the server's own same-address rule folds it. A browser
  // that disagreed would discard a result the server would have kept, which reports a reading that
  // still holds as absent.
  it("reports no edit for a change of letter case", () => {
    expect(isAddressEdit("http://whisparr:6969", "HTTP://WHISPARR:6969")).toBe(false);
    expect(isAddressEdit("HTTP://HOST:6969", "http://host:6969/")).toBe(false);
  });

  it("reports an edit for a change that does", () => {
    expect(isAddressEdit("http://whisparr:6969", "http://whisparr:6970")).toBe(true);
    expect(isAddressEdit("http://whisparr:6969", "https://whisparr:6969")).toBe(true);
    expect(isAddressEdit("http://whisparr:6969", "")).toBe(true);
  });
});

describe("what retires the transient result", () => {
  it("is any change that moves the address, and no change that does not", () => {
    expect(clearsTransientResult("http://whisparr:6969", "http://whisparr:6969/")).toBe(false);
    expect(clearsTransientResult("http://whisparr:6969", "HTTP://WHISPARR:6969")).toBe(false);
    expect(clearsTransientResult("http://whisparr:6969", "http://whisparr:6970")).toBe(true);
  });

  // There is no address left for a result to be about, whatever the previous value was.
  it("is an emptied field, always", () => {
    expect(clearsTransientResult("http://whisparr:6969", "")).toBe(true);
    expect(clearsTransientResult("http://whisparr:6969", "   ")).toBe(true);
    expect(clearsTransientResult("", "")).toBe(true);
  });
});

/** A connected answer from `generation`, in the shape the response carries it. */
function connected(generation: "v3" | "v2", version: string): ConnectionTestView {
  return {
    kind: "connected",
    generation,
    capabilities: ["outOfBandCallbackSecret"],
    version,
    branch: "master",
    corroborated: true,
    otherApplication: null,
    address: "http://whisparr:6969",
    missingSetting: null,
  };
}

describe("a successful test whose generation is not the card's", () => {
  it("names the version found and carries no instruction to write anything", () => {
    const outcome = detectionOutcome(connected("v2", "2.0.0.1082"), "v3");

    expect(outcome).toEqual({ kind: "otherGeneration", detected: "v2", version: "2.0.0.1082" });
    // The shape itself holds nothing a caller could act on as a write. Transcribed by hand, so a
    // member added later has to be accounted for here before it can be acted on.
    expect(Object.keys(outcome ?? {}).sort()).toEqual(["detected", "kind", "version"]);
  });

  it("is distinguished from a success on the card's own generation", () => {
    expect(detectionOutcome(connected("v3", "3.3.8.1097"), "v3")).toEqual({ kind: "matchesCard" });
  });

  it("is not reported at all for an answer that did not connect", () => {
    const refused: ConnectionTestView = { ...connected("v3", "3.3.8.1097"), kind: "keyRejected" };

    expect(detectionOutcome(refused, "v3")).toBeNull();
  });
});

const NOTHING_STORED: WhisparrSyncGenerationSettingsView = {
  address: "",
  keyIsSet: false,
  recordedVersion: null,
  versionVerifiedAtUtc: null,
  lastReachableAtUtc: null,
};

const UNCHANGED_DRAFT: GenerationDraft = { address: "", apiKey: "", keyCleared: false };

describe("whether a save changes which generation is selected", () => {
  it("reports no change for a save selecting the generation already selected", () => {
    expect(isGenerationChange("v3", "v3")).toBe(false);
    expect(isGenerationChange("v2", "v2")).toBe(false);
  });

  it("reports a change for a save selecting the other one", () => {
    expect(isGenerationChange("v3", "v2")).toBe(true);
    expect(isGenerationChange("v2", "v3")).toBe(true);
  });

  // Nothing is known about what is selected until the read answers, so nothing is claimed about it.
  it("reports no change before the settings read has said which is selected", () => {
    expect(isGenerationChange(null, "v3")).toBe(false);
  });
});

describe("whether a save would write nothing", () => {
  it("is true for the same generation with an untouched form", () => {
    expect(isNoOpSave(NOTHING_STORED, "v3", "v3", UNCHANGED_DRAFT)).toBe(true);
  });

  it("is false once anything about it differs", () => {
    expect(isNoOpSave(NOTHING_STORED, "v3", "v2", UNCHANGED_DRAFT)).toBe(false);
    expect(
      isNoOpSave(NOTHING_STORED, "v3", "v3", { ...UNCHANGED_DRAFT, address: "http://a:1" }),
    ).toBe(false);
    expect(isNoOpSave(NOTHING_STORED, "v3", "v3", { ...UNCHANGED_DRAFT, apiKey: "k" })).toBe(false);
    expect(isNoOpSave(NOTHING_STORED, "v3", "v3", { ...UNCHANGED_DRAFT, keyCleared: true })).toBe(
      false,
    );
  });

  // Only letter case and a trailing separator differ, and neither moves the address.
  it("is true for an address that differs only in ways that do not move it", () => {
    const stored = { ...NOTHING_STORED, address: "http://whisparr:6969" };

    expect(
      isNoOpSave(stored, "v3", "v3", { ...UNCHANGED_DRAFT, address: "HTTP://WHISPARR:6969/" }),
    ).toBe(true);
  });
});

describe("whether Test asks about the stored connection", () => {
  const stored = { ...NOTHING_STORED, address: "http://whisparr:6969", keyIsSet: true };
  const asStored: GenerationDraft = {
    address: "http://whisparr:6969",
    apiKey: "",
    keyCleared: false,
  };

  // The browser never has the stored key to send back, so this is what lets a test run at all after
  // a save, and it is the only test whose answer may update the recorded version.
  it("asks about it when the form still describes what is stored", () => {
    expect(testsStoredConnection(stored, "v3", "v3", asStored)).toBe(true);
  });

  it("asks about the typed pair once the form differs", () => {
    expect(
      testsStoredConnection(stored, "v3", "v3", { ...asStored, address: "http://other:1" }),
    ).toBe(false);
    expect(testsStoredConnection(stored, "v3", "v3", { ...asStored, apiKey: "typed" })).toBe(false);
  });

  // The stored test asks about whichever generation is selected, so running one from the other card
  // would answer about an instance that card does not name.
  it("never asks about it from a card that is not the one in use", () => {
    expect(testsStoredConnection(stored, "v2", "v3", asStored)).toBe(false);
  });

  it("never asks about it when no key is stored to ask with", () => {
    expect(testsStoredConnection({ ...stored, keyIsSet: false }, "v3", "v3", asStored)).toBe(false);
    expect(testsStoredConnection(null, "v3", "v3", asStored)).toBe(false);
  });
});

describe("which generation's values a card shows", () => {
  it("takes them from the generation the card names, never the other", () => {
    const settings = {
      v3: { ...NOTHING_STORED, address: "http://three:6969" },
      v2: { ...NOTHING_STORED, address: "http://two:6969" },
    };

    expect(valuesForCard(settings, "v3")?.address).toBe("http://three:6969");
    expect(valuesForCard(settings, "v2")?.address).toBe("http://two:6969");
    expect(valuesForCard(null, "v3")).toBeNull();
  });
});

describe("the four-way read the recorded lines render through", () => {
  it("is reading before an answer and failed when the read failed", () => {
    expect(recordedRead(null, false)).toEqual({ reading: true, failed: false, hasContent: false });
    expect(recordedRead(null, true)).toEqual({ reading: false, failed: true, hasContent: false });
  });

  // A card whose instance has never been reached and whose version has never been read is a true
  // zero, not an absence of an answer.
  it("is the genuine zero for a generation nothing has ever reached", () => {
    expect(recordedRead(NOTHING_STORED, false).hasContent).toBe(false);
    expect(
      recordedRead({ ...NOTHING_STORED, lastReachableAtUtc: "2026-06-24T11:00:00Z" }, false)
        .hasContent,
    ).toBe(true);
    expect(
      recordedRead({ ...NOTHING_STORED, recordedVersion: "3.3.8.1097" }, false).hasContent,
    ).toBe(true);
  });

  // HON-7. A re-read that fails while the recorded lines are on screen has to reach the state
  // machine as a failure, or the outage can never be raised and the staleness is silent.
  it("carries a failed re-read through when there is content to keep", () => {
    const withContent = { ...NOTHING_STORED, recordedVersion: "3.3.8.1097" };
    const read = recordedRead(withContent, true);

    expect(read).toEqual({ reading: false, failed: true, hasContent: true });
    expect(deriveAsyncRegionState(read)).toEqual({ status: "content", outage: true });
  });

  // The control on the assertion above: with nothing to keep, a failed re-read must NOT become a
  // failure state, because content and empty render the same stored lines and the failure branch
  // replaces them with an error.
  it("does not turn a failed re-read into a failure when there is nothing to keep", () => {
    const read = recordedRead(NOTHING_STORED, true);

    expect(read).toEqual({ reading: false, failed: false, hasContent: false });
    expect(deriveAsyncRegionState(read)).toEqual({ status: "empty", outage: false });
  });
});
