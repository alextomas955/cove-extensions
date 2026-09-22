import type { CallbackView, ConnectionSetting } from "../wire/api";
import type { AsyncRead } from "../common/ui/asyncRegionLogic";

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
export type RegistrationRendering =
  "notCheckedYet" | "notRegistered" | "registeredWithNoEvents" | "registeredAndDelivering";

export interface RegistrationDescription {
  readonly rendering: RegistrationRendering;
  readonly sentence: string;
  readonly tone: "success" | "warning" | "muted";
}

// Total by type, so a rendering added to the union fails the build instead of compiling silently.
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
 * What this Cove may do to itself when Whisparr calls the address.
 *
 * Said before the gesture, not after it. Whisparr checks a webhook by posting to it. A Cove holding
 * an owner account while sign-in is off can read that call as an instance reachable from outside
 * its own machine: it turns sign-in on, keeps it on, and signs out the person configuring this.
 * Whether it does depends on the address, where the call comes from and the host's own trusted-host
 * list, so this is a warning rather than a refusal.
 */
export const REGISTRATION_WOULD_LOCK_COVE_DOWN =
  "Sign-in is off for this Cove. Whisparr checks the address by calling it, and if that call reaches Cove from outside this machine, Cove protects itself by turning sign-in on and will sign you out. Set a password under Security & Access first if you want to avoid that.";

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
  if (input.registering) return "This registration is still running.";
  return input.address.trim() === "" ? "There is no callback address to register." : null;
}

/**
 * The standing note, shown while deliveries still carry the secret in the address.
 *
 * There is no dismiss control. The note goes when the fact goes.
 */
export const LESS_PRIVATE_FORM_NOTE =
  "Imports are arriving with the callback secret in the address, where proxies and load balancers record it. Registering again from here moves it out of the address.";

/**
 * Whether the note above is standing.
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
      return "Enter the Whisparr address above and save it before registering the callback.";
    case "apiKey":
      return "Enter the Whisparr API key above and save it before registering the callback.";
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
