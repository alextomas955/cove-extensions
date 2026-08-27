/**
 * The sentence shown in place of an entity name when the entity is gone.
 *
 * Extracted so it can be asserted without a DOM and without the host's component module, which has no
 * local implementation to import. The rule this holds: the label must state that the rule no longer
 * applies, and name the id it held, because that id is the only handle the user has left for deciding
 * whether to delete the row.
 */

/** The label for a rule key whose entity Cove no longer holds. */
export function orphanedRuleLabel(entityType: string, id: number): string {
  return `Deleted ${entityType} (was #${id}) — this rule no longer applies`;
}
