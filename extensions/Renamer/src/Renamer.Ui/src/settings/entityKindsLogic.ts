/**
 * How one edit in the "Entity kinds" card changes the stored per-kind map.
 *
 * Pure, so the rule can be read and tested without a DOM: a kind at its defaults is ABSENT from the
 * map rather than stored as an entry saying nothing, which is what keeps turning a setting on and
 * back off from leaving the blob different than it started.
 */
import type { Destination, KindOptions, RenamableKind } from "./options";

export type KindMap = Partial<Record<RenamableKind, KindOptions>>;

/** What the kind's settings are when the map holds no entry for it: renamed, no folder of its own. */
export function kindSettings(map: KindMap, kind: RenamableKind): KindOptions {
  return map[kind] ?? { Enabled: true, Destination: null };
}

/** The map after setting one kind's enabled flag and destination. */
export function nextKinds(
  map: KindMap,
  kind: RenamableKind,
  enabled: boolean,
  destination: Destination | null,
): KindMap {
  const { [kind]: _dropped, ...rest } = map;
  return enabled && destination === null
    ? rest
    : { ...rest, [kind]: { Enabled: enabled, Destination: destination } };
}
