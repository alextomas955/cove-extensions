/**
 * How a recorded instant reads: relative within a day, an absolute date beyond it, so a reading a
 * user could still act on is never displaced by a vague age.
 *
 * The absolute form is built from a month table rather than from the platform's locale formatter,
 * because a locale-dependent rendering makes the same instant read differently in two places and
 * gives a test nothing fixed to assert.
 *
 * Relative imports only, and there are none: this module runs with no environment.
 */

/** Which of the two forms an instant rendered in. */
export type InstantForm = "relative" | "absolute";

/** One rendered instant, and which form it took. */
export interface InstantRendering {
  readonly form: InstantForm;
  readonly text: string;
}

const SECOND_MS = 1000;
const MINUTE_MS = 60 * SECOND_MS;
const HOUR_MS = 60 * MINUTE_MS;

/** The boundary. Anything this old or older reads as a date. */
const DAY_MS = 24 * HOUR_MS;

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

function hoursAgo(count: number): string {
  return `${String(count)} hour${count === 1 ? "" : "s"} ago`;
}

/**
 * How `iso` reads as of `nowMs`, or `null` when the value cannot be read as an instant at all.
 *
 * An instant in the future reads as just now rather than as a negative age: a clock a few seconds
 * ahead of this one is the ordinary cause, and it is not a fact worth reporting.
 */
export function describeInstant(iso: string, nowMs: number): InstantRendering | null {
  const at = Date.parse(iso);
  if (Number.isNaN(at)) {
    return null;
  }

  const elapsed = nowMs - at;
  if (elapsed >= DAY_MS) {
    // The reader's own calendar day, not UTC's: a date is only actionable in the timezone the person
    // reading it lives in.
    const when = new Date(at);
    return {
      form: "absolute",
      text: `${String(when.getDate())} ${MONTHS[when.getMonth()]} ${String(when.getFullYear())}`,
    };
  }

  if (elapsed < MINUTE_MS) {
    return { form: "relative", text: "just now" };
  }
  if (elapsed < HOUR_MS) {
    // "min" whatever the count, which is the spelling the specification's own lines use.
    return { form: "relative", text: `${String(Math.floor(elapsed / MINUTE_MS))} min ago` };
  }
  return { form: "relative", text: hoursAgo(Math.floor(elapsed / HOUR_MS)) };
}
