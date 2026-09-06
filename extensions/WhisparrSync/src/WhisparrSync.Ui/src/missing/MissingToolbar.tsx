/**
 * The controls above the grid: search, ordering, Refresh and the whole-view action.
 *
 * Every control writes through the tab's own URL hook, so a change reaches the tab shell that
 * refetches and the address a reader copies says what they were looking at. This file parses no
 * query string of its own.
 *
 * The catalogue prop is absent until a page has answered. Before that there is no ordering to offer
 * and no facet to offer, so the toolbar draws the two controls that need neither.
 */
import {
  useCallback,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type RefObject,
} from "react";
import { createPortal } from "react-dom";
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";
import { TextInput } from "@cove-extensions/ui-shared";

import { ACTION_REFRESH } from "../common/ui/copy";
import { OFF_SCREEN } from "../common/ui/offScreen";
import type { MissingPageView } from "../wire/api";
import type { MissingEntityKind } from "./entityKindLogic";
import {
  MONITOR_ALL,
  SEARCH_PLACEHOLDER,
  SORT_MENU_LABEL,
  searchSettleDelayMs,
  sortOptionsFor,
  toolbarControlsFor,
} from "./missingToolbarLogic";
import { useMissingUrlState } from "./useMissingUrlState";

/** The class every toolbar control carries, so all of them take the same focus ring. */
const CONTROL_CLASS =
  "rounded border border-border px-2.5 py-1.5 text-sm text-foreground hover:text-accent focus:outline-none focus:ring-2 focus:ring-accent";

/** What the answered page tells the toolbar, once one has answered. */
export interface MissingToolbarCatalogue {
  readonly kind: MissingEntityKind;
  readonly view: MissingPageView;
  readonly onMonitorAll: () => void;
}

export function MissingToolbar({
  onRefresh,
  catalogue,
}: {
  onRefresh: () => void;
  catalogue?: MissingToolbarCatalogue;
}) {
  const [view, setView] = useMissingUrlState();
  const [text, setText] = useState(() => view.q);
  const [openMenu, setOpenMenu] = useState<string | null>(null);
  const openTrigger = useRef<HTMLElement | null>(null);

  // Written by replacement once typing settles, so the back button leaves the tab and does not step
  // through half-typed searches. A search that matched a page the reader is not on would leave them
  // looking at an empty page of a non-empty answer, so the position resets with it.
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
    catalogue === undefined
      ? (["search", "refresh"] as const)
      : toolbarControlsFor(catalogue.kind, catalogue.view.monitorAllIsOffered);

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

      <button type="button" onClick={onRefresh} className={CONTROL_CLASS}>
        {ACTION_REFRESH}
      </button>

      {controls.includes("monitorAll") && catalogue !== undefined ? (
        <button type="button" onClick={catalogue.onMonitorAll} className={CONTROL_CLASS}>
          {MONITOR_ALL}
        </button>
      ) : null}
    </div>
  );
}

/** One row of a toolbar menu. */
interface MenuRow {
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
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
  onOpen,
  onClose,
  onPick,
}: {
  name: string;
  label: string;
  trigger: string;
  rows: readonly MenuRow[];
  open: boolean;
  openTrigger: RefObject<HTMLElement | null>;
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
        <ToolbarMenu
          label={label}
          rows={rows}
          triggerRef={openTrigger}
          onPick={onPick}
          onClose={onClose}
        />
      ) : null}
    </>
  );
}

/** The host's own gap between a control and the panel it opens. */
const OFFSET = 4;

/** The host's own margin between a panel and the viewport edge. */
const GUTTER = 8;

/** The least room a panel is ever given, whatever the measurement says. */
const MIN_ROOM = 160;

interface AnchoredPlacement {
  readonly at: CSSProperties;
  /** The room below the trigger, or null until it has been measured. */
  readonly availableHeight: number | null;
}

/**
 * Where to put a panel, given the control it belongs to.
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

/**
 * The panel a toolbar menu draws.
 *
 * Every row carries a menu role: the overlay's roving focus selects on `[role^="menuitem"]`, so a
 * row without one is invisible to the arrow keys.
 */
function ToolbarMenu({
  label,
  rows,
  triggerRef,
  onPick,
  onClose,
}: {
  label: string;
  rows: readonly MenuRow[];
  triggerRef: RefObject<HTMLElement | null>;
  onPick: (value: string) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const placement = useAnchoredTo(triggerRef);

  useOverlayKeys(ref, {
    onClose,
    nav: "menu",
    // The trigger does not count as outside. Without it a press on the trigger closes the menu here
    // and the trigger's own handler opens it again in the same gesture.
    excludeRefs: [triggerRef],
    restoreFocus: true,
  });

  return createPortal(
    <div
      ref={ref}
      style={{ ...placement.at, maxHeight: placement.availableHeight ?? undefined }}
      className="fixed z-50 flex w-64 flex-col"
    >
      <div
        role="menu"
        aria-label={label}
        className="min-h-0 overflow-y-auto overflow-x-hidden rounded-lg border border-border bg-surface py-1 text-left shadow-xl"
      >
        {rows.map((row) => (
          <button
            key={row.value}
            type="button"
            role="menuitemcheckbox"
            aria-checked={row.selected}
            onClick={() => {
              onPick(row.value);
            }}
            className="flex w-full items-center gap-2 px-3 py-2 text-left text-sm text-foreground hover:bg-card focus:outline-none focus:ring-2 focus:ring-accent"
          >
            <span
              className={
                row.selected
                  ? "h-3.5 w-3.5 shrink-0 rounded-full border border-accent bg-accent"
                  : "h-3.5 w-3.5 shrink-0 rounded-full border border-border"
              }
            />
            {row.label}
          </button>
        ))}
      </div>
    </div>,
    document.body,
  );
}
