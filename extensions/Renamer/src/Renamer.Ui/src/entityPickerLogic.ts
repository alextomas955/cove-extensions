/**
 * Pure, DOM-free logic behind the studio/tag picker. Kept import-free (no React, no DOM, no SDK) so
 * the offline test runner can compile it in isolation exactly like options.ts/primitivesLogic.ts —
 * the same logic the picker renders on top of is the logic the offline suite covers.
 */

/**
 * The id+name reference row the picker consumes. Declared locally rather than imported from the SDK
 * so this module stays zero-import (the SDK's `request<EntityRef[]>` returns this same shape over the
 * wire); the backend projects studios/tags to exactly `{ id, name }`.
 */
export interface EntityRef {
  id: number;
  name: string;
}

/**
 * Case-insensitive substring filter over the fetched reference list. A blank/whitespace query yields
 * the whole list (a no-op filter, not an empty result); the query is trimmed; the server's name
 * ordering is preserved.
 */
export function filterEntities(query: string, entities: readonly EntityRef[]): EntityRef[] {
  const q = query.trim().toLowerCase();
  if (q.length === 0) return [...entities];
  return entities.filter((e) => e.name.toLowerCase().includes(q));
}

/**
 * Drop the rows whose stored value is already used elsewhere, so a picker supplying a NEW map key
 * never offers a studio/tag that already has a rule (which would let the user pick a duplicate that
 * the map editor then silently refuses). `valueOf` maps a row to the same value the picker would
 * store — the studio id for the studio map, the canonical name for the tag map — and `exclude` holds
 * the already-used keys in that same value space, compared by `===` against the mapped value.
 */
export function excludeEntities<V>(
  entities: readonly EntityRef[],
  exclude: readonly V[],
  valueOf: (entity: EntityRef) => V,
): EntityRef[] {
  if (exclude.length === 0) return [...entities];
  const used = new Set<V>(exclude);
  return entities.filter((e) => !used.has(valueOf(e)));
}

/**
 * Resolve a stored studio id to its display label against the fetched list.
 *
 * Returns the matching name when the id is still present, or the stale marker `#{id} (missing)` when
 * it is absent. A stored studio id that no longer exists in the library MUST stay visible and
 * removable — never blanked, never thrown — so a rule referencing a deleted studio is something the
 * user can see and clear, not a silently broken or crashing row. Studios key on the stable id (never
 * the display name), so an absent id is a real, expected state, not a programming error.
 */
export function resolveStudioLabel(id: number, entities: readonly EntityRef[]): string {
  const match = entities.find((e) => e.id === id);
  return match ? match.name : `#${id} (missing)`;
}

/**
 * Whether a stored studio id resolves to a live entry. The picker styles a resolved chip differently
 * from a stale one; a `false` result still renders (via {@link resolveStudioLabel}) and stays
 * removable.
 */
export function isResolvedStudioId(id: number, entities: readonly EntityRef[]): boolean {
  return entities.some((e) => e.id === id);
}

/**
 * Map a typed/selected tag name to the library's canonical spelling. Tags key on NAME
 * case-insensitively on the backend, so a near-miss casing must store the library's stored spelling
 * rather than the user's keystrokes — otherwise two casings of the same tag would diverge. Returns
 * the canonical name when the list contains a case-insensitive match, or the trimmed input otherwise
 * (a tag the picker has not seen is stored as typed).
 */
export function canonicalTagName(name: string, entities: readonly EntityRef[]): string {
  const trimmed = name.trim();
  const match = entities.find((e) => e.name.toLowerCase() === trimmed.toLowerCase());
  return match ? match.name : trimmed;
}
