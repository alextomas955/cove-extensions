/**
 * Pure, DOM-free logic behind the version-aware missing-id guard (§3.6): the single-sourced
 * `providerNameFor(version)` mapping (StashDB on v3, ThePornDB on v2) and the one missing-id message
 * builder every surface reads, so the guard wording — and the provider name it cites — can never drift
 * between the scene panel, the monitor button, and any bulk chooser (C10). Kept import-free (no React, no
 * DOM, no SDK) so the offline gate compiles it in isolation exactly like monitorLogic.ts.
 *
 * The guard is a GUIDED FIX, not an error: it names the cause AND the next step ("identify it in Cove
 * first"), and it never implies the user must migrate Whisparr versions — v2 and v3 are both first-class.
 */

/** The entity a guard message can target — the three Whisparr-pushable Cove entities. */
export type WhisparrEntity = "scene" | "performer" | "studio";

/**
 * The connected Whisparr version's identity-provider name — `ThePornDB` on v2, `StashDB` on v3 (the
 * default). Single-sourced here so no call site ever hardcodes a provider literal. Accepts the version as a
 * number or a numeric string; anything that is not 2 is treated as v3 (StashDB), the modern default.
 */
export function providerNameFor(version: number | string | null | undefined): string {
  const v = typeof version === "string" ? Number.parseInt(version, 10) : version;
  return v === 2 ? "ThePornDB" : "StashDB";
}

/**
 * The design-locked missing-id message: `This {entity} has no {provider} id — identify it in Cove first so
 * Whisparr can match it.` The provider name is supplied by the caller (the server-returned `provider` field
 * on the NO_STASHDB_IDENTITY response, or {@link providerNameFor}); when it is absent it falls back to the v3
 * default so the message is never provider-less.
 */
export function missingIdMessage(
  entity: WhisparrEntity,
  provider: string | null | undefined,
): string {
  const name =
    provider !== null && provider !== undefined && provider !== "" ? provider : "StashDB";
  return `This ${entity} has no ${name} id — identify it in Cove first so Whisparr can match it.`;
}
