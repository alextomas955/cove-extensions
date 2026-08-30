/**
 * Pure, DOM-free rules for the connect surface: which sentence a refusal reads as, which affordances
 * it offers, and when a typed address stops being the one a result describes.
 *
 * Relative imports only, so this module runs with no environment and needs no doubles. The wire types
 * arrive as `import type`, which erases at runtime and so takes nothing with it.
 */
import type { ConnectionFailureKind, ConnectionTestView } from "../wire/api";
import {
  CONNECT_KEY_REJECTED,
  CONNECT_NOT_CONFIGURED,
  connectNotTheWhisparrApiSentence,
  connectUnreachableSentence,
  connectVersionNotManagedSentence,
} from "../common/ui/copy";

/** Every outcome except the one that is not a refusal. */
export type RefusalKind = Exclude<ConnectionFailureKind, "connected">;

/** The refusal kinds, in the order the decision table reaches them. */
export const REFUSAL_KINDS: readonly RefusalKind[] = [
  "notConfigured",
  "unreachable",
  "keyRejected",
  "notTheWhisparrApi",
  "versionNotManaged",
];

/** The values a refusal sentence may name, in the spelling the response carries them. */
export interface RefusalValues {
  address: string | null;
  version: string | null;
  otherApplication: string | null;
}

/** Nothing named. The starting point for a caller that has only a kind. */
export const NO_REFUSAL_VALUES: RefusalValues = {
  address: null,
  version: null,
  otherApplication: null,
};

/** What a refusal offers the user to do about it. */
export interface RefusalAffordances {
  /** Whether trying the same thing again could give a different answer. */
  readonly retry: boolean;
  /** Whether a setting would fix it, so the sentence may point at one. */
  readonly settingsLink: boolean;
}

/** The sentence <code>kind</code> reads as, naming whichever of <code>values</code> it uses. */
export function sentenceForKind(kind: RefusalKind, values: RefusalValues): string {
  switch (kind) {
    case "notConfigured":
      return CONNECT_NOT_CONFIGURED;
    case "unreachable":
      return connectUnreachableSentence(values.address);
    case "keyRejected":
      return CONNECT_KEY_REJECTED;
    case "notTheWhisparrApi":
      return connectNotTheWhisparrApiSentence(values.address);
    case "versionNotManaged":
      return connectVersionNotManagedSentence(values.version, values.otherApplication);
  }
}

/**
 * What <code>kind</code> offers.
 *
 * A version this product does not manage offers neither: retrying asks the same instance the same
 * question, and no setting would enable it, so advice to change one sends the user to the wrong place.
 */
export function affordancesForKind(kind: RefusalKind): RefusalAffordances {
  switch (kind) {
    case "notConfigured":
      return { retry: false, settingsLink: true };
    case "unreachable":
      return { retry: true, settingsLink: false };
    case "keyRejected":
      return { retry: false, settingsLink: true };
    case "notTheWhisparrApi":
      return { retry: false, settingsLink: true };
    case "versionNotManaged":
      return { retry: false, settingsLink: false };
  }
}

/** The values <code>view</code> carries, for the sentence its kind reads as. */
export function valuesOf(view: ConnectionTestView): RefusalValues {
  return {
    address: view.address,
    version: view.version,
    otherApplication: view.otherApplication,
  };
}

/**
 * The address with the parts that do not change where it points removed.
 *
 * Trimming and a trailing separator only. Nothing is added: an address with no scheme is left without
 * one, so it is refused and named rather than silently turned into a guess at what the user meant.
 */
export function normaliseAddress(raw: string): string {
  return raw.trim().replace(/\/+$/, "");
}

/**
 * Whether <code>next</code> points somewhere other than <code>previous</code>, and so whether a
 * result taken against the previous one still describes what the field now says.
 */
export function isAddressEdit(previous: string, next: string): boolean {
  return normaliseAddress(previous) !== normaliseAddress(next);
}
