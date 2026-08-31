/**
 * Pure rules for the import callback: what its status reads as, which setting a refused registration
 * points at, and when the note about the less private form is standing.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles.
 */
import type { CallbackView, ConnectionSetting } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";

/**
 * The query parameter a hand-pasted address carries its secret in.
 *
 * Transcribed by hand from the server's own constant. A value read back off the address it is used
 * to inspect would agree with whatever that address said.
 */
const SECRET_QUERY_PARAMETER = "s";

/** Whether <code>address</code> carries the callback secret in its query. */
export function carriesSecretInAddress(address: string): boolean {
  const separator = address.indexOf("?");
  if (separator === -1) {
    return false;
  }
  return new URLSearchParams(address.slice(separator + 1)).has(SECRET_QUERY_PARAMETER);
}

/**
 * The four ways the status reads.
 *
 * Registered-with-no-events is its own rendering rather than a shade of registered: that combination
 * is the tell for the whole class of address-confusion failure, and reading it as plain success is
 * the failure mode.
 */
export type RegistrationRendering =
  "notCheckedYet" | "notRegistered" | "registeredWithNoEvents" | "registeredAndDelivering";

/** How one rendering reads. */
export interface RegistrationDescription {
  readonly rendering: RegistrationRendering;
  readonly sentence: string;
  readonly tone: "success" | "warning" | "muted";
}

/**
 * Every rendering's sentence and tone.
 *
 * Total by TYPE, so a rendering added to the union fails this build rather than compiling with no
 * decision made about it.
 */
const DESCRIPTIONS: Record<RegistrationRendering, Omit<RegistrationDescription, "rendering">> = {
  notCheckedYet: {
    sentence: "Cove has not checked this instance for its callback yet.",
    tone: "muted",
  },
  notRegistered: {
    sentence: "Cove's callback is not registered on this instance.",
    tone: "warning",
  },
  registeredWithNoEvents: {
    sentence: "Registered, but no import has reached Cove through it yet.",
    tone: "warning",
  },
  registeredAndDelivering: {
    sentence: "Registered, and imports are reaching Cove through it.",
    tone: "success",
  },
};

/**
 * How <code>view</code>'s status reads.
 *
 * Never-checked and not-registered are kept apart: "we have not looked" and "it is not there" send a
 * user somewhere different, and the first is what a generation nothing has asked yet answers.
 */
export function describeRegistration(view: CallbackView): RegistrationDescription {
  switch (view.status) {
    case "notCheckedYet":
      return { rendering: "notCheckedYet", ...DESCRIPTIONS.notCheckedYet };
    case "notRegistered":
      return { rendering: "notRegistered", ...DESCRIPTIONS.notRegistered };
    case "registered":
      return view.lastEventSecretPosition === null
        ? { rendering: "registeredWithNoEvents", ...DESCRIPTIONS.registeredWithNoEvents }
        : { rendering: "registeredAndDelivering", ...DESCRIPTIONS.registeredAndDelivering };
  }
}

/**
 * The standing note, shown while deliveries are still carrying the secret where intermediaries record
 * it.
 *
 * There is no dismiss control anywhere: the note goes when the fact goes, so it never tells a user
 * about a problem they have already fixed.
 */
export const LESS_PRIVATE_FORM_NOTE =
  "Imports are arriving with the callback secret in the address, where proxies and load balancers record it. Registering again from here moves it out of the address.";

/**
 * Whether the note above is standing.
 *
 * Keyed on where a delivery ACTUALLY carried its secret rather than on which generation answered: an
 * instance reached through an address a user pasted by hand carries it in the address whatever the
 * generation is able to do.
 */
export function shouldShowLessPrivateFormNote(view: CallbackView): boolean {
  return view.lastEventSecretPosition === "address";
}

/** The sentence for a registration that could not be attempted because a setting is empty. */
export function missingSettingSentence(setting: ConnectionSetting): string | null {
  switch (setting) {
    case "address":
      return "Enter the Whisparr address above and save it before registering the callback.";
    case "apiKey":
      return "Enter the Whisparr API key above and save it before registering the callback.";
    case null:
      return null;
  }
}

/**
 * The four-way read the status renders through.
 *
 * A status nothing has checked is the genuine zero here - there is no answer about this instance yet,
 * which is not the same as an answer that says the callback is absent.
 */
export function registrationRead(view: CallbackView | null, failed: boolean): AsyncRead {
  if (view === null) {
    return { reading: !failed, failed, hasContent: false };
  }
  return { reading: false, failed: false, hasContent: view.status !== "notCheckedYet" };
}
