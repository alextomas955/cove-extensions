/**
 * The panel a toolbar menu opens: the ordering menu and every facet menu draw through this one.
 *
 * The rows a menu opens on arrive already decided. A facet menu given a way to ask replaces them
 * with the values the source itself matches while a fragment is typed, so a value the menu was never
 * handed is still reachable; a menu given none narrows the rows it holds.
 */
import { useEffect, useId, useRef, useState, type CSSProperties, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Check, Search } from "lucide-react";
// From the subpath, so drawing a menu does not pull the whole primitives module into this slice.
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import {
  FACET_MENU_NO_MATCHES,
  FACET_MENU_SEARCH,
  FACET_VALUES_ASKING,
  FACET_VALUES_NONE_MATCH,
  FACET_VALUES_NOT_READ,
  facetMatchesBound,
  facetMenuBound,
  facetMenuSearchLabel,
} from "../common/ui/copy";
import {
  facetPanelView,
  type MissingFacetCounts,
  type MissingFacetNotice,
  type MissingFacetRow,
} from "./missingFacetLogic";
import { useFacetValueLookup, type FacetValueSearch } from "./useFacetValueLookup";

/** The sentence each state of a lookup reads as. */
const NOTICES: Record<MissingFacetNotice, string> = {
  asking: FACET_VALUES_ASKING,
  noneHere: FACET_MENU_NO_MATCHES,
  noneAtSource: FACET_VALUES_NONE_MATCH,
  notRead: FACET_VALUES_NOT_READ,
};

/** The host's own gap between a control and the panel it opens. */
const OFFSET = 4;

/** The host's own margin between a panel and the viewport edge. */
const GUTTER = 8;

/** The least room a panel is ever given, whatever the measurement says. */
const MIN_ROOM = 160;

/**
 * What the overlay's roving focus steps through.
 *
 * The search box joins the rows, in document order, so the panel opens with the caret in it and a
 * typed character is not swallowed by a focused row. The arrow keys step from it into the rows, and
 * Escape closes from anywhere because the overlay listens on the document.
 */
const NAV_ITEMS = '[data-menu-search], [role^="menuitem"]';

interface AnchoredPlacement {
  readonly at: CSSProperties;
  /** The room below the trigger, or null until it has been measured. */
  readonly availableHeight: number | null;
}

/**
 * Where to put the panel, given the control it belongs to.
 *
 * Fixed and portaled to the document: the host clips its entity hero with `overflow-hidden`, which
 * a `z-50` panel in the flow there does not escape.
 */
function useAnchoredTo(triggerRef: RefObject<HTMLElement | null>): AnchoredPlacement {
  const [placement, setPlacement] = useState<AnchoredPlacement>({
    at: { top: 0, left: 0 },
    availableHeight: null,
  });

  useEffect(() => {
    const place = () => {
      const anchor = triggerRef.current;
      if (anchor === null) return;
      const rect = anchor.getBoundingClientRect();
      const top = rect.bottom + OFFSET;
      setPlacement({
        at: { top, left: Math.max(GUTTER, rect.left) },
        availableHeight: Math.max(MIN_ROOM, window.innerHeight - top - GUTTER),
      });
    };

    place();
    window.addEventListener("resize", place);
    // Capture, so the panel follows a scroll of any container between it and the document.
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [triggerRef]);

  return placement;
}

export function MissingFacetMenu({
  label,
  rows,
  triggerRef,
  bound,
  facetKey,
  search,
  onPick,
  onClose,
}: {
  /** What the menu is called, which is the name it announces. */
  label: string;
  rows: readonly MissingFacetRow[];
  /** The control that opened it. */
  triggerRef: RefObject<HTMLElement | null>;
  /** What the menu carries of the source's list, or null where it carries all of it. */
  bound?: MissingFacetCounts | null;
  /** The facet the source knows this menu by, absent for a menu that is not a facet. */
  facetKey?: string;
  /** How to ask the source which values match, absent for a menu that asks nothing. */
  search?: FacetValueSearch;
  onPick: (value: string, label: string) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const boundId = useId();
  const placement = useAnchoredTo(triggerRef);
  const [query, setQuery] = useState("");
  const lookup = useFacetValueLookup(search, facetKey, query);
  const panel = facetPanelView(rows, bound ?? null, query, lookup);
  const stated =
    panel.bound === null
      ? null
      : panel.bound.ofMatches
        ? facetMatchesBound(panel.bound.shown, panel.bound.reported)
        : facetMenuBound(panel.bound.shown, panel.bound.reported);

  useOverlayKeys(ref, {
    onClose,
    nav: "menu",
    itemSelector: NAV_ITEMS,
    // The trigger does not count as outside. Without it a press on the trigger closes the menu here
    // and the trigger's own handler opens it again in the same gesture.
    excludeRefs: [triggerRef],
    restoreFocus: true,
  });

  return createPortal(
    <div
      ref={ref}
      style={{ ...placement.at, maxHeight: placement.availableHeight ?? undefined }}
      // The host's own dropdown surface, which carries the background, the border, the radius its
      // theme is set to, the shadow and the clip. A radius utility here would fight the theme.
      className="styled-dropdown-panel fixed z-50 flex w-64 flex-col"
    >
      <div className="relative border-b border-border p-1.5">
        <Search
          aria-hidden
          className="pointer-events-none absolute left-3 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted"
        />
        <input
          type="text"
          data-menu-search=""
          value={query}
          aria-label={facetMenuSearchLabel(label)}
          placeholder={FACET_MENU_SEARCH}
          onChange={(event) => {
            setQuery(event.target.value);
          }}
          className="w-full rounded-md border border-border/60 bg-input py-1.5 pl-9 pr-2 text-xs text-foreground shadow-inner focus:border-accent focus:outline-none"
        />
      </div>
      <div
        role="menu"
        aria-label={label}
        aria-describedby={stated === null ? undefined : boundId}
        // `min-h-0` is what lets the panel shrink below its own content, so every row is reachable
        // with a pointer at any trigger position.
        className="min-h-0 overflow-y-auto overflow-x-hidden py-1 text-left"
      >
        {stated === null ? null : (
          // Carries no menu role, so the overlay's roving focus passes over it.
          <div id={boundId} className="px-3 py-2 text-xs text-secondary">
            {stated}
          </div>
        )}
        {panel.says === null ? null : (
          // Carries no menu role either: what a lookup answered is a sentence to read, not a row to
          // pick. It is stated beside the value in force rather than instead of it, so a value
          // picked before the fragment was typed can still be unpicked.
          <p className="px-3 py-2 text-xs text-secondary">{NOTICES[panel.says]}</p>
        )}
        {panel.rows.map((row) => (
          // The overlay's roving focus selects on `[role^="menuitem"]`, so a row without one is
          // invisible to the arrow keys.
          <button
            key={row.value}
            type="button"
            role="menuitemcheckbox"
            aria-checked={row.selected}
            onClick={() => {
              onPick(row.value, row.label);
            }}
            className="flex w-full items-center justify-between gap-2 px-3 py-1.5 text-left text-xs text-foreground hover:bg-card focus:outline-none focus:ring-2 focus:ring-accent"
          >
            <span className="min-w-0 truncate">{row.label}</span>
            {row.selected ? (
              <Check aria-hidden className="h-3.5 w-3.5 shrink-0 text-accent" />
            ) : null}
          </button>
        ))}
      </div>
    </div>,
    document.body,
  );
}
