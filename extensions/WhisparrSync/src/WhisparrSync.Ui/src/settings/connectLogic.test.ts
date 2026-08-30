import { describe, expect, it } from "vitest";

import { CAP_UNAVAILABLE_ON_THIS_GENERATION } from "../common/ui/copy";
import {
  affordancesForKind,
  isAddressEdit,
  normaliseAddress,
  NO_REFUSAL_VALUES,
  REFUSAL_KINDS,
  sentenceForKind,
  valuesOf,
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

  it("reports an edit for a change that does", () => {
    expect(isAddressEdit("http://whisparr:6969", "http://whisparr:6970")).toBe(true);
    expect(isAddressEdit("http://whisparr:6969", "https://whisparr:6969")).toBe(true);
    expect(isAddressEdit("http://whisparr:6969", "")).toBe(true);
  });
});
