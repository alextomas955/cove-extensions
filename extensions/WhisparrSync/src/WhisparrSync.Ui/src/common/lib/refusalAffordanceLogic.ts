/**
 * Composition and precedence for a control some guard disables. Import-free (no React, no DOM, no SDK, not even a
 * type import) so the offline gate compiles it standalone, exactly like configGuardLogic.ts. It holds no copy —
 * every reason is supplied by the caller.
 *
 * The invariant it exists to hold: a reason is ADDED to a control's name, never substituted for it. An
 * `aria-label` that is the bare reason leaves the control unnameable to anyone not looking at it — a screen
 * reader announces a paragraph of advice and never says which control it belongs to.
 */

/** Which kind of guard refused. `capability` is a fact about the connected generation, `identity` about the entity. */
export type RefusalCause = "capability" | "identity" | "configuration";

/** What a set-level notice is saying: a failure a retry might clear, or a standing property of the instance. */
export type SetNoticeKind = "outage" | "permanent";

export interface SetNotice {
  kind: SetNoticeKind | null;
  /**
   * Whether the notice may carry a retry control. A permanent refusal must not: a button whose only outcome
   * is the same refusal presents a standing limitation as a transient failure, and the user cannot tell the
   * difference from the sentence alone once a control invites them to try.
   */
  offersRetry: boolean;
}

/**
 * Which set-level notice a read's outcome flags call for, and whether it may offer a retry.
 *
 * A permanent refusal wins over an outage when both are somehow set: the stronger claim is that the instance
 * cannot answer at all, and layering a retry over that is the defect this decides once instead of at each
 * render site.
 */
export function setNotice(input: { outage: boolean; permanentRefusal: boolean }): SetNotice {
  if (input.permanentRefusal) {
    return { kind: "permanent", offersRetry: false };
  }
  return input.outage ? { kind: "outage", offersRetry: true } : { kind: null, offersRetry: false };
}

/**
 * Between a control's own name and an appended reason. Exported rather than inlined so a test or a live driver can
 * split a composed accessible name back into its two halves deterministically.
 */
export const REASON_SEPARATOR = " — ";

export interface GuardedControlInput {
  /** The control's own accessible name — what it is, before why it is dimmed. */
  name: string;
  enabledTitle?: string | null;
  /** Disables without refusing, so it contributes no reason. */
  busy?: boolean;
  capabilityReason?: string | null;
  identityReason?: string | null;
  configurationReason?: string | null;
}

export interface GuardedControlAffordance {
  disabled: boolean;
  /**
   * `undefined` rather than `null` on purpose: this spreads straight onto a DOM prop, where `undefined` omits the
   * attribute and `null` renders an empty one.
   */
  title: string | undefined;
  ariaLabel: string;
  reason: string | null;
  cause: RefusalCause | null;
}

/** A blank or whitespace-only string is absent, not a reason — it would compose a dangling separator. */
function stated(value: string | null | undefined): string | null {
  return typeof value === "string" && value.trim() !== "" ? value : null;
}

/**
 * `name` with `reason` appended behind {@link REASON_SEPARATOR}, or `name` alone when there is no reason.
 *
 * Idempotent: composing an already-composed name returns it unchanged. A blank `name` is a programming error and
 * yields the blank name rather than a bare reason, because a reason standing in for a name is the whole defect
 * this module rules out.
 */
export function appendReason(name: string, reason: string | null | undefined): string {
  const stem = stated(reason);
  if (stem === null || name.trim() === "") {
    return name;
  }
  const tail = `${REASON_SEPARATOR}${stem}`;
  return name.endsWith(tail) ? name : `${name}${tail}`;
}

/**
 * The `disabled` / `title` / accessible-name trio for one guarded control, and which guard won.
 *
 * Precedence is capability, then identity, then configuration. A verb the connected generation does not offer
 * cannot be restored by changing a setting, so a configuration reason shown there is advice that would change
 * nothing; an entity with no remote id is the same case one step down. The order is the one
 * `discovery/MissingSceneCard.tsx` and `scene/WhisparrScenePanel.tsx` already keep between their `title` and
 * their branch chain.
 */
export function guardedControl(input: GuardedControlInput): GuardedControlAffordance {
  const capability = stated(input.capabilityReason);
  const identity = stated(input.identityReason);
  const configuration = stated(input.configurationReason);

  const reason = capability ?? identity ?? configuration;
  const cause: RefusalCause | null =
    capability !== null
      ? "capability"
      : identity !== null
        ? "identity"
        : configuration !== null
          ? "configuration"
          : null;

  return {
    disabled: input.busy === true || reason !== null,
    title: reason ?? stated(input.enabledTitle) ?? undefined,
    ariaLabel: appendReason(input.name, reason),
    reason,
    cause,
  };
}
