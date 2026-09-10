/**
 * Which kind of entity page the tab is mounted on.
 *
 * One component is registered for all three page types and the host passes it the entity id and a
 * navigate callback and nothing else, so the kind is read from the address rather than from a prop
 * that does not exist.
 */
import type { WhisparrEntityKind } from "../wire/api";

/**
 * The host's own route segment for each kind.
 *
 * Host-owned literals: these belong to Cove's router rather than to this repository, so there is no
 * file here to read them from.
 */
const SEGMENTS: Record<string, WhisparrEntityKind | undefined> = {
  studios: "studio",
  studio: "studio",
  performers: "performer",
  performer: "performer",
  tags: "tag",
  tag: "tag",
};

/**
 * The kind `pathname` names, or null for a route this tab does not recognise.
 *
 * Null is answered rather than guessed. A guess would read one entity's catalogue on another
 * entity's page, and the surface states the refusal instead.
 *
 * @param pathname the address the tab is mounted at
 */
export function readEntityKind(pathname: string): WhisparrEntityKind | null {
  const segments = pathname.split("/").filter((segment) => segment !== "");
  for (let at = 0; at < segments.length; at++) {
    const kind = SEGMENTS[segments[at].toLowerCase()];

    // The segment after the kind has to be the entity's own id, so a route merely CONTAINING the
    // word does not read as an entity page.
    if (kind !== undefined && /^\d+$/.test(segments[at + 1] ?? "")) {
      return kind;
    }
  }

  return null;
}
