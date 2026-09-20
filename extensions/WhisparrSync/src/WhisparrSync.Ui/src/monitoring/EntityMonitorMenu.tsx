/**
 * The menu the entity control opens: every monitoring choice for one entity, and nothing else.
 *
 * Presentational. Every value arrives as a prop and the item set arrives already decided, so this
 * module computes nothing about capabilities or scopes and runs with no host and no network.
 *
 * No count is drawn. Whisparr's catalogue count is unstable while a refresh runs, so a number here
 * would be wrong through no fault of this product.
 */
import { useRef, type RefObject } from "react";
import { createPortal } from "react-dom";
import {
  CalendarClock,
  CircleSlash,
  FileCheck2,
  Library,
  PlusCircle,
  Radar,
  Search,
} from "lucide-react";
// From the subpath rather than the barrel, so drawing a menu does not pull the whole primitives
// module - and its host-only imports - into this slice.
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import type { RowIcon } from "../common/ui/ChoiceOverlay";
import { OFF_SCREEN } from "../common/ui/offScreen";
import { monitorMenuItemKey } from "./monitorMenuLogic";
import type { MonitorMenu, MonitorMenuItem, MonitorMenuItemKey } from "./monitorMenuLogic";
import { useAnchoredTo } from "./useAnchoredTo";

/**
 * The glyph each item draws, shared with the selection overlay so one verb keeps one glyph wherever
 * it is offered. Total by type, so an item added later fails the build rather than drawing no glyph.
 */
export const MONITOR_ITEM_ICON: Record<MonitorMenuItemKey, RowIcon> = {
  "scope:futureScenes": CalendarClock,
  "scope:allScenes": Library,
  monitor: Radar,
  unmonitor: CircleSlash,
  "secondary:addAllMissing": PlusCircle,
  "secondary:reflectOwned": FileCheck2,
  "secondary:searchAllMonitored": Search,
};

// A reason disables and an absent reason enables, so a dimmed row with nothing to hear cannot be
// expressed. A plain button rather than the shared `DisabledControl`, which wraps a primitive that
// takes neither a role nor a class: without a role the overlay's roving focus finds nothing.
function MenuRow({
  role,
  checked,
  label,
  icon: Icon,
  reason,
  onSelect,
}: {
  role: "menuitem" | "menuitemradio";
  /** The radio's own state. Omitted for a row that is not one of a pair. */
  checked?: boolean;
  label: string;
  icon: RowIcon;
  /** Why the row cannot be pressed, or null when it can. */
  reason: string | null;
  onSelect: () => void;
}) {
  const disabled = reason !== null;
  return (
    <div className={disabled ? "px-3 py-2 opacity-60" : "px-3 py-2 hover:bg-surface"}>
      <button
        type="button"
        role={role}
        aria-checked={checked}
        disabled={disabled}
        // The row's own name leads and the reason follows it. The same string is the hover text, so
        // a pointer and a screen reader are told the same thing.
        title={reason === null ? label : `${label}, ${reason}`}
        onClick={onSelect}
        className="flex w-full items-center gap-2 text-left text-sm text-foreground disabled:cursor-not-allowed"
      >
        {role === "menuitemradio" ? (
          <span
            className={
              checked === true
                ? "h-3.5 w-3.5 shrink-0 rounded-full border border-accent bg-accent"
                : "h-3.5 w-3.5 shrink-0 rounded-full border border-border"
            }
          />
        ) : null}
        <Icon className="h-4 w-4 shrink-0 text-secondary" />
        <span className="flex-1">{label}</span>
        {reason === null ? null : <span style={OFF_SCREEN}>{reason}</span>}
      </button>
    </div>
  );
}

/**
 * The surface an outcome sentence is drawn on, shared with the control's own standalone notice so
 * the two read the same whether the menu is open or closed.
 */
export const NOTICE_SURFACE_CLASS =
  "rounded-lg border border-border bg-surface px-3 py-2 text-xs text-secondary shadow-xl";

export function EntityMonitorMenu({
  menu,
  label,
  triggerRef,
  notice,
  onSelect,
  onClose,
}: {
  menu: MonitorMenu;
  /** What the control this menu belongs to is called, so the menu is named too. */
  label: string;
  triggerRef: RefObject<HTMLElement | null>;
  /**
   * What the last gesture did, stated below the menu, or null where there is nothing to say. The
   * menu stays open while an action runs, so this is where a refusal or a skip is read.
   */
  notice?: string | null;
  onSelect: (item: MonitorMenuItem) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const placement = useAnchoredTo(triggerRef);

  useOverlayKeys(ref, {
    onClose,
    nav: "menu",
    // The trigger does not count as outside. Without this, a press on it closes the menu here and
    // the trigger's own handler reopens it in the same gesture.
    excludeRefs: [triggerRef],
    // Off by default in menu mode. The trigger carries a mark and no word, so its accessible name
    // is the only name the control has and focus must not fall to the document.
    restoreFocus: true,
  });

  return createPortal(
    // One positioned container holding the panel and the notice, so flow layout stacks them and
    // neither has to win a z-index contest. The overlay ref is on the container, so a press on the
    // notice does not count as outside.
    //
    // The room below the trigger bounds this container rather than the panel inside it. The bound
    // is inline because the host's Tailwind JIT never scans this bundle, so an arbitrary-value
    // height class would not render.
    <div
      ref={ref}
      style={{ ...placement.at, maxHeight: placement.availableHeight ?? undefined }}
      className="fixed z-50 flex w-72 flex-col"
    >
      <div
        role="menu"
        aria-label={label}
        // Scrolls inside whatever room the column leaves it, so every row is reachable at any
        // trigger position. `min-h-0` is what lets it shrink below its own content.
        className="min-h-0 overflow-y-auto overflow-x-hidden rounded-lg border border-border bg-surface py-1 text-left shadow-xl"
      >
        {/* The overlay's roving focus selects on `[role^="menuitem"]`, so a row without one is
            invisible to the arrow keys. */}
        {menu.items.map((item) => (
          <MenuRow
            key={monitorMenuItemKey(item)}
            role={item.item === "scope" ? "menuitemradio" : "menuitem"}
            checked={item.item === "scope" ? item.selected : undefined}
            label={item.label}
            icon={MONITOR_ITEM_ICON[monitorMenuItemKey(item)]}
            reason={item.reason}
            onSelect={() => {
              onSelect(item);
            }}
          />
        ))}
      </div>

      {/* A sibling of the menu element, never a child: a `menu` role admits `menuitem`, `group`
          and `separator` children only, and a screen reader may drop a status paragraph inside
          one. `shrink-0` so the column takes its height out of the panel, which scrolls. */}
      {notice === null || notice === undefined ? null : (
        <p role="status" className={`mt-1 shrink-0 ${NOTICE_SURFACE_CLASS}`}>
          {notice}
        </p>
      )}
    </div>,
    document.body,
  );
}
