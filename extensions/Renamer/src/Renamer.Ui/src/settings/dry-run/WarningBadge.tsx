/**
 * Per-row status pill. The label set and the derivation live in warningBadgeLogic.ts, which is
 * exhaustive over the wire's status union — so a status the server grows cannot reach a row with no
 * pill saying why it was skipped.
 *
 * Color is NEVER the only signal: amber/red badges lead with a lucide `AlertTriangle` glyph and
 * always carry text (accessibility). Every label string is a React text node (auto-escaped).
 */
import { AlertTriangle } from "lucide-react";
import { StatusPill } from "@cove-extensions/ui-shared";

import { badgesFor, type Badge, type Badgeable } from "./warningBadgeLogic";

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
