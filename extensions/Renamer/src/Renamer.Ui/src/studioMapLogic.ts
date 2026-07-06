/**
 * Pure, DOM-free coercion between the backend's number-keyed `StudioDestinations` and the string-keyed
 * map the reusable `KeyValueMapEditor` works in. Kept import-free (no React, no DOM, no SDK) so the
 * offline test runner can compile it in isolation exactly like options.ts/entityPickerLogic.ts — the
 * same coercion the editor relies on is the coercion the offline suite covers.
 *
 * A studio destination keys on the stable studio id. The id must stay a NUMBER end to end so the
 * persisted map is value-equal with the backend `Record<number, string>` and with normalizeOptions'
 * own `numKeyStringMap` coercion; a string key would diverge from both.
 */

/**
 * Adapt a number-keyed destination map to the string-keyed shape `KeyValueMapEditor` consumes. JS
 * object keys are strings regardless, so this is the explicit, typed crossing of that boundary rather
 * than a silent cast.
 */
export function toStringKeyed(map: Record<number, string>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(map)) out[k] = v;
  return out;
}

/**
 * Adapt the editor's string-keyed map back to the backend `Record<number, string>`.
 *
 * Mirrors options.ts `numKeyStringMap`: keep only entries whose key is an integer and whose value is a
 * string, rebuilding a fresh plain object. A hand-edited/legacy blob can carry a non-integer key
 * ("x", "1.5") — dropping it here yields a safe shape value-equal with the backend coercion rather
 * than propagating a NaN key downstream.
 */
export function fromStringKeyed(map: Record<string, string>): Record<number, string> {
  const out: Record<number, string> = {};
  for (const [k, v] of Object.entries(map)) {
    const n = Number(k);
    if (Number.isInteger(n) && typeof v === "string") out[n] = v;
  }
  return out;
}
