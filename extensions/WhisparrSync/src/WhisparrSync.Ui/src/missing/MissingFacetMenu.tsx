/**
 * The panel a toolbar menu opens: the ordering menu and every facet menu draw through this one.
 *
 * Presentational. The rows arrive already decided, so nothing here reads a provider or decides
 * which values exist.
 */
import { useEffect, useRef, useState, type CSSProperties, type RefObject } from "react";
import { createPortal } from "react-dom";
// From the subpath, so drawing a menu does not pull the whole primitives module into this slice.
import { useOverlayKeys } from "@cove-extensions/ui-shared/overlay";
import { TextInput } from "@cove-extensions/ui-shared";

/** One row the panel draws. */
export interface MissingMenuRow {
  readonly value: string;
  readonly label: string;
  readonly selected: boolean;
}

/** The field at the head of a menu the provider fills as the reader types. */
export interface MissingMenuTypeAhead {
  readonly text: string;
  readonly placeholder: string;
  readonly onText: (text: string) => void;
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
  typeAhead,
  onPick,
  onClose,
}: {
  /** What the menu is called, which is the name it announces. */
  label: string;
  rows: readonly MissingMenuRow[];
  /** The control that opened it. */
  triggerRef: RefObject<HTMLElement | null>;
  /** The field at the panel's head, or null for a menu drawn from a fixed list. */
  typeAhead?: MissingMenuTypeAhead | null;
  onPick: (value: string) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const fieldRef = useRef<HTMLInputElement>(null);
  const placement = useAnchoredTo(triggerRef);
  const hasField = typeAhead !== null && typeAhead !== undefined;

  useOverlayKeys(ref, {
    onClose,
    nav: "menu",
    // The trigger does not count as outside. Without it a press on the trigger closes the menu here
    // and the trigger's own handler opens it again in the same gesture.
    excludeRefs: [triggerRef],
    restoreFocus: true,
  });

  // A type-ahead menu opens with no rows, so the overlay's roving focus has nothing to land on and
  // the field is where the reader has to start.
  useEffect(() => {
    if (!hasField) return;
    fieldRef.current?.focus();
  }, [hasField]);

  return createPortal(
    <div
      ref={ref}
      style={{ ...placement.at, maxHeight: placement.availableHeight ?? undefined }}
      className="fixed z-50 flex w-64 flex-col"
    >
      <div
        role="menu"
        aria-label={label}
        // `min-h-0` is what lets the panel shrink below its own content, so every row is reachable
        // with a pointer at any trigger position.
        className="min-h-0 overflow-y-auto overflow-x-hidden rounded-lg border border-border bg-surface py-1 text-left shadow-xl"
      >
        {typeAhead === null || typeAhead === undefined ? null : (
          // Outside every row and carrying no menu role, so the arrow keys pass over it.
          <div className="px-2 py-1">
            <TextInput
              value={typeAhead.text}
              onChange={typeAhead.onText}
              placeholder={typeAhead.placeholder}
              inputRef={fieldRef}
            />
          </div>
        )}
        {rows.map((row) => (
          // The overlay's roving focus selects on `[role^="menuitem"]`, so a row without one is
          // invisible to the arrow keys.
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
