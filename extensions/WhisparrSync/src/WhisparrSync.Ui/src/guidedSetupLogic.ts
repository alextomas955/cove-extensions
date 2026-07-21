/**
 * Pure, DOM-free logic behind the guided-setup advisory banner (§3.5): it parses the untrusted
 * `/identity-health` response ({ totalScenes, unidentifiedScenes }), decides whether the banner shows
 * at all (quiet by default — nothing when N === 0), and builds the design-locked heading + summary text.
 * Extracted so the derivation is unit-testable without a DOM, and kept import-free (no React, no SDK) so
 * the offline gate compiles it in isolation exactly like syncHealthLogic.ts.
 *
 * The provider name ({provider}) is supplied by the caller rather than resolved here: SettingsPage owns
 * the connected version and single-sources the name through identityGuardLogic.providerNameFor, so this
 * module carries no provider literal of its own and cannot drift from the missing-id guard (C10).
 */

/** The library-wide identity-health count the banner reads from `/identity-health`. */
export interface IdentityHealth {
  /** Every Cove scene the count considered (whole library, counted under the System principal server-side). */
  totalScenes: number;
  /** How many of those have no connected-version provider id — the scenes Whisparr can't reconcile. */
  unidentifiedScenes: number;
}

export const NO_IDENTITY_PROBLEMS: IdentityHealth = {
  totalScenes: 0,
  unidentifiedScenes: 0,
};

/** The banner heading — an advisory, not an error (amber tone). Design-locked. */
export const GUIDED_SETUP_HEADING = "Some scenes can't be reconciled";

/** Read the identity-health shape from an untrusted `/identity-health` response; a healthy zero on anything malformed. */
export function identityHealthFromServer(raw: unknown): IdentityHealth {
  if (!raw || typeof raw !== "object") return NO_IDENTITY_PROBLEMS;
  const r = raw as Record<string, unknown>;
  const clamp = (v: unknown): number =>
    typeof v === "number" && Number.isFinite(v) && v > 0 ? Math.floor(v) : 0;
  return {
    totalScenes: clamp(r.totalScenes),
    unidentifiedScenes: clamp(r.unidentifiedScenes),
  };
}

/** True when at least one scene has no provider id — the only condition under which the banner renders. */
export function hasUnidentified(health: IdentityHealth): boolean {
  return health.unidentifiedScenes > 0;
}

/**
 * The advisory body, or null when there is nothing to fix (N === 0 → the banner renders nothing). Names the
 * count and the provider AND the next step ("Identify them in Cove"), so it reads as a guided fix, never an
 * error. `provider` is the connected version's identity-provider name, resolved by the caller.
 */
export function guidedSetupSummary(health: IdentityHealth, provider: string): string | null {
  if (!hasUnidentified(health)) return null;
  const n = health.unidentifiedScenes;
  const name = provider.length > 0 ? provider : "StashDB";
  return `${n} of your scenes have no ${name} id — Whisparr can't reconcile them. Identify them in Cove so they can sync.`;
}
