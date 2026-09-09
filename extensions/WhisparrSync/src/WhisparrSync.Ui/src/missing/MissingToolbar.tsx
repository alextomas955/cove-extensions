/**
 * The controls above the grid: search, ordering, the facet menus and Refresh.
 *
 * Every control writes through the tab's own URL hook, so a change reaches the tab shell that
 * refetches and the address a reader copies says what they were looking at. This file parses no
 * query string of its own.
 *
 * The catalogue prop is absent until a page has answered. Before that there is no ordering and no
 * facet to offer, so the toolbar draws the two controls that need neither.
 */
import { useCallback, useEffect, useRef, useState, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Chip, TextInput } from "@cove-extensions/ui-shared";

import { ACTION_REFRESH, facetMenuBound, monitorAllConfirmation } from "../common/ui/copy";
import { OFF_SCREEN } from "../common/ui/offScreen";
import type { MissingFacetMenu as FacetMenuView, MissingPageView } from "../wire/api";
import type { MissingEntityKind } from "./entityKindLogic";
import { ConfirmDialog } from "./hostComponents";
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

/** The class every toolbar control carries, so all of them take the same focus ring. */
const CONTROL_CLASS =
  "rounded border border-border px-2.5 py-1.5 text-sm text-foreground hover:text-accent focus:outline-none focus:ring-2 focus:ring-accent";

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

  return (
    <div className="mb-3 flex flex-wrap items-center gap-2">
      <div className="w-56">
        <TextInput value={text} onChange={setText} placeholder={SEARCH_PLACEHOLDER} />
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

      <button type="button" onClick={onRefresh} className={CONTROL_CLASS}>
        {ACTION_REFRESH}
      </button>

      {catalogue === undefined || !monitorAllOffered(catalogue.kind) ? null : (
        <button
          type="button"
          onClick={() => {
            setConfirming(true);
          }}
          className={CONTROL_CLASS}
        >
          {MONITOR_ALL_LABEL}
        </button>
      )}

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
 * A trigger and the menu it opens.
 *
 * The trigger's own name leads and the value in force follows it, so a reader hears which menu they
 * are on before they hear what it is set to.
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
    <>
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={(event) => {
          onOpen(name, event.currentTarget);
        }}
        className={CONTROL_CLASS}
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
    </>
  );
}

/**
 * One facet menu and the value in force for it.
 *
 * The value renders through the shared chip, whose selected colour set is mutually exclusive with
 * its unselected one: appending accent utilities to the unselected string loses every colour
 * conflict against the host stylesheet and draws the selection invisibly.
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
    <>
      <MenuControl
        name={menu.key}
        label={menu.label}
        trigger={menu.label}
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
      {selected === null ? null : (
        <Chip
          selected
          title={menu.label}
          onClick={() => {
            onPick(selected);
          }}
        >
          {inForce?.label ?? selected}
        </Chip>
      )}
    </>
  );
}
