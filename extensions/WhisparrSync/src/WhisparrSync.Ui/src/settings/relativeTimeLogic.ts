/**
 * How a recorded instant reads: relative within a day, an absolute date beyond it.
 *
 * The absolute form uses a month table, not the platform locale formatter. A locale-dependent
 * rendering shows the same instant differently in two places and gives a test nothing fixed to
 * assert.
 */

export type InstantForm = "relative" | "absolute";

export interface InstantRendering {
  readonly form: InstantForm;
  readonly text: string;
}

const SECOND_MS = 1000;
const MINUTE_MS = 60 * SECOND_MS;
const HOUR_MS = 60 * MINUTE_MS;

// Anything this old or older reads as a date.
const DAY_MS = 24 * HOUR_MS;

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

function hoursAgo(count: number): string {
  return `${String(count)} hour${count === 1 ? "" : "s"} ago`;
}

/**
 * How `iso` reads as of `nowMs`, or `null` when the value is not an instant.
 *
 * An instant in the future reads as just now. A clock a few seconds ahead is the ordinary cause,
 * and a negative age is not worth reporting.
 */
export function describeInstant(iso: string, nowMs: number): InstantRendering | null {
  const at = Date.parse(iso);
  if (Number.isNaN(at)) {
    return null;
  }

  const elapsed = nowMs - at;
  if (elapsed >= DAY_MS) {
    // The reader's own calendar day, not UTC's. A date is actionable only in the reader's timezone.
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
    // "min" whatever the count, matching the copy.
    return { form: "relative", text: `${String(Math.floor(elapsed / MINUTE_MS))} min ago` };
  }
  return { form: "relative", text: hoursAgo(Math.floor(elapsed / HOUR_MS)) };
}
