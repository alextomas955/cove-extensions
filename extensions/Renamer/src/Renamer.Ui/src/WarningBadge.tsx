/**
 * Per-row status pill. Badges derive from the PreviewItem `status`
 * STRING enum PLUS the `suffixed` / `sanitized` bools — there is NO `flags[]` array on /preview.
 *
 * Color is NEVER the only signal: amber/red badges lead with a lucide `AlertTriangle` glyph and
 * always carry text (accessibility). Every label string is a React text node (auto-escaped).
 */
// This is a Vite *library* bundle (one ESM artifact), not an HMR app, so the fast-refresh
// "components-only file" rule's premise (preserving Fast Refresh boundaries) does not apply. The
// co-located `badgesFor` helper is intentionally exported beside the component; splitting it into a
// separate file would be pure churn with no runtime or HMR benefit here.
/* eslint-disable react-refresh/only-export-components */
import { AlertTriangle } from "lucide-react";

import type { PreviewItem } from "./preview";

type Variant = "amber" | "gray" | "red";

interface Badge {
  label: string;
  variant: Variant;
}

const VARIANT_CLASS: Record<Variant, string> = {
  amber: "border-amber-400/40 bg-amber-400/10 text-amber-400",
  gray: "border-border bg-card text-muted",
  red: "border-red-700/50 bg-red-950/40 text-red-400",
};

/**
 * Map a PreviewItem to its badges (one per warning kind, with user-facing labels).
 * Rename/Move with no extra signal returns [] (the positive default, no badge). suffixed/sanitized
 * add amber advisory badges even on a will-rename row.
 */
export function badgesFor(item: PreviewItem): Badge[] {
  const badges: Badge[] = [];
  switch (item.status) {
    case "NoOp":
      badges.push({ label: "No change needed", variant: "gray" });
      break;
    case "SkipGated":
      badges.push({ label: "Skipped — needs a required field", variant: "amber" });
      break;
    case "SkipCollision":
      badges.push({ label: "Skipped — name conflict", variant: "amber" });
      break;
    case "SkipLocked":
      badges.push({ label: "Skipped — file in use", variant: "amber" });
      break;
    case "Failed":
      badges.push({ label: "Failed — rolled back", variant: "red" });
      break;
    case "Renamer":
    case "Move":
      if (item.suffixed) badges.push({ label: "Numbered to avoid a clash", variant: "amber" });
      if (item.sanitized) badges.push({ label: "Cleaned for the filesystem", variant: "amber" });
      break;
  }
  return badges;
}

function Pill({ badge }: { badge: Badge }) {
  const showGlyph = badge.variant === "amber" || badge.variant === "red";
  return (
    <span
      className={`inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${VARIANT_CLASS[badge.variant]}`}
    >
      {showGlyph ? <AlertTriangle className="h-3 w-3" /> : null}
      {badge.label}
    </span>
  );
}

/** Render every badge for an item (may be empty → renders nothing). */
export function WarningBadges({ item }: { item: PreviewItem }) {
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
