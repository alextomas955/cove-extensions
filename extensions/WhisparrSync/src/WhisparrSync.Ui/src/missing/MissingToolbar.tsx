/**
 * The bar above the grid: the tab's name and range, the search, the ordering, the facet menus,
 * Refresh and the whole-catalogue control.
 *
 * Every control writes through the tab's own URL hook, so a change reaches the tab shell that
 * refetches and the address a reader copies says what they were looking at. This file parses no
 * query string of its own.
 *
 * The catalogue prop is absent until a page has answered. Before that there is no range, no ordering
 * and no facet to offer, so the bar draws the two controls that need neither.
 *
 * The class strings below are Cove's own, transcribed from `ui/src/components/listToolbarStyles.ts`,
 * `DetailListToolbar.tsx` and `ListSearchControl.tsx`. No stylesheet ships with this bundle, so a
 * class the host does not emit renders nothing.
 */
import { useCallback, useEffect, useId, useRef, useState, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Radar, RefreshCw, Search } from "lucide-react";

import {
  ACTION_REFRESH,
  MISSING_TAB_HEADING,
  countLine,
  facetCoversEverything,
  facetMenuBound,
  monitorAllConfirmation,
} from "../common/ui/copy";
import { OFF_SCREEN } from "../common/ui/offScreen";
import type { MissingFacetMenu as FacetMenuView, MissingPageView } from "../wire/api";
import type { MissingEntityKind } from "./entityKindLogic";
import { ConfirmDialog } from "./hostComponents";
import { countLineParts } from "./missingCountLogic";
import { facetMenuRows, menuIsBounded, toggleFacetValue } from "./missingFacetLogic";
import { MissingFacetMenu, type MissingMenuRow } from "./MissingFacetMenu";
import {
  MISSING_TOOLBAR_CONTROLS,
  MONITOR_ALL_LABEL,
  SEARCH_PLACEHOLDER,
  SORT_MENU_LABEL,
  monitorAllOffered,
  searchSettleDelayMs,
  sortOptionsFor,
} from "./missingToolbarLogic";
import { useMissingUrlState } from "./useMissingUrlState";

/** Cove's own list toolbar surface, so the controls read as one bar rather than a row of boxes. */
const BAR_CLASS =
  "mb-3 flex w-full flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-3 py-3 text-sm shadow-sm shadow-black/20 sm:px-2.5 sm:py-2";

/** Cove's `toolbarSegmentClass`: the group one control sits in. */
const SEGMENT_CLASS =
  "flex min-h-10 items-center gap-1 rounded-lg border border-border bg-card/70 px-1.5 py-1 shadow-sm sm:min-h-0";

/**
 * Cove's `toolbarSelectClass`: the control inside a segment.
 *
 * `rounded-md` is Cove's own. A `rounded-xl border border-border` control would meet the host's
 * glass rule for that combination, which redeclares the border colour outside any pseudo-class and
 * so paints over the accent border a focused control takes.
 */
const SELECT_CLASS =
  "min-h-10 rounded-md border border-border/60 bg-input px-2.5 py-2 text-sm text-foreground shadow-inner focus:outline-none focus:border-accent sm:min-h-[30px] sm:px-2 sm:py-1 sm:text-xs";

/** The same control with room for a leading glyph. */
const ACTION_CLASS = `inline-flex items-center gap-1.5 ${SELECT_CLASS}`;

/** Cove's own search field, which dodges the glass rule the same way. */
const SEARCH_INPUT_CLASS =
  "min-h-10 w-full rounded-lg border border-border bg-card/70 py-2 pl-8 pr-3 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none sm:min-h-0 sm:py-1.5 sm:pl-7 sm:text-xs";

/** Cove's own leading glyph inside a search field. */
const SEARCH_ICON_CLASS = "absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted";

/** What the answered page tells the toolbar, once one has answered. */
export interface MissingToolbarCatalogue {
  readonly kind: MissingEntityKind;
  readonly view: MissingPageView;
}

export function MissingToolbar({
  onRefresh,
  onMonitorAll,
  catalogue,
}: {
  onRefresh: () => void;
  /** Marks everything the narrowing in the address covers, once the reader has confirmed. */
  onMonitorAll: () => void;
  catalogue?: MissingToolbarCatalogue;
}) {
  const [view, setView] = useMissingUrlState();
  const [text, setText] = useState(() => view.q);
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const openTrigger = useRef<HTMLElement | null>(null);
  const headingId = useId();

  // Written by replacement once typing settles, so the back button leaves the tab and does not step
  // through half-typed searches. A search whose answer is shorter than the position the reader is at
  // would leave them looking at an empty page of a non-empty answer, so the position resets with it.
  useEffect(() => {
    if (text === view.q) return undefined;
    const timer = setTimeout(() => {
      setView({ ...view, q: text, page: 1 });
    }, searchSettleDelayMs);
    return () => {
      clearTimeout(timer);
    };
  }, [text, view, setView]);

  const openFrom = useCallback((name: string, trigger: HTMLElement) => {
    openTrigger.current = trigger;
    setOpenMenu((current) => (current === name ? null : name));
  }, []);

  const closeMenu = useCallback(() => {
    setOpenMenu(null);
  }, []);

  const controls =
    catalogue === undefined ? (["search", "refresh"] as const) : MISSING_TOOLBAR_CONTROLS;

  const sortRows =
    catalogue === undefined
      ? []
      : sortOptionsFor(catalogue.view.sorts, view.sort ?? catalogue.view.sortInForce);
  const sortInForce = sortRows.find((row) => row.selected);

  // A catalogue with nothing in it has no range, and the grid states why in place of a page of
  // cards, so the bar states no range either.
  const range = catalogue === undefined ? null : countLineParts(catalogue.view);

  return (
    <div role="toolbar" aria-labelledby={headingId} className={BAR_CLASS}>
      <div className="mr-auto flex min-w-0 items-center gap-2 pr-2">
        <h2 id={headingId} className="font-semibold text-foreground">
          {MISSING_TAB_HEADING}
        </h2>
        {range === null || range.total === 0 ? null : (
          // The figure the range ends on moves under a facet or a search without focus moving with
          // it, so a screen-reader user is told what a sighted reader sees change.
          <span role="status" aria-live="polite" className="text-xs tabular-nums text-muted">
            {countLine(range.from, range.to, range.total, range.atCeiling)}
          </span>
        )}
      </div>

      <div className="relative min-w-0 flex-1">
        <Search aria-hidden className={SEARCH_ICON_CLASS} />
        <input
          type="text"
          value={text}
          placeholder={SEARCH_PLACEHOLDER}
          onChange={(event) => {
            setText(event.target.value);
          }}
          className={SEARCH_INPUT_CLASS}
        />
      </div>

      {controls.includes("sort") && sortRows.length > 0 ? (
        <MenuControl
          name="sort"
          label={SORT_MENU_LABEL}
          trigger={sortInForce?.label ?? SORT_MENU_LABEL}
          rows={sortRows}
          open={openMenu === "sort"}
          openTrigger={openTrigger}
          onOpen={openFrom}
          onClose={closeMenu}
          onPick={(value) => {
            setView({ ...view, sort: view.sort === value ? null : value, page: 1 });
            closeMenu();
          }}
        />
      ) : null}

      {controls.includes("facets") && catalogue !== undefined
        ? catalogue.view.facets.map((menu) => (
            <FacetControl
              key={menu.key}
              menu={menu}
              selected={view.filters[menu.key] ?? null}
              open={openMenu === menu.key}
              openTrigger={openTrigger}
              onOpen={openFrom}
              onClose={closeMenu}
              onPick={(value) => {
                setView({
                  ...view,
                  filters: toggleFacetValue(view.filters, menu.key, value),
                  page: 1,
                });
                closeMenu();
              }}
            />
          ))
        : null}

      <div className={SEGMENT_CLASS}>
        <button type="button" onClick={onRefresh} className={ACTION_CLASS}>
          <RefreshCw aria-hidden className="h-3.5 w-3.5" />
          {ACTION_REFRESH}
        </button>

        {catalogue === undefined || !monitorAllOffered(catalogue.kind) ? null : (
          <button
            type="button"
            onClick={() => {
              setConfirming(true);
            }}
            className={ACTION_CLASS}
          >
            {/* The glyph the monitor menu already gives to marking something wanted. */}
            <Radar aria-hidden className="h-3.5 w-3.5" />
            {MONITOR_ALL_LABEL}
          </button>
        )}
      </div>

      {!confirming || catalogue === undefined
        ? null
        : // Portaled to the document, so no ancestor of this tab can become the containing block of a
          // dialog that positions against the viewport and clip it to the grid.
          createPortal(
            <ConfirmDialog
              open
              title={MONITOR_ALL_LABEL}
              confirmLabel={MONITOR_ALL_LABEL}
              message={monitorAllConfirmation(
                catalogue.view.catalogueSize,
                catalogue.view.providerName,
              )}
              onConfirm={() => {
                onMonitorAll();
                setConfirming(false);
              }}
              onCancel={() => {
                setConfirming(false);
              }}
            />,
            document.body,
          )}
    </div>
  );
}

/**
 * A trigger and the menu it opens, inside its own toolbar segment.
 *
 * The trigger draws the value in force. Its own name leads that off screen, so a reader hears which
 * menu they are on before they hear what it is set to.
 */
function MenuControl({
  name,
  label,
  trigger,
  rows,
  open,
  openTrigger,
  bound,
  onOpen,
  onClose,
  onPick,
}: {
  name: string;
  label: string;
  trigger: string;
  rows: readonly MissingMenuRow[];
  open: boolean;
  openTrigger: RefObject<HTMLElement | null>;
  bound?: string | null;
  onOpen: (name: string, trigger: HTMLElement) => void;
  onClose: () => void;
  onPick: (value: string) => void;
}) {
  return (
    <div className={SEGMENT_CLASS}>
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={(event) => {
          onOpen(name, event.currentTarget);
        }}
        className={SELECT_CLASS}
      >
        <span style={OFF_SCREEN}>{label}</span>
        {trigger}
      </button>
      {open ? (
        <MissingFacetMenu
          label={label}
          rows={rows}
          triggerRef={openTrigger}
          bound={bound}
          onPick={onPick}
          onClose={onClose}
        />
      ) : null}
    </div>
  );
}

/**
 * One facet menu, whose control names the value in force.
 *
 * With nothing picked the control names what the menu covers rather than what pressing it opens, so
 * the bar reads as a set of answers instead of a set of doors. Picking the value in force again
 * clears it, which is the same row the menu already offers.
 */
function FacetControl({
  menu,
  selected,
  open,
  openTrigger,
  onOpen,
  onClose,
  onPick,
}: {
  menu: FacetMenuView;
  selected: string | null;
  open: boolean;
  openTrigger: RefObject<HTMLElement | null>;
  onOpen: (name: string, trigger: HTMLElement) => void;
  onClose: () => void;
  onPick: (value: string) => void;
}) {
  const rows = facetMenuRows(menu, selected);
  const inForce =
    selected === null ? null : (menu.values.find((value) => value.value === selected) ?? null);

  return (
    <MenuControl
      name={menu.key}
      label={menu.label}
      trigger={selected === null ? facetCoversEverything(menu.label) : (inForce?.label ?? selected)}
      rows={rows}
      open={open}
      openTrigger={openTrigger}
      bound={
        menuIsBounded(menu) ? facetMenuBound(menu.values.length, menu.reportedValueCount) : null
      }
      onOpen={onOpen}
      onClose={onClose}
      onPick={onPick}
    />
  );
}
