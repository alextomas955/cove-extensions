/**
 * The bar above the grid: the tab's name and range, the search, the ordering, the facet
 * dropdowns, Refresh and the whole-catalogue control.
 *
 * Every control writes through the tab's own URL hook, so a change reaches the tab shell that
 * refetches. This file parses no query string of its own.
 *
 * The catalogue prop is absent until a page has answered. Before that there is no range, no
 * ordering and no facet to offer, so the bar draws the two controls that need neither.
 *
 * The class strings below are Cove's own, transcribed from
 * `ui/src/components/listToolbarStyles.ts`, `DetailListToolbar.tsx` and `ListSearchControl.tsx`.
 * No stylesheet ships with this bundle, so a class the host does not emit renders nothing.
 */
import { useEffect, useId, useState } from "react";
import { createPortal } from "react-dom";
// `Search` here is the search box's own affordance, not the verb that asks Whisparr to look.
import { Search } from "lucide-react";

import {
  ACTION_REFRESH,
  MISSING_TAB_HEADING,
  countLine,
  facetCoversEverything,
  monitorAllConfirmation,
} from "../common/ui/copy";
import { VERB_GLYPH } from "../common/ui/verbGlyphs";

const RefreshGlyph = VERB_GLYPH.refresh;
const MonitorGlyph = VERB_GLYPH.monitor;
import type {
  MissingFacetMenu as FacetMenuView,
  MissingPageView,
  WhisparrEntityKind,
} from "../wire/api";
import { ConfirmDialog } from "./hostComponents";
import { countLineParts } from "./missingCountLogic";
import { facetMenuRows, toggleFacetValue } from "./missingFacetLogic";
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

// Cove's own list toolbar surface.
const BAR_CLASS =
  "mb-3 flex w-full flex-wrap items-center gap-2 rounded-xl border border-border bg-surface/90 px-3 py-3 text-sm shadow-sm shadow-black/20 sm:px-2.5 sm:py-2";

// Cove's `toolbarSegmentClass`.
const SEGMENT_CLASS =
  "flex min-h-10 items-center gap-1 rounded-lg border border-border bg-card/70 px-1.5 py-1 shadow-sm sm:min-h-0";

// Cove's `toolbarSelectClass`. `rounded-md` and not `rounded-xl`: a `rounded-xl border
// border-border` control meets the host's glass rule, which redeclares the border colour outside
// any pseudo-class and paints over the accent border a focused control takes.
const SELECT_CLASS =
  "min-h-10 rounded-md border border-border/60 bg-input px-2.5 py-2 text-sm text-foreground shadow-inner focus:outline-none focus:border-accent sm:min-h-[30px] sm:px-2 sm:py-1 sm:text-xs";

// Cove's `toolbarIconButtonClass`, widened for a label. Cove writes it for a square icon-only
// button, so its fixed minimum width, centring and square padding give way here. Its colours,
// border, hover and focus ring are Cove's own.
const ACTION_CLASS =
  "inline-flex min-h-10 items-center gap-1.5 rounded-md border border-transparent px-2.5 py-2 text-sm text-secondary hover:bg-card/80 hover:text-foreground focus:outline-none focus:border-accent sm:min-h-0 sm:px-2 sm:py-1.5 sm:text-xs";

// Capped at Cove's own width, so a long source value cannot stretch the bar. The ring is added
// because the host redeclares a border colour outside any pseudo-class, so the focused border
// Cove's own field relies on is painted over and a keyboard user is left with nothing to see.
const DROPDOWN_CLASS = `max-w-[10rem] focus:ring-2 focus:ring-accent ${SELECT_CLASS}`;

// Cove's own search field, which dodges the glass rule the same way.
const SEARCH_INPUT_CLASS =
  "min-h-10 w-full rounded-lg border border-border bg-card/70 py-2 pl-8 pr-3 text-sm text-foreground placeholder:text-muted focus:border-accent focus:outline-none sm:min-h-0 sm:py-1.5 sm:pl-7 sm:text-xs";

// Cove's own leading glyph inside a search field.
const SEARCH_ICON_CLASS = "absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted";

export interface MissingToolbarCatalogue {
  readonly kind: WhisparrEntityKind;
  readonly view: MissingPageView;
}

export function MissingToolbar({
  onRefresh,
  onMonitorAll,
  catalogue,
}: Readonly<{
  onRefresh: () => void;
  /** Marks everything the narrowing in the address covers, once the reader has confirmed. */
  onMonitorAll: () => void;
  catalogue?: MissingToolbarCatalogue;
}>) {
  const [view, setView] = useMissingUrlState();
  const [text, setText] = useState(() => view.q);
  const [confirming, setConfirming] = useState(false);
  const headingId = useId();

  // Written by replacement once typing settles, so the back button leaves the tab and does not
  // step through half-typed searches. The page resets with it, because a shorter answer would
  // otherwise leave the reader on an empty page of a non-empty result.
  useEffect(() => {
    if (text === view.q) return undefined;
    const timer = setTimeout(() => {
      setView({ ...view, q: text, page: 1 });
    }, searchSettleDelayMs);
    return () => {
      clearTimeout(timer);
    };
  }, [text, view, setView]);

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
          // The range changes under a facet or a search without focus moving, so a live region
          // tells a screen-reader user what a sighted reader sees change.
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
        <select
          aria-label={SORT_MENU_LABEL}
          value={sortInForce?.value ?? ""}
          onChange={(event) => {
            setView({ ...view, sort: event.target.value || null, page: 1 });
          }}
          className={DROPDOWN_CLASS}
        >
          {sortInForce === undefined ? <option value="">{SORT_MENU_LABEL}</option> : null}
          {sortRows.map((row) => (
            <option key={row.value} value={row.value}>
              {row.label}
            </option>
          ))}
        </select>
      ) : null}

      {controls.includes("facets") && catalogue !== undefined
        ? catalogue.view.facets.map((menu) => (
            <FacetControl
              key={menu.key}
              menu={menu}
              selected={view.filters[menu.key] ?? null}
              onPick={(value) => {
                setView({
                  ...view,
                  filters: toggleFacetValue(view.filters, menu.key, value),
                  page: 1,
                });
              }}
            />
          ))
        : null}

      <div className={SEGMENT_CLASS}>
        <button type="button" onClick={onRefresh} className={ACTION_CLASS}>
          <RefreshGlyph className="h-3.5 w-3.5" />
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
            <MonitorGlyph className="h-3.5 w-3.5" />
            {MONITOR_ALL_LABEL}
          </button>
        )}
      </div>

      {!confirming || catalogue === undefined
        ? null
        : // Portaled to the document, so no ancestor of this tab becomes the containing block of
          // a dialog that positions against the viewport and clips it to the grid.
          createPortal(
            <ConfirmDialog
              open
              // The host defaults this to true and paints the confirm button red. This run marks
              // scenes wanted and downloads nothing, so a red button would contradict the
              // message.
              destructive={false}
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

// The dropdown names the value in force, or what the facet covers while nothing is picked.
// Choosing that first entry clears the facet.
function FacetControl({
  menu,
  selected,
  onPick,
}: Readonly<{
  menu: FacetMenuView;
  selected: string | null;
  onPick: (value: string) => void;
}>) {
  // A value in force the served list does not carry leads the options, so it can still be cleared.
  const rows = facetMenuRows(
    menu,
    selected === null
      ? null
      : (menu.values.find((value) => value.value === selected) ?? {
          value: selected,
          label: selected,
        }),
  );

  return (
    <select
      aria-label={menu.label}
      value={selected ?? ""}
      onChange={(event) => {
        // The handler toggles, so clearing means naming the value that is already in force.
        onPick(event.target.value === "" ? (selected ?? "") : event.target.value);
      }}
      className={DROPDOWN_CLASS}
    >
      <option value="">{facetCoversEverything(menu.label)}</option>
      {rows.map((row) => (
        <option key={row.value} value={row.value}>
          {row.label}
        </option>
      ))}
    </select>
  );
}
