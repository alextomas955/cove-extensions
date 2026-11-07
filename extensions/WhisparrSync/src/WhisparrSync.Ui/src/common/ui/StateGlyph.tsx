/**
 * The one place a vocabulary icon key becomes a drawn glyph. Every surface resolves a state here,
 * so one state has one shape.
 *
 * The mark is hidden from assistive technology because the label beside it carries the meaning.
 */
import {
  Ban,
  Bookmark,
  BookmarkMinus,
  Check,
  Circle,
  CircleAlert,
  CircleDashed,
  CircleHelp,
  HardDrive,
  Unlink,
} from "lucide-react";

// lucide-react is a host import-map external, so a name must exist in the module the host serves
// as well as in the version resolved for typechecking. `CircleHelp` does, `CircleQuestionMark`
// does not. A named import the host module lacks takes the whole bundle down at load time, and a
// build and a typecheck cannot see it.
const GLYPH: Record<string, typeof Bookmark> = {
  bookmark: Bookmark,
  bookmarkMinus: BookmarkMinus,
  circleDashed: CircleDashed,
  ban: Ban,
  circleQuestion: CircleHelp,
  hardDrive: HardDrive,
  unlink: Unlink,
  // The settings page's own three: something is settled, something needs an answer, and something
  // holds nothing to answer for.
  check: Check,
  circleAlert: CircleAlert,
};

/** Drawn filled, so the state that leads the axis reads heavier than the rest. */
const FILLED = new Set(["bookmark"]);

export function StateGlyph({
  iconKey,
  className = "h-3.5 w-3.5",
}: Readonly<{
  iconKey: string;
  className?: string;
}>) {
  // A key with no glyph draws the plain circle, not nothing. A chip with no mark leaves the state
  // distinguished by its tint alone.
  const Icon = GLYPH[iconKey] ?? Circle;
  return (
    <Icon
      className={className}
      fill={FILLED.has(iconKey) ? "currentColor" : "none"}
      aria-hidden="true"
    />
  );
}
