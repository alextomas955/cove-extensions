/**
 * The menu the entity control opens: every monitoring choice for one entity, and nothing else.
 *
 * Presentational. Every value arrives as a prop and the item set arrives already decided, so this
 * module computes nothing about capabilities or scopes and runs with no host and no network.
 *
 * There is no status line and no count of any sort. Whisparr's own catalogue count is unstable while
 * a refresh runs, so a number here would be wrong through no fault of this product.
 */
import { Fragment, useRef, type RefObject } from "react";
import { createPortal } from "react-dom";
// From the subpath rather than the barrel, so drawing a menu does not pull the whole primitives
// module - and its host-only imports - into this slice.
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import { OFF_SCREEN } from "../common/ui/offScreen";
import { monitorMenuItemKey } from "./monitorMenuLogic";
import type { MonitorMenu, MonitorMenuItem } from "./monitorMenuLogic";
import { useAnchoredTo } from "./useAnchoredTo";

/**
 * One row of the menu.
 *
 * A reason disables and an absent reason enables, so a dimmed row with nothing to hear cannot be
 * expressed. The row is a plain button rather than the shared `DisabledControl`, which wraps a
 * primitive that takes neither a role nor a class: without a role the overlay's roving focus finds
 * nothing, and the primitive's own classes draw a pill rather than a menu row.
 */
function MenuRow({
  role,
  checked,
  label,
  sentences,
  reason,
  onSelect,
}: {
  role: "menuitem" | "menuitemradio";
  /** The radio's own state. Omitted for a row that is not one of a pair. */
  checked?: boolean;
  label: string;
  sentences: readonly string[];
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
        // The row's own name leads and the reason follows it, which is the order the name is read
        // in; the same string is the hover text, so a pointer and a screen reader are told the same
        // thing.
        title={reason === null ? label : `${label}, ${reason}`}
        onClick={onSelect}
        className="flex w-full items-center gap-2 text-left text-sm text-foreground disabled:cursor-not-allowed"
      >
        <span
          className={
            role === "menuitemradio"
              ? checked === true
                ? "h-3.5 w-3.5 shrink-0 rounded-full border border-accent bg-accent"
                : "h-3.5 w-3.5 shrink-0 rounded-full border border-border"
              : "h-3.5 w-3.5 shrink-0"
          }
        />
        {label}
        {reason === null ? null : <span style={OFF_SCREEN}>{reason}</span>}
      </button>
      {sentences.map((sentence) => (
        // Outside the button on purpose: text inside it would join the accessible name, and the
        // name a control announces has to be its own name and its reason, not a paragraph.
        <p key={sentence} className="mt-1 text-xs text-secondary">
          {sentence}
        </p>
      ))}
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
  /** The control that opened it. */
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
    // The trigger does not count as outside. Without it a press on the trigger closes the menu here
    // and the trigger's own handler opens it again in the same gesture, so the menu never appears
    // to close.
    excludeRefs: [triggerRef],
    // Off by default in menu mode. The trigger carries a mark and no word, so its accessible name is
    // the only name the control has; letting focus fall to the document would leave a reader with
    // nothing to say where they are.
    restoreFocus: true,
  });

  return createPortal(
    // One positioned container holding the panel and the notice, so flow layout stacks them and
    // neither has to win a z-index contest with the other. The overlay ref is on the container, so
    // a press on the notice does not count as outside and close the menu the notice reports on.
    <div ref={ref} style={placement.at} className="fixed z-50 w-72">
      <div
        role="menu"
        aria-label={label}
        // Bounded to the room below the trigger and scrolling inside that bound, so every row is
        // reachable with a pointer at any trigger position. Deliberately no flip above the trigger:
        // that would add a second placement mode and a measurement loop to buy the same
        // reachability. The bound is inline because the host's Tailwind JIT never scans this bundle,
        // so no arbitrary-value height class would render.
        style={{ maxHeight: placement.availableHeight ?? undefined }}
        className="overflow-y-auto overflow-x-hidden rounded-lg border border-border bg-surface py-1 text-left shadow-xl"
      >
        {menu.items.map((item) => (
          <Fragment key={monitorMenuItemKey(item)}>
            {/* Every row carries a menu role. The overlay's roving focus selects on
                `[role^="menuitem"]`, so a row without one is invisible to the arrow keys. */}
            <MenuRow
              role={item.item === "scope" ? "menuitemradio" : "menuitem"}
              checked={item.item === "scope" ? item.selected : undefined}
              label={item.label}
              sentences={item.sentences}
              reason={item.reason}
              onSelect={() => {
                onSelect(item);
              }}
            />
            {/* Outside every row, because it is true of the pair rather than of one option, and
                carries no role so the arrow keys pass over it. */}
            {menu.scopeNote !== null && monitorMenuItemKey(item) === lastScopeKey(menu) ? (
              <p className="px-3 py-2 text-xs text-secondary">{menu.scopeNote}</p>
            ) : null}
          </Fragment>
        ))}
      </div>

      {/* A sibling of the menu element and never a child of it: a `menu` role admits `menuitem`,
          `group` and `separator` children only, and a screen reader may drop a status paragraph
          placed inside one. */}
      {notice === null || notice === undefined ? null : (
        <p role="status" className={`mt-1 ${NOTICE_SURFACE_CLASS}`}>
          {notice}
        </p>
      )}
    </div>,
    document.body,
  );
}

/** The key of the last scope row, which is the row the pair's own sentence follows. */
function lastScopeKey(menu: MonitorMenu): string | null {
  const scopes = menu.items.filter((item) => item.item === "scope");
  const last = scopes.at(-1);
  return last === undefined ? null : monitorMenuItemKey(last);
}
