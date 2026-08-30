/**
 * The four refusal kinds and what each one offers the user to do about it.
 *
 * The kinds are never collapsed into a single generic failure: a surface that cannot say which kind
 * applies says that instead of substituting a sentence that fits none of them.
 *
 * This is the product-wide vocabulary. A surface with its own narrower outcomes - the connection
 * test's decision table, say - keeps those beside itself and maps them onto these when it needs an
 * affordance.
 */
import { CAP_UNAVAILABLE_ON_THIS_GENERATION, NOTHING_MISSING, PROVIDER_UNREACHABLE } from "./copy";

/** The four kinds. A fifth is not representable. */
export type RefusalKind = "notConfigured" | "unreachable" | "versionCapability" | "nothingToDo";

/** What a refusal offers. Read-only because the entries below are shared constants. */
export interface RefusalAffordances {
  /** Whether asking again could give a different answer. */
  readonly retry: boolean;
  /** Whether a setting would fix it, so the surface may name one. */
  readonly namesASetting: boolean;
}

/** How one kind reads and what it offers. */
export interface Refusal {
  /**
   * The specified sentence, or <code>null</code> where the sentence names a setting only the
   * surface knows, which is the not-configured kind's whole affordance.
   */
  readonly sentence: string | null;
  readonly affordances: RefusalAffordances;
}

/**
 * Every kind's affordances and sentence.
 *
 * Total by TYPE, so a kind added to the union fails this build rather than compiling with no
 * decision made about it.
 *
 * The version-capability kind offers neither: retrying asks the same instance the same question, and
 * no setting would enable it, so advice to change one sends the user somewhere that cannot help.
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
  // Not an error at all: the check succeeded and the answer was nothing.
  nothingToDo: {
    sentence: NOTHING_MISSING,
    affordances: { retry: false, namesASetting: false },
  },
};

/** The kinds, so a caller that must cover all four cannot miss one. */
export const REFUSAL_KINDS: readonly RefusalKind[] = [
  "notConfigured",
  "unreachable",
  "versionCapability",
  "nothingToDo",
];

/** How <code>kind</code> reads and what it offers. */
export function describeRefusal(kind: RefusalKind): Refusal {
  return REFUSALS[kind];
}
