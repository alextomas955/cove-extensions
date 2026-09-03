/**
 * The menu the entity control opens: every monitoring choice for one entity, and nothing else.
 *
 * Presentational. Every value arrives as a prop and the item set arrives already decided, so this
 * module computes nothing about capabilities or scopes and runs with no host and no network.
 *
 * There is no status line and no count of any sort. Whisparr's own catalogue count is unstable while
 * a refresh runs, so a number here would be wrong through no fault of this product.
 */
import { useRef, type RefObject } from "react";
// From the subpath rather than the barrel, so drawing a menu does not pull the whole primitives
// module - and its host-only imports - into this slice.
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";

import { OFF_SCREEN } from "../common/ui/offScreen";
import type { MonitorMenu, MonitorMenuItem } from "./monitorMenuLogic";

/** A stable key for one item, so two secondary actions are not the same row to React. */
function keyOf(item: MonitorMenuItem): string {
  switch (item.item) {
    case "scope":
      return `scope:${item.scope}`;
    case "secondary":
      return `secondary:${item.action}`;
    default:
      return item.item;
  }
}

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

export function EntityMonitorMenu({
  menu,
  label,
  triggerRef,
  onSelect,
  onClose,
}: {
  menu: MonitorMenu;
  /** What the control this menu belongs to is called, so the menu is named too. */
  label: string;
  /** The control that opened it. */
  triggerRef: RefObject<HTMLElement | null>;
  onSelect: (item: MonitorMenuItem) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);

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

  return (
    <div
      ref={ref}
      role="menu"
      aria-label={label}
      className="absolute right-0 z-50 mt-2 w-72 overflow-hidden rounded-lg border border-border bg-surface py-1 text-left shadow-xl"
    >
      {menu.items.map((item) => (
        // Every row carries a menu role. The overlay's roving focus selects on `[role^="menuitem"]`,
        // so a row without one is invisible to the arrow keys.
        <MenuRow
          key={keyOf(item)}
          role={item.item === "scope" ? "menuitemradio" : "menuitem"}
          checked={item.item === "scope" ? item.selected : undefined}
          label={item.label}
          sentences={item.sentences}
          reason={item.reason}
          onSelect={() => {
            onSelect(item);
          }}
        />
      ))}
    </div>
  );
}
