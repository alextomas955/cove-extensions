/**
 * Which form a recorded instant takes: relative within a day, an absolute date beyond it. The
 * words each form reads in are the catalogue's.
 */
import { dateSentence, hoursAgo, JUST_NOW, minutesAgo } from "../common/ui/copy";

type InstantForm = "relative" | "absolute";

export interface InstantRendering {
  readonly form: InstantForm;
  readonly text: string;
}

const SECOND_MS = 1000;
const MINUTE_MS = 60 * SECOND_MS;
const HOUR_MS = 60 * MINUTE_MS;

// Anything this old or older reads as a date.
const DAY_MS = 24 * HOUR_MS;

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
      text: dateSentence(when.getDate(), when.getMonth(), when.getFullYear()),
    };
  }

  if (elapsed < MINUTE_MS) {
    return { form: "relative", text: JUST_NOW };
  }
  if (elapsed < HOUR_MS) {
    return { form: "relative", text: minutesAgo(Math.floor(elapsed / MINUTE_MS)) };
  }
  return { form: "relative", text: hoursAgo(Math.floor(elapsed / HOUR_MS)) };
}
