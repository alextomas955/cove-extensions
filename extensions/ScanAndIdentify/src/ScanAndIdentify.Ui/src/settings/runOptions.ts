/**
 * The settings panel's vocabulary for the saved run options.
 *
 * The options themselves are the generated wire contract. The backend owns the defaults, the stored
 * spelling and what an unreadable blob falls back to; this panel edits what `GET /options` hands it
 * and sends the result back. Per-member documentation stays on the C# record.
 */

import type * as Wire from "../wire/api";

/**
 * A generated wire shape with the optional markers dropped.
 *
 * The generator marks every member of a C# record with a default initializer optional, because a
 * request may omit it. A response never does - the server writes all of them - and a panel control
 * needs a value, so the response side is modeled as complete. A backend test holds the server to it.
 */
type Complete<T> = T extends readonly (infer E)[]
  ? Complete<E>[]
  : T extends object
    ? { [K in keyof T]-?: Complete<T[K]> }
    : T;

export type RunOptions = Complete<Wire.RunOptions>;

/** One switch as the form draws it. */
export interface RunOptionField {
  readonly key: keyof RunOptions;
  readonly label: string;
  readonly helper?: string;
}

/**
 * The switches the form renders, in the order it renders them. The labels are Cove's own words for
 * the same switches, so a user meets one vocabulary rather than two.
 */
export const RUN_OPTION_FIELDS: readonly RunOptionField[] = [
  {
    key: "backfillFingerprints",
    label: "Fill in missing fingerprints",
    helper: "Identify matches on a fingerprint, so a file without one can never be identified.",
  },
];
