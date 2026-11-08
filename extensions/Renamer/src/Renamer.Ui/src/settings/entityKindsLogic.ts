/**
 * How one edit in the "Per kind" list changes the stored per-kind map. A kind at its defaults is
 * absent from the map rather than stored as an entry saying nothing, so turning a setting on and
 * back off leaves the blob as it started.
 */
import { RENAMABLE_KINDS, type Destination, type KindOptions, type RenamableKind } from "./options";

/**
 * The per-kind map as it actually arrives.
 *
 * The value admits null, which the generated wire type does not: `Kinds` is a C# dictionary whose
 * declared value is nullable, and openapi-typescript does not carry nullability on a dictionary
 * value, so the generated shape says the entry is always there. A stored blob can hold a present key
 * with no value - `{"Kinds":{"Text":null}}` - the store's non-null repair does not reach inside a
 * collection, and the server writes it straight back out. A backend test holds that to be true.
 */
export type KindMap = Partial<Record<RenamableKind, KindOptions | null>>;

/**
 * What the kind's settings are when the map holds nothing usable for it: renamed, no folder of its
 * own. A key that is absent and a key whose value is null mean the same thing - unlisted - which is
 * the reading the server takes too.
 */
export function kindSettings(map: KindMap, kind: RenamableKind): KindOptions {
  return map[kind] ?? { enabled: true, destination: null };
}

/**
 * The map after setting one kind's enabled flag and destination.
 *
 * What comes back carries no null entry, which is what lets a save send it as the wire shape. An
 * entry the server wrote as null means the kind is unlisted, and leaving the key out says the same
 * thing, so dropping it loses nothing.
 *
 * Keys are carried over from the map rather than from {@link RENAMABLE_KINDS}: a kind this panel
 * does not render would otherwise be deleted from the stored document by an edit to a different one.
 */
export function nextKinds(
  map: KindMap,
  kind: RenamableKind,
  enabled: boolean,
  destination: Destination | null,
): Record<string, KindOptions> {
  const kept: Record<string, KindOptions> = {};
  for (const [key, settings] of Object.entries(map)) {
    if (key !== kind && settings != null) kept[key] = settings;
  }
  return enabled && destination === null ? kept : { ...kept, [kind]: { enabled, destination } };
}

/** How many kinds are not renamed at all, and how many have a folder of their own. */
export function kindsSummary(map: KindMap): { excluded: number; ownFolder: number } {
  const settings = RENAMABLE_KINDS.map((kind) => kindSettings(map, kind));
  return {
    excluded: settings.filter((s) => !s.enabled).length,
    ownFolder: settings.filter((s) => s.enabled && s.destination !== null).length,
  };
}
