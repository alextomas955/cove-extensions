/**
 * How the sync section's numbers read, and what its count control says at each state.
 *
 * Relative imports only, and there are none: this module runs with no environment.
 */
import { ACTION_REFRESH, SYNC_COUNT, SYNC_IS_COUNTING } from "../common/ui/copy";

/**
 * `n` with a separator every three digits.
 *
 * Hand-written rather than `Intl.NumberFormat` or `toLocaleString`, for the reason the relative-time
 * module already states: a locale-dependent rendering makes the same value read differently in two
 * places and gives a test nothing fixed to assert. The server's own side of these sentences uses the
 * invariant culture, which produces this same grouping.
 */
export function groupThousands(n: number): string {
  const whole = Math.trunc(Math.abs(n));
  const digits = String(whole);
  let grouped = "";
  for (let i = 0; i < digits.length; i++) {
    if (i > 0 && (digits.length - i) % 3 === 0) grouped += ",";
    grouped += digits[i];
  }
  return n < 0 ? `-${grouped}` : grouped;
}

/** What the count control is called, and why it cannot be pressed. */
export interface CountControl {
  readonly name: string;
  /** Why the control is unavailable, or null when it is available. */
  readonly reason: string | null;
}

/**
 * The count control's name and its one reason.
 *
 * The name is one of two fixed constants and interpolates neither, so the control has no
 * variable-length text. A failed count leaves it pressable: the failure belongs in the preview
 * region, not on the control that would retry it.
 *
 * @param counting whether a count is in flight
 * @param hasResult whether a count has already answered
 */
export function countControl(counting: boolean, hasResult: boolean): CountControl {
  return {
    name: hasResult ? ACTION_REFRESH : SYNC_COUNT,
    reason: counting ? SYNC_IS_COUNTING : null,
  };
}
