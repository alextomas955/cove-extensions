/**
 * The glyph each Whisparr verb is drawn with, wherever it is offered.
 *
 * One verb keeps one glyph across the entity menu, the scene batch overlay, a catalogue card, the
 * catalogue toolbar and the selection bar. A surface maps its own row keys onto these names rather
 * than naming an icon, so a verb cannot pick up a second shape on one surface.
 *
 * Separate from the state vocabulary on purpose. A state says what an entity is and a verb says
 * what pressing does, so the two families are told apart by shape: `Monitored` is a bookmark and
 * the monitor verb is a radar.
 *
 * A scope is here too, because choosing one carries the monitor gesture out.
 */
import {
  Ban,
  CalendarClock,
  CircleSlash,
  FileCheck2,
  Library,
  Plus,
  Radar,
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
function decorative(Icon: typeof Radar): RowIcon {
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
  monitor: decorative(Radar),
  unmonitor: decorative(CircleSlash),
  search: decorative(Search),
  exclude: decorative(Ban),
  refresh: decorative(RefreshCw),
  reflectOwned: decorative(FileCheck2),
  monitorNewReleases: decorative(CalendarClock),
  monitorBackCatalogue: decorative(Library),
};
