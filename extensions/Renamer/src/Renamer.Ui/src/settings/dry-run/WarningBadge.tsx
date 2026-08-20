/**
 * Per-row status pill. Which badges a row earns is decided in `warningBadgeLogic.ts` — from the
 * PreviewItemView `status` STRING enum PLUS the row's advisory bools (`suffixed`, `sanitized`,
 * `inFlightPathOverflow`), since there is NO `flags[]` array on /preview. This file only renders them.
 *
 * Color is NEVER the only signal: amber/red badges lead with a lucide `AlertTriangle` glyph and
 * always carry text (accessibility). Every label string is a React text node (auto-escaped).
 */
import { AlertTriangle } from "lucide-react";
import { StatusPill } from "@cove-extensions/ui-shared";

import type { Badge, Badgeable } from "./warningBadgeLogic";
import { badgesFor } from "./warningBadgeLogic";

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
