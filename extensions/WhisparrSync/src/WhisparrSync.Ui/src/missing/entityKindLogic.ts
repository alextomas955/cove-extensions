/**
 * Which kind of entity page the tab is mounted on.
 *
 * One component is registered for all three page types, and the host passes it the entity id and a
 * navigate callback and nothing else, so the kind is read from the address.
 */
import type { WhisparrEntityKind } from "../wire/api";

// Host-owned literals: these belong to Cove's router, so there is no file here to read them from.
const SEGMENTS: Record<string, WhisparrEntityKind | undefined> = {
  studios: "studio",
  studio: "studio",
  performers: "performer",
  performer: "performer",
  tags: "tag",
  tag: "tag",
};

/**
 * The kind `pathname` names, or null for a route this tab does not recognise. Null rather than
 * a guess: a guess would read one entity's catalogue on another entity's page.
 */
export function readEntityKind(pathname: string): WhisparrEntityKind | null {
  const segments = pathname.split("/").filter((segment) => segment !== "");
  for (let at = 0; at < segments.length; at++) {
    const kind = SEGMENTS[segments[at].toLowerCase()];

    // The segment after the kind has to be the entity's own id, so a route merely containing
    // the word does not read as an entity page.
    if (kind !== undefined && /^\d+$/.test(segments[at + 1] ?? "")) {
      return kind;
    }
  }

  return null;
}
