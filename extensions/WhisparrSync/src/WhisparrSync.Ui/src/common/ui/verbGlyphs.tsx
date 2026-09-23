/**
 * The glyph each Whisparr verb is drawn with, wherever it is offered.
 *
 * One verb keeps one glyph across the entity menu, the scene batch overlay, a catalogue card, the
 * catalogue toolbar and the selection bar. A surface maps its own row keys onto these names rather
 * than naming an icon, so a verb cannot pick up a second shape on one surface.
 *
 * A verb that produces a state takes that state's own mark: pressing `Monitor` is what makes an
 * entity `Monitored`, and a reader meets the two a tab apart. The tint is what separates them - a
 * state is tinted by what it means and a verb by the surface it sits on.
 *
 * A scope is here too, because choosing one carries the monitor gesture out.
 */
import {
  Ban,
  Bookmark,
  BookmarkMinus,
  CalendarClock,
  FileCheck2,
  Library,
  Plus,
  RefreshCw,
  Search,
} from "lucide-react";

import type { RowIcon } from "./ChoiceOverlay";

export type WhisparrVerb =
  | "add"
  | "monitor"
  | "unmonitor"
  | "search"
  | "exclude"
  | "refresh"
  | "reflectOwned"
  | "monitorNewReleases"
  | "monitorBackCatalogue";

// Hidden from assistive technology in the one place a verb becomes a glyph, because every surface
// draws the verb's own name beside it.
function decorative(Icon: typeof Bookmark): RowIcon {
  return function VerbGlyph({ className }: { className?: string }) {
    return <Icon className={className} aria-hidden="true" />;
  };
}

/**
 * Total by type, so a verb added later fails the build rather than drawing no glyph.
 *
 * Two verbs share a glyph only where they are the same gesture at a different scope: adding one
 * scene and adding every missing one, searching one scene and searching every monitored one.
 */
export const VERB_GLYPH: Record<WhisparrVerb, RowIcon> = {
  add: decorative(Plus),
  monitor: decorative(Bookmark),
  unmonitor: decorative(BookmarkMinus),
  search: decorative(Search),
  exclude: decorative(Ban),
  refresh: decorative(RefreshCw),
  reflectOwned: decorative(FileCheck2),
  monitorNewReleases: decorative(CalendarClock),
  monitorBackCatalogue: decorative(Library),
};
