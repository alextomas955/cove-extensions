/**
 * A row's warning badges, derived from its `status` and its advisory flags (`suffixed`, `sanitized`,
 * `inFlightPathOverflow`).
 */
import type { RenamerStatus } from "../../wire/api";
import { classifyItem, inFlightOverflowLabel } from "./dryRunLogic";

type Variant = "amber" | "gray" | "red";

/** One rendered pill. Read-only because the status badges are shared constants. */
export interface Badge {
  readonly label: string;
  readonly variant: Variant;
}

/** What a badge is derived from. Structural, so a `/scan-rows` row fits it. */
export interface Badgeable {
  status: RenamerStatus;
  suffixed: boolean;
  sanitized: boolean;
  inFlightPathOverflow: boolean;
}

/**
 * The badge each status earns, or null for one that earns none. Keyed on the generated wire union, so a
 * status the server grows fails this build until it is given a decision here.
 */
const STATUS_BADGES: Record<RenamerStatus, Badge | null> = {
  // The acting statuses earn no badge of their own; the advisory flags qualify them instead.
  rename: null,
  move: null,
  noOp: { label: "No change needed", variant: "gray" },
  skipGated: { label: "Needs a required field", variant: "amber" },
  skipCollision: { label: "Name conflict", variant: "amber" },
  skipExcluded: { label: "An exclude rule matched", variant: "amber" },
  skipRuleTimedOut: { label: "A regex rule timed out", variant: "amber" },
  skipLocked: { label: "File in use", variant: "amber" },
  skipMissingSource: { label: "File missing on disk", variant: "amber" },
  failed: { label: "Failed — rolled back", variant: "red" },
  skipUnanchored: { label: "File is outside your Cove library", variant: "amber" },
  skipRootMissing: {
    label: "The rule's destination is no longer a library path",
    variant: "amber",
  },
  skipNotAllowed: { label: "Destination outside its own root", variant: "amber" },
  skipTooLong: { label: "Path too long", variant: "amber" },
  skipPermissionDenied: { label: "Permission denied", variant: "amber" },
  // Red: the copy read back different, so the destination or the transport is suspect.
  skipVerifyFailed: { label: "Copy did not verify", variant: "red" },
  // Gray: a clean stop on shutdown is not a defect.
  skipCancelled: { label: "Cancelled", variant: "gray" },
  // Assigned at move time, after every row is built, so no row carries it.
  skipNoSpace: null,
};

// A status the running server grew after this bundle shipped is still counted as attention, so it is
// labelled rather than left bare. The widened lookup is what lets the miss be expressed at all.
const UNKNOWN_STATUS: Badge = { label: "Unrecognised status", variant: "amber" };

function statusBadge(status: string): Badge | null {
  const badges = STATUS_BADGES as Record<string, Badge | null | undefined>;
  return status in badges ? (badges[status] ?? null) : UNKNOWN_STATUS;
}

/**
 * Map an item to its badges, one per warning kind. An acting row also reads its suffixed and sanitized
 * advisories; a skipped row ran nothing, so neither applies. An in-flight path overflow is red on any
 * row the server set it on, because that move cannot complete.
 */
export function badgesFor(item: Badgeable): Badge[] {
  const badges: Badge[] = [];
  const badge = statusBadge(item.status);
  if (badge !== null) badges.push(badge);
  if (classifyItem(item) === "will-change") {
    if (item.suffixed) badges.push({ label: "Numbered to avoid a clash", variant: "amber" });
    if (item.sanitized) badges.push({ label: "Cleaned for the filesystem", variant: "amber" });
  }
  const overflow = inFlightOverflowLabel(item);
  if (overflow !== null) badges.push({ label: overflow, variant: "red" });
  return badges;
}
