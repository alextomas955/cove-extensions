/**
 * Reads the member list off a table that is total over a union.
 *
 * A table typed `Record<Member, …>` fails the build when a member is added to the union, so its
 * keys are the whole set. A member list written out beside it carries no such check and can sit
 * short of the union indefinitely.
 */

/**
 * Every key of `table`, in the order it declares them.
 *
 * Declaration order holds because these keys are not array indices; a table keyed by digits would
 * come back in numeric order instead.
 */
export function membersOf<T extends string>(table: Readonly<Record<T, unknown>>): readonly T[] {
  return Object.keys(table) as T[];
}
