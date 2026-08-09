/**
 * Per-row status pill. Badges derive from the PreviewItemView `status`
 * STRING enum PLUS the `suffixed` / `sanitized` bools — there is NO `flags[]` array on /preview.
 *
 * Color is NEVER the only signal: amber/red badges lead with a lucide `AlertTriangle` glyph and
 * always carry text (accessibility). Every label string is a React text node (auto-escaped).
 */
import { AlertTriangle } from "lucide-react";
import { StatusPill } from "@cove-extensions/ui-shared";

import type { RenamerStatus } from "../../wire/api";

type Variant = "amber" | "gray" | "red";

interface Badge {
  label: string;
  variant: Variant;
}

/**
 * The three fields a badge is derived from. Declared structurally rather than as `PreviewItemView` so
 * a `/preview` item and a leaner `/scan-rows` row both qualify without either wire shape gaining a
 * field only the badges would read.
 */
export interface Badgeable {
  status: RenamerStatus;
  suffixed: boolean;
  sanitized: boolean;
}

/**
 * Map an item to its badges (one per warning kind, with user-facing labels).
 * Rename/Move with no extra signal returns [] (the positive default, no badge). suffixed/sanitized
 * add amber advisory badges even on a will-rename row.
 */
function badgesFor(item: Badgeable): Badge[] {
  const badges: Badge[] = [];
  switch (item.status) {
    case "noOp":
      badges.push({ label: "No change needed", variant: "gray" });
      break;
    case "skipGated":
      badges.push({ label: "Skipped — needs a required field", variant: "amber" });
      break;
    case "skipCollision":
      badges.push({ label: "Skipped — name conflict", variant: "amber" });
      break;
    case "skipLocked":
      badges.push({ label: "Skipped — file in use", variant: "amber" });
      break;
    case "skipMissingSource":
      badges.push({ label: "Skipped — file missing on disk", variant: "amber" });
      break;
    case "failed":
      badges.push({ label: "Failed — rolled back", variant: "red" });
      break;
    case "renamer":
    case "move":
      if (item.suffixed) badges.push({ label: "Numbered to avoid a clash", variant: "amber" });
      if (item.sanitized) badges.push({ label: "Cleaned for the filesystem", variant: "amber" });
      break;
  }
  return badges;
}

function Pill({ badge }: { badge: Badge }) {
  const showGlyph = badge.variant === "amber" || badge.variant === "red";
  return (
    <StatusPill
      variant={badge.variant}
      icon={showGlyph ? <AlertTriangle className="h-3 w-3" /> : undefined}
    >
      <span className="font-medium">{badge.label}</span>
    </StatusPill>
  );
}

/** Render every badge for an item (may be empty → renders nothing). */
export function WarningBadges({ item }: { item: Badgeable }) {
  const badges = badgesFor(item);
  if (badges.length === 0) return null;
  return (
    <span className="inline-flex flex-wrap gap-1">
      {badges.map((b) => (
        <Pill key={b.label} badge={b} />
      ))}
    </span>
  );
}
