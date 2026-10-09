import type { CallbackView, ConnectionSetting } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";
import {
  CALLBACK_NOT_CHECKED_YET,
  CALLBACK_NOT_REGISTERED,
  CALLBACK_REGISTERED_AND_DELIVERING,
  CALLBACK_REGISTERED_WITH_NO_EVENTS,
  NOTHING_TO_REGISTER,
  REGISTER_NEEDS_A_SAVED_ADDRESS,
  REGISTER_NEEDS_A_SAVED_KEY,
  REGISTRATION_IS_STILL_RUNNING,
} from "../common/ui/copy";

/**
 * The query parameter a hand-pasted address carries its secret in.
 *
 * Transcribed by hand from the server's own constant.
 */
const SECRET_QUERY_PARAMETER = "s";

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
 * Registered with no events is its own rendering, not a shade of registered. That combination is
 * the tell for an address mismatch, and plain success would hide it.
 */
type RegistrationRendering =
  "notCheckedYet" | "notRegistered" | "registeredWithNoEvents" | "registeredAndDelivering";

export interface RegistrationDescription {
  readonly rendering: RegistrationRendering;
  readonly sentence: string;
  readonly tone: "success" | "warning" | "muted";
}

// Total by type, so a rendering added to the union fails the build instead of compiling silently.
const DESCRIPTIONS: Record<RegistrationRendering, Omit<RegistrationDescription, "rendering">> = {
  notCheckedYet: { sentence: CALLBACK_NOT_CHECKED_YET, tone: "muted" },
  notRegistered: { sentence: CALLBACK_NOT_REGISTERED, tone: "warning" },
  registeredWithNoEvents: { sentence: CALLBACK_REGISTERED_WITH_NO_EVENTS, tone: "warning" },
  registeredAndDelivering: { sentence: CALLBACK_REGISTERED_AND_DELIVERING, tone: "success" },
};

/**
 * How the view's status reads.
 *
 * Never-checked and not-registered stay apart. "Not looked yet" and "not there" send a user
 * somewhere different.
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
 * Whether the callback can be registered at all, or a sentence saying why not.
 *
 * The lockdown reason outranks the rest: the others clear on their own, and this one stands until
 * somebody changes a Cove setting.
 */
export function registerRefusal(input: {
  readonly sharedReason: string | null;
  readonly registering: boolean;
  readonly address: string;
}): string | null {
  if (input.sharedReason !== null) return input.sharedReason;
  if (input.registering) return REGISTRATION_IS_STILL_RUNNING;
  return input.address.trim() === "" ? NOTHING_TO_REGISTER : null;
}

/**
 * Whether the standing note about the secret's place is shown.
 *
 * Keyed on where a delivery carried its secret, not on which generation answered. A hand-pasted
 * address carries it in the address whatever the generation supports.
 */
export function shouldShowLessPrivateFormNote(view: CallbackView): boolean {
  return view.lastEventSecretPosition === "address";
}

/** The sentence for a registration that could not be attempted because a setting is empty. */
export function missingSettingSentence(setting: ConnectionSetting): string | null {
  switch (setting) {
    case "address":
      return REGISTER_NEEDS_A_SAVED_ADDRESS;
    case "apiKey":
      return REGISTER_NEEDS_A_SAVED_KEY;
    case null:
      return null;
  }
}

/**
 * The read state the status renders through.
 *
 * Never-checked is the genuine zero here. It is not an answer saying the callback is absent.
 */
export function registrationRead(view: CallbackView | null, failed: boolean): AsyncRead {
  if (view === null) {
    return { reading: !failed, failed, hasContent: false };
  }
  const hasContent = view.status !== "notCheckedYet";
  // A failure is carried only where there is content to keep, as recordedRead does.
  return { reading: false, failed: hasContent && failed, hasContent };
}
