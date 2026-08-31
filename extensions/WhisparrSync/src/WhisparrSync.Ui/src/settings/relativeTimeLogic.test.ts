import { describe, expect, it } from "vitest";

import { describeInstant } from "./relativeTimeLogic";

// Hand-transcribed rather than imported. An expectation computed from the module's own constant
// would agree with whatever the module said the boundary was.
const A_DAY_MS = 24 * 60 * 60 * 1000;
const A_MINUTE_MS = 60 * 1000;

const NOW = Date.parse("2026-06-24T12:00:00Z");

/** An instant `ago` milliseconds before {@link NOW}, in the spelling the wire carries. */
function agoIso(ago: number): string {
  return new Date(NOW - ago).toISOString();
}

describe("the day boundary", () => {
  it("renders a time just inside a day as a relative age", () => {
    const rendered = describeInstant(agoIso(A_DAY_MS - A_MINUTE_MS), NOW);

    expect(rendered?.form).toBe("relative");
    expect(rendered?.text).toBe("23 hours ago");
  });

  it("renders a time just outside a day as a date", () => {
    // The month names are transcribed here rather than imported, and the calendar day comes from
    // `Date` rather than from the module under test, so the expectation is independent of it and
    // holds in whatever timezone the runner is in.
    const at = new Date(NOW - A_DAY_MS);
    const months = [
      "Jan",
      "Feb",
      "Mar",
      "Apr",
      "May",
      "Jun",
      "Jul",
      "Aug",
      "Sep",
      "Oct",
      "Nov",
      "Dec",
    ];
    const rendered = describeInstant(agoIso(A_DAY_MS), NOW);

    expect(rendered?.form).toBe("absolute");
    expect(rendered?.text).toBe(
      `${String(at.getDate())} ${months[at.getMonth()]} ${String(at.getFullYear())}`,
    );
  });

  // The two sides must not merely differ in wording: one is an age and the other is a date a user
  // could act on, and HON-10 exists because an age displacing a date is the failure.
  it("puts the two sides of the boundary in different forms", () => {
    const inside = describeInstant(agoIso(A_DAY_MS - 1), NOW);
    const outside = describeInstant(agoIso(A_DAY_MS + 1), NOW);

    expect(inside?.form).not.toBe(outside?.form);
  });
});

describe("the relative forms", () => {
  it("reads under a minute as just now", () => {
    expect(describeInstant(agoIso(59 * 1000), NOW)).toEqual({ form: "relative", text: "just now" });
  });

  // "4 min ago", transcribed by hand from the specification's own line, which does not pluralise it.
  it("counts minutes in the specification's own spelling", () => {
    expect(describeInstant(agoIso(A_MINUTE_MS), NOW)?.text).toBe("1 min ago");
    expect(describeInstant(agoIso(4 * A_MINUTE_MS), NOW)?.text).toBe("4 min ago");
  });

  it("counts hours, singular and plural", () => {
    expect(describeInstant(agoIso(60 * A_MINUTE_MS), NOW)?.text).toBe("1 hour ago");
    expect(describeInstant(agoIso(3 * 60 * A_MINUTE_MS), NOW)?.text).toBe("3 hours ago");
  });

  // A clock a few seconds ahead of this one is the ordinary cause, and a negative age is not a fact
  // worth reporting.
  it("reads an instant in the future as just now", () => {
    expect(describeInstant(agoIso(-30 * 1000), NOW)?.text).toBe("just now");
  });
});

describe("a value that is not an instant", () => {
  it("is reported as unreadable rather than rendered as a date", () => {
    expect(describeInstant("not a date", NOW)).toBeNull();
  });
});
