/**
 * Pure, DOM-free logic behind the activity sub-page: the `ACTIVITY_SECTIONS` descriptor table that is the ONE
 * place a grouping is declared (the tab bar, the store's section map and the page's section render all derive
 * from it), the per-row label/glyph/tint descriptor maps (keyed on the exact camelCase wire strings), and the
 * pure state reducer that keeps loading / populated / empty / error as SEPARATE outcomes — so a Whisparr
 * outage is never rendered as an empty list. Kept import-free (no React, no DOM, no SDK) so the offline
 * `activity-logic` gate compiles it in isolation exactly like sceneStatusLogic.ts. The wire types live in
 * contracts.ts and are imported as types (erased at runtime), so this module stays offline-gate-clean.
 *
 * `ActivityHistoryEvent` is the camelCase MIRROR of the C# enum's JsonStringEnumConverter output, PINNED to
 * exactly grabbed/imported/failed. Any drift from the wire casing means the label map keys on the wrong
 * literal and rows blank — this gate is that drift check.
 */

import type { ActivityHistoryEvent, ActivityQueueState } from "../contracts";

/**
 * The StatusPill tint literals this surface uses. A local mirror of the shared `StatusPillVariant` union kept
 * here so the logic module stays import-free; the `.tsx` passes these strings to the real `StatusPill`, where
 * `tsc` checks assignability to the shared type (the compile-time drift guard for the tint side).
 */
export type ActivityPillVariant = "accent" | "amber" | "red" | "green" | "gray";

/** A row's status display descriptor: the label, a lucide glyph NAME the view resolves to a component, and the pill tint. Status never rides on color alone (glyph + label). */
export interface ActivityGlyphMeta {
  label: string;
  iconKey: string;
  variant: ActivityPillVariant;
}

/** Back-compat alias — the History event descriptor is the same glyph+label+tint triple every section uses. */
export type HistoryEventMeta = ActivityGlyphMeta;

/**
 * Each history event → its label, lucide glyph name, and StatusPill tint: grabbed is an accent Download,
 * imported a green CheckCircle2, failed a red XCircle. Keyed on the
 * exact camelCase wire strings and nothing else.
 */
export const HISTORY_EVENT_META: Record<ActivityHistoryEvent, ActivityGlyphMeta> = {
  grabbed: { label: "Grabbed", iconKey: "Download", variant: "accent" },
  imported: { label: "Imported", iconKey: "CheckCircle2", variant: "green" },
  failed: { label: "Failed", iconKey: "XCircle", variant: "red" },
};

/**
 * Each queue state → its label, lucide glyph name, and StatusPill tint: downloading is an accent Download,
 * queued a gray Clock, importing a green Download, warning an amber
 * AlertTriangle, failed a red XCircle. Keyed on the exact camelCase wire strings the C# `ActivityQueueState`
 * emits and nothing else — this map is the FE half of the queue-vocabulary drift gate.
 */
export const QUEUE_STATE_META: Record<ActivityQueueState, ActivityGlyphMeta> = {
  downloading: { label: "Downloading", iconKey: "Download", variant: "accent" },
  queued: { label: "Queued", iconKey: "Clock", variant: "gray" },
  importing: { label: "Importing", iconKey: "Download", variant: "green" },
  warning: { label: "Warning", iconKey: "AlertTriangle", variant: "amber" },
  failed: { label: "Failed", iconKey: "XCircle", variant: "red" },
};

/**
 * The single Wanted-row descriptor: a wanted scene is monitored-without-a-file, so its glyph is the accent
 * `Bookmark` — a wanted row carries no per-state variance, so one descriptor
 * covers the whole section.
 */
export const WANTED_META: ActivityGlyphMeta = {
  label: "Wanted",
  iconKey: "Bookmark",
  variant: "accent",
};

/**
 * Clamp an upstream progress value to a whole percent in [0, 100] for the determinate `ProgressBar`. A
 * null/undefined or non-finite input returns `undefined` — the indeterminate signal, distinct from a real 0%.
 */
export function clampProgress(percent: number | null | undefined): number | undefined {
  if (percent === null || percent === undefined || !Number.isFinite(percent)) return undefined;
  return Math.min(100, Math.max(0, Math.round(percent)));
}

/** The "load more" footer label for a server-paged section: "Showing {shown} of {total}". */
export function showingLabel(shown: number, total: number): string {
  return `Showing ${shown.toString()} of ${total.toString()}`;
}

/**
 * One activity grouping. Holds strings and keys only — no component and no hook, because the offline gate
 * compiles this module in isolation; the view resolves the per-key React halves from its own local record.
 */
export interface ActivitySectionDescriptor {
  /** The tab key, and the URL-hash literal a bookmark carries — a stable contract, never renamed. */
  key: string;
  label: string;
  /** Whether the tab carries a live count Badge. History has no meaningful "outstanding" total. */
  showsCount: boolean;
  /** The extension route the section pages, passed to the store's section factory. */
  route: string;
  emptyHeading: string;
  emptyBody: string;
}

/**
 * Every activity grouping, in tab-bar order — the single place one is declared. `as const` narrows `key` to
 * its literals so `ActivityTab` stays a union of the tab keys rather than widening to `string`.
 */
export const ACTIVITY_SECTIONS = [
  {
    key: "wanted",
    label: "Wanted",
    showsCount: true,
    route: "activity/wanted",
    emptyHeading: "Nothing wanted",
    // Whisparr tracks wanted PER SCENE, and monitoring an entity marks the container, not its scenes — so a
    // monitored studio with an empty wanted list is the normal state, not a fault.
    emptyBody:
      "Whisparr tracks wanted scene by scene, and monitoring a studio doesn't mark its scenes wanted on its own. Mark scenes wanted from a studio, performer, or tag's Missing tab and they'll show up here.",
  },
  {
    key: "queue",
    label: "Queue",
    showsCount: true,
    route: "activity/queue",
    emptyHeading: "Queue is empty",
    emptyBody: "Nothing is downloading right now. Grabs you start in Whisparr will appear here.",
  },
  {
    key: "history",
    label: "History",
    showsCount: false,
    route: "activity/history",
    emptyHeading: "No history yet",
    emptyBody: "Scenes that Whisparr grabs or imports will show up here.",
  },
] as const satisfies readonly ActivitySectionDescriptor[];

export type ActivityTab = (typeof ACTIVITY_SECTIONS)[number]["key"];

export const ACTIVITY_TAB_ORDER: readonly ActivityTab[] = ACTIVITY_SECTIONS.map((s) => s.key);

/** The four disjoint render outcomes for an activity section — empty and error are distinct by contract. */
export type ActivityStatus = "loading" | "populated" | "empty" | "error";

/**
 * The pure reducer every activity section renders from (History, Queue, Wanted alike). `loading` wins first;
 * then a real error signal (or a null rows payload, defensively) is `error`, NEVER `empty` — an outage must
 * not collapse into "nothing here"; an actually-empty resolved list is `empty`.
 */
export function activityStatus(input: {
  loading: boolean;
  error: boolean;
  rows: readonly unknown[] | null;
}): ActivityStatus {
  if (input.loading) return "loading";
  if (input.error || input.rows === null) return "error";
  return input.rows.length > 0 ? "populated" : "empty";
}
