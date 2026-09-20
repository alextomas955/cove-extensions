/**
 * The four refusal kinds and what each one offers the user to do about it.
 *
 * This is the product-wide vocabulary. A surface with its own narrower outcomes, such as the
 * connection test's decision table, keeps those beside itself and maps them onto these.
 */
import { CAP_UNAVAILABLE_ON_THIS_GENERATION, NOTHING_MISSING, PROVIDER_UNREACHABLE } from "./copy";

export type RefusalKind = "notConfigured" | "unreachable" | "versionCapability" | "nothingToDo";

export interface RefusalAffordances {
  /** Whether asking again could give a different answer. */
  readonly retry: boolean;
  /** Whether a setting would fix it, so the surface may name one. */
  readonly namesASetting: boolean;
}

export interface Refusal {
  /** Null where the sentence names a setting only the surface knows. */
  readonly sentence: string | null;
  readonly affordances: RefusalAffordances;
}

/**
 * Every kind's affordances and sentence. The record is total by type, so a kind added to the union
 * fails the build here.
 *
 * The version-capability kind offers neither: retrying asks the same instance the same question,
 * and no setting would enable it.
 */
const REFUSALS: Record<RefusalKind, Refusal> = {
  notConfigured: {
    sentence: null,
    affordances: { retry: false, namesASetting: true },
  },
  unreachable: {
    sentence: PROVIDER_UNREACHABLE,
    affordances: { retry: true, namesASetting: false },
  },
  versionCapability: {
    sentence: CAP_UNAVAILABLE_ON_THIS_GENERATION,
    affordances: { retry: false, namesASetting: false },
  },
  // Not an error: the check succeeded and the answer was nothing.
  nothingToDo: {
    sentence: NOTHING_MISSING,
    affordances: { retry: false, namesASetting: false },
  },
};

export const REFUSAL_KINDS: readonly RefusalKind[] = [
  "notConfigured",
  "unreachable",
  "versionCapability",
  "nothingToDo",
];

export function describeRefusal(kind: RefusalKind): Refusal {
  return REFUSALS[kind];
}
