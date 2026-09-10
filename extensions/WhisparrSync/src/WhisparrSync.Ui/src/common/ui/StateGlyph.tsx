/**
 * The one place a vocabulary icon key becomes a drawn glyph.
 *
 * Every surface that shows a state resolves it here, so the mark on a card, the mark in a list and
 * the mark on the row above them are the same shape. Two records of glyphs is how they stop being.
 *
 * The mark is hidden from assistive technology because the label beside it already carries the
 * meaning. It is what distinguishes two states that share a tint on screen.
 */
import { Ban, Bookmark, Circle, CircleDashed, CircleHelp, Download } from "lucide-react";

// `CircleHelp` is the name that exists on both sides of the externalized boundary, which
// `CircleQuestionMark` is not: lucide-react is a host import-map external, so a name must exist in
// the module the host serves as well as in the version resolved for typechecking. A named import
// the host module lacks is a load-time error that takes the whole bundle down, and every surface of
// this extension with it. A build and a typecheck cannot see that.
const GLYPH: Record<string, typeof Bookmark> = {
  bookmark: Bookmark,
  circle: Circle,
  circleDashed: CircleDashed,
  ban: Ban,
  circleQuestion: CircleHelp,
  download: Download,
};

/** Which marks are drawn filled, so the state that leads the axis reads heavier than the rest. */
const FILLED = new Set(["bookmark"]);

export function StateGlyph({
  iconKey,
  className = "h-3.5 w-3.5",
}: {
  iconKey: string;
  /** The size, and a colour where the surface does not want the one it inherits. */
  className?: string;
}) {
  // A key with no glyph draws the plain circle rather than nothing: a chip with a label and no mark
  // is a state distinguished by its tint alone, which is the one thing the mark is there to prevent.
  const Icon = GLYPH[iconKey] ?? Circle;
  return (
    <Icon
      className={className}
      fill={FILLED.has(iconKey) ? "currentColor" : "none"}
      aria-hidden="true"
    />
  );
}
