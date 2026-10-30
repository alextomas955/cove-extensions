/**
 * Whether Whisparr is still building the entity's catalogue, read from the two figures the page
 * carries.
 *
 * Whisparr adds an entity's scenes over the minutes after it is told about the entity, so until
 * that finishes it lists fewer than the metadata source counts and the tab's list is short without
 * saying so.
 */

/** The two figures the sentence names, once there is something to say. */
export interface ScenesStillArriving {
  /** How many scenes Whisparr lists for the entity. */
  readonly listed: number;
  /** How many the metadata source counts in the same catalogue. */
  readonly counted: number;
}

/**
 * The figures to state, or null where there is nothing to say.
 *
 * Null where the source answered no figure, which states nothing about the catalogue and is a
 * different answer from a source counting zero.
 *
 * @param view the page's own counts of the whole catalogue, before any subtraction or narrowing
 */
export function scenesStillArriving(view: {
  instanceCatalogueCount: number;
  providerCatalogueCount: number | null;
}): ScenesStillArriving | null {
  const counted = view.providerCatalogueCount;
  if (counted === null || view.instanceCatalogueCount >= counted) return null;
  return { listed: view.instanceCatalogueCount, counted };
}
