import { describe, expect, it } from "vitest";

import type { CallbackView, RegistrationStatus } from "../wire/api";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import {
  carriesSecretInAddress,
  describeRegistration,
  LESS_PRIVATE_FORM_NOTE,
  missingSettingSentence,
  registrationRead,
  shouldShowLessPrivateFormNote,
} from "./registrationLogic";

// The three values the server declares, transcribed by hand from the C# enum. A list derived from
// the module under test would agree with whatever that module covered.
const STATUSES: RegistrationStatus[] = ["notCheckedYet", "registered", "notRegistered"];

// The two address forms in the shapes the server builds them, including the secret parameter's own
// name. Transcribed from CallbackAddress rather than read back off a response.
const HOST = "http://host.docker.internal:5073";
const ROUTE = "/api/extensions/com.alextomas955.whisparrsync/callback";
const COPYABLE = `${HOST}${ROUTE}?s=6d1f3a9c88b24e07`;
const REGISTERED = `${HOST}${ROUTE}`;

function callback(overrides: Partial<CallbackView>): CallbackView {
  return {
    generation: "v3",
    status: "notCheckedYet",
    copyableAddress: COPYABLE,
    registeredAddress: REGISTERED,
    secretTravelsOutOfBand: true,
    lastEventSecretPosition: null,
    missingSetting: null,
    refusal: null,
    ...overrides,
  };
}

describe("the three status values", () => {
  it("map to three distinct renderings", () => {
    const renderings = STATUSES.map(
      (status) => describeRegistration(callback({ status })).rendering,
    );

    expect(new Set(renderings).size).toBe(STATUSES.length);
  });

  it("each read as a sentence with something in it, and no two the same", () => {
    const sentences = STATUSES.map((status) => describeRegistration(callback({ status })).sentence);

    for (const sentence of sentences) {
      expect(sentence.trim()).not.toBe("");
    }
    expect(new Set(sentences).size).toBe(STATUSES.length);
  });

  // "We have not looked" and "it is not there" send a user somewhere different. A generation nothing
  // has asked yet must never borrow the answer that says the callback is absent.
  it("never lets the never-checked value read as not-registered", () => {
    const neverChecked = describeRegistration(callback({ status: "notCheckedYet" }));
    const absent = describeRegistration(callback({ status: "notRegistered" }));

    expect(neverChecked.rendering).toBe("notCheckedYet");
    expect(neverChecked.rendering).not.toBe(absent.rendering);
    expect(neverChecked.sentence).not.toBe(absent.sentence);
  });
});

describe("registered with nothing arriving", () => {
  // The tell for the whole class of address-confusion failure. Reading it as plain success is the
  // failure mode this rendering exists to prevent.
  it("has its own rendering, distinct from registered and delivering", () => {
    const silent = describeRegistration(
      callback({ status: "registered", lastEventSecretPosition: null }),
    );
    const delivering = describeRegistration(
      callback({ status: "registered", lastEventSecretPosition: "outOfBand" }),
    );

    expect(silent.rendering).toBe("registeredWithNoEvents");
    expect(delivering.rendering).toBe("registeredAndDelivering");
    expect(silent.sentence).not.toBe(delivering.sentence);
  });

  it("is not reported in the tone a working callback is", () => {
    expect(
      describeRegistration(callback({ status: "registered", lastEventSecretPosition: null })).tone,
    ).not.toBe("success");
  });
});

describe("the standing note about the less private form", () => {
  it("is visible while deliveries carry the secret in the address", () => {
    expect(
      shouldShowLessPrivateFormNote(
        callback({ status: "registered", lastEventSecretPosition: "address" }),
      ),
    ).toBe(true);
  });

  it("is invisible once a delivery has carried the secret out of band", () => {
    expect(
      shouldShowLessPrivateFormNote(
        callback({ status: "registered", lastEventSecretPosition: "outOfBand" }),
      ),
    ).toBe(false);
  });

  // Nothing has arrived, so there is nothing to tell a user about. A note raised before the first
  // delivery would be a warning about a fact not yet in evidence.
  it("is invisible before any delivery has arrived", () => {
    expect(
      shouldShowLessPrivateFormNote(
        callback({ status: "registered", lastEventSecretPosition: null }),
      ),
    ).toBe(false);
  });

  // A note that self-clears has nothing to dismiss, and offering a dismissal would let a user hide a
  // fact that is still true.
  it("offers no dismissal in its own wording", () => {
    const lowered = LESS_PRIVATE_FORM_NOTE.toLowerCase();
    for (const forbidden of ["dismiss", "hide", "don't show", "do not show"]) {
      expect(lowered, forbidden).not.toContain(forbidden);
    }
  });
});

describe("the two address forms", () => {
  // The copyable one has nowhere else to put the secret; the registered one carries it in a header,
  // which no proxy writes to an access log.
  it("keeps the secret in the copyable address and out of the registered one", () => {
    const view = callback({ status: "registered" });

    expect(carriesSecretInAddress(view.copyableAddress)).toBe(true);
    expect(carriesSecretInAddress(view.registeredAddress)).toBe(false);
  });

  it("reads no secret out of an address whose query holds something else", () => {
    expect(carriesSecretInAddress(`${REGISTERED}?other=1`)).toBe(false);
  });
});

describe("a registration that could not be attempted", () => {
  it("points at the setting that is actually empty rather than at the pair", () => {
    const address = missingSettingSentence("address");
    const key = missingSettingSentence("apiKey");

    expect(address).not.toBeNull();
    expect(key).not.toBeNull();
    expect(address).not.toBe(key);
    expect(missingSettingSentence(null)).toBeNull();
  });
});

describe("the four-way read the status renders through", () => {
  it("is reading before an answer arrives and failed when the read failed", () => {
    expect(registrationRead(null, false)).toEqual({
      reading: true,
      failed: false,
      hasContent: false,
    });
    expect(registrationRead(null, true)).toEqual({
      reading: false,
      failed: true,
      hasContent: false,
    });
  });

  // A status nothing has checked is the genuine zero: there is no answer about this instance yet,
  // which is not an answer saying the callback is absent.
  it("treats a never-checked answer as the genuine zero and any other as content", () => {
    expect(registrationRead(callback({ status: "notCheckedYet" }), false).hasContent).toBe(false);
    expect(registrationRead(callback({ status: "notRegistered" }), false).hasContent).toBe(true);
    expect(registrationRead(callback({ status: "registered" }), false).hasContent).toBe(true);
  });

  // HON-7, the same rule the recorded lines follow: a failed re-read over a status already on
  // screen has to reach the state machine as a failure, or the staleness is silent.
  it("carries a failed re-read through when there is a status to keep", () => {
    const read = registrationRead(callback({ status: "registered" }), true);

    expect(read).toEqual({ reading: false, failed: true, hasContent: true });
    expect(deriveAsyncRegionState(read)).toEqual({ status: "content", outage: true });
  });

  // The control: with nothing to keep, the failure branch would replace a genuine zero.
  it("does not turn a failed re-read into a failure when there is nothing to keep", () => {
    const read = registrationRead(callback({ status: "notCheckedYet" }), true);

    expect(read).toEqual({ reading: false, failed: false, hasContent: false });
    expect(deriveAsyncRegionState(read)).toEqual({ status: "empty", outage: false });
  });
});
