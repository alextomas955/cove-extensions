/**
 * The settings panel's vocabulary for the saved run options.
 *
 * The options themselves are the generated wire contract. The backend owns the defaults, the stored
 * spelling and what an unreadable blob falls back to; this panel edits what `GET /options` hands it
 * and sends the result back. Per-member documentation stays on the C# record.
 */

import type * as Wire from "../wire";
import type { Complete } from "../common/lib/wire";

export type RunOptions = Complete<Wire.RunOptions>;

/** The blocks the form draws, in the order a run executes them. */
export type RunOptionGroup = "Scan" | "Fingerprints" | "Identify" | "Images and previews";

export const RUN_OPTION_GROUPS: readonly RunOptionGroup[] = [
  "Scan",
  "Fingerprints",
  "Identify",
  "Images and previews",
];

/** One switch as the form draws it. */
export interface RunOptionField {
  readonly key: keyof RunOptions;
  readonly group: RunOptionGroup;
  readonly label: string;
  readonly helper?: string;
}

/**
 * The switches the form renders, in the order it renders them. The labels are Cove's own words for
 * the same switches, so a user meets one vocabulary rather than two.
 */
export const RUN_OPTION_FIELDS: readonly RunOptionField[] = [
  {
    key: "rescan",
    group: "Scan",
    label: "Force rescan (ignore mtime)",
  },
  {
    key: "backfillFingerprints",
    group: "Fingerprints",
    label: "Fill in missing fingerprints",
    helper: "Turn it off for a library that already has a fingerprint on every file.",
  },
  {
    key: "createMissingEntities",
    group: "Identify",
    label: "Let Identify create new performers, studios and tags",
  },
  {
    key: "markOrganized",
    group: "Identify",
    label: "Mark an identified video as organized",
  },
  {
    key: "thumbnails",
    group: "Images and previews",
    label: "Thumbnails / screenshots",
  },
  {
    key: "previews",
    group: "Images and previews",
    label: "Video previews",
  },
  {
    key: "sprites",
    group: "Images and previews",
    label: "Sprite sheets",
  },
  {
    key: "vrStereo",
    group: "Images and previews",
    label: "VR 3D covers and previews",
    helper:
      "The stereoscopic cover and preview a headset shows. The flat ones come from the switches above.",
  },
  {
    key: "imageThumbnails",
    group: "Images and previews",
    label: "Image thumbnails",
  },
  {
    key: "galleryThumbnails",
    group: "Images and previews",
    label: "Gallery cover thumbnails",
  },
];
