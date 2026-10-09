/**
 * Which cards a background run this browser started is still working through.
 *
 * A run over a selection acts on its scenes one at a time and can refuse any of them, so nothing
 * here is an outcome: it says only that the work is under way. What the instance ends up holding is
 * read when the run stops, and that read is what a card finally draws.
 */

/** The scenes one run covers, or every scene the narrowing in force covers. */
export type MissingRun =
  | { readonly kind: "scenes"; readonly providerSceneIds: readonly string[] }
  | { readonly kind: "everything" };

/** The run covering exactly `providerSceneIds`. */
export function runOver(providerSceneIds: readonly string[]): MissingRun {
  return { kind: "scenes", providerSceneIds: [...providerSceneIds] };
}

/** The run covering everything the narrowing in force reaches. */
export const RUN_OVER_EVERYTHING: MissingRun = { kind: "everything" };

/**
 * Whether `providerSceneId` is one the run is working through.
 *
 * A run over everything covers every card drawn, because the narrowing it was started under is the
 * one the page is drawn from.
 */
export function runCovers(run: MissingRun | null, providerSceneId: string): boolean {
  if (run === null) return false;
  return run.kind === "everything" || run.providerSceneIds.includes(providerSceneId);
}

/** Whether any run is under way, which is what the selection's own controls wait on. */
export function runIsUnderWay(run: MissingRun | null): boolean {
  return run !== null;
}
