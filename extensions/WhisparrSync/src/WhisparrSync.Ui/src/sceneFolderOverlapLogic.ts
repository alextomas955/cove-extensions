/**
 * Pure logic for the scene-folder-format overlap advisory. The server (`/scene-folder-overlap`, v3/Eros only)
 * reports each Whisparr root whose trailing segment equals the Scene Folder Format's leading literal — the case
 * where Eros concatenates root + format into a doubled segment (`/data/media/scenes` + `scenes/…` →
 * `/data/media/scenes/scenes/…`, unfindable, every scene shows "Missing"). This module parses that untrusted shape
 * and derives the banner copy, extracted so it is unit-testable without a DOM. The server returns facts only
 * (root/prefix/suggestedRoot); the doubled-path display and the guidance sentence are built here.
 */

/** One flagged root: the offending Whisparr root, the doubled leading literal, and the fix one level above. */
export interface SceneFolderOverlap {
  /** The Whisparr root folder whose trailing segment doubles the format prefix, as the server supplied it. */
  root: string;
  /** The Scene Folder Format's leading literal that the root's trailing segment repeats (e.g. `scenes`). */
  prefix: string;
  /** The root one level above (trailing overlapping segment stripped) the user should set in Whisparr. */
  suggestedRoot: string;
}

/**
 * Read the overlap list from an untrusted `/scene-folder-overlap` response. Anything malformed — null, a
 * non-object, a missing/non-array `overlaps`, or an item missing/blanking any of the three string fields — yields
 * an empty list, so a bad payload shows no advisory (the same quiet-on-anything-off posture as the v2/failed read).
 */
export function sceneFolderOverlapsFromServer(raw: unknown): SceneFolderOverlap[] {
  if (!raw || typeof raw !== "object") return [];
  const overlaps = (raw as Record<string, unknown>).overlaps;
  if (!Array.isArray(overlaps)) return [];
  return overlaps.filter((o): o is SceneFolderOverlap => {
    if (!o || typeof o !== "object") return false;
    const r = o as Record<string, unknown>;
    return (
      typeof r.root === "string" &&
      r.root.length > 0 &&
      typeof r.prefix === "string" &&
      r.prefix.length > 0 &&
      typeof r.suggestedRoot === "string" &&
      r.suggestedRoot.length > 0
    );
  });
}

/** True when at least one root overlaps the format prefix — the condition that shows the advisory. */
export function hasSceneFolderOverlap(overlaps: SceneFolderOverlap[]): boolean {
  return overlaps.length > 0;
}

/** The concrete doubled path Eros produces, so the user sees the repeated segment rather than an abstraction. */
export function doubledPathDisplay(o: SceneFolderOverlap): string {
  return `${o.root}/${o.prefix}/…`;
}

/**
 * The advisory's one-sentence body: names the doubled path, then the fix. Phrased as guidance the user applies in
 * Whisparr (the extension cannot change Whisparr's config), and always contains the `suggestedRoot` literal so the
 * exact value to enter is on screen.
 */
export function sceneFolderOverlapSummary(o: SceneFolderOverlap): string {
  return `Whisparr will write scenes to ${doubledPathDisplay(o)} — the “${o.prefix}” segment is doubled, so Cove can’t find them. In Whisparr, set this root folder to ${o.suggestedRoot} (the level above).`;
}
