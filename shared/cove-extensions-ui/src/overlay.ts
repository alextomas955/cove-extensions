/**
 * The hand-rolled overlay foundation shared by the extensions' popovers and dialogs: a focus +
 * keyboard + outside-click hook.
 *
 * Deliberately hand-rolled (not Radix, not a native `<dialog>` `showModal()`): the two nav modes
 * below keep semantics that differ for real reasons, and a library would either flatten them or pull
 * in a second focus manager. See each mode's inline note.
 */
import { useEffect, useLayoutEffect, useRef } from "react";
import type { RefObject } from "react";

// The tab-order set for the dialog focus trap: the same query the Renamer Dialog used.
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';

const DEFAULT_MENU_ITEM_SELECTOR = '[role^="menuitem"]';

export interface OverlayKeysOptions {
  onClose: () => void;
  /**
   * "menu": ArrowUp/Down roving focus over `itemSelector`, capture-phase Escape that stops
   * propagation so the menu beats host key handlers.
   * "dialog": Tab focus-trap over the focusable set, bubble-phase Escape that prevents default so
   * the close does not leak to the host page.
   */
  nav: "menu" | "dialog";
  /** menu mode; default `[role^="menuitem"]` — the prefix form also catches menuitemcheckbox/radio. */
  itemSelector?: string;
  /** default true → a capture-phase document pointerdown outside the ref closes the overlay. */
  closeOnOutsideClick?: boolean;
  /** Containers that do NOT count as "outside" (e.g. a menu's own trigger button). */
  excludeRefs?: ReadonlyArray<RefObject<HTMLElement | null>>;
  /** false suspends Escape/outside-click without unwiring the trap (the dialog's `pending`). default true. */
  enabled?: boolean;
  /** dialog mode: restore focus to the opener on unmount. default `nav === "dialog"`. */
  restoreFocus?: boolean;
}

/**
 * Focus-first-on-open + Escape + (arrow roving | Tab trap) + outside-click for a mounted overlay.
 *
 * @remarks
 * The capture-vs-bubble and stopPropagation-vs-preventDefault Escape differences are intentional per
 * mode (a menu must win over host handlers; a dialog must not leak its cancel to the host page).
 * `enabled` gates only the cancels (Escape + outside-click) — the Tab trap keeps running while
 * suspended — and toggling it never re-runs focus-first nor drops the opener captured for restore.
 */
export function useOverlayKeys(
  ref: RefObject<HTMLElement | null>,
  options: OverlayKeysOptions,
): void {
  const {
    nav,
    itemSelector = DEFAULT_MENU_ITEM_SELECTOR,
    closeOnOutsideClick = true,
    enabled = true,
    restoreFocus = nav === "dialog",
  } = options;

  // onClose / excludeRefs read through a ref so the listener effect never re-attaches on their
  // per-render identity, and focus-first stays a mount-once run. The write lands in a post-render
  // effect: every read happens inside a DOM event handler, which cannot fire before commit.
  const optsRef = useRef(options);
  useEffect(() => {
    optsRef.current = options;
  });

  // Focus the first target on open; on dialog cleanup, restore focus to whoever opened it. Runs once
  // per open (empty deps): re-running on an option change would steal focus back mid-interaction.
  useLayoutEffect(() => {
    const opener = restoreFocus
      ? (document.activeElement as HTMLElement | null)
      : null;
    const sel = nav === "menu" ? itemSelector : FOCUSABLE;
    ref.current?.querySelector<HTMLElement>(sel)?.focus();
    return () => {
      opener?.focus();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const capture = nav === "menu";

    function menuItems(): HTMLElement[] {
      return Array.from(
        ref.current?.querySelectorAll<HTMLElement>(itemSelector) ?? [],
      );
    }

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") {
        if (!enabled) return;
        if (nav === "menu") e.stopPropagation();
        else e.preventDefault();
        optsRef.current.onClose();
        return;
      }
      if (nav === "menu") {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
          const list = menuItems();
          if (list.length === 0) return;
          const current = list.indexOf(document.activeElement as HTMLElement);
          const next =
            e.key === "ArrowDown"
              ? (current + 1) % list.length
              : (current - 1 + list.length) % list.length;
          list[next]?.focus();
        }
        return;
      }
      // dialog: Tab is trapped within the panel (wrap first↔last over the focusable set).
      if (e.key !== "Tab") return;
      const panel = ref.current;
      if (!panel) return;
      const items = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
      if (items.length === 0) return;
      const firstEl = items[0];
      const lastEl = items[items.length - 1];
      const active = document.activeElement as HTMLElement | null;
      if (e.shiftKey && active === firstEl) {
        e.preventDefault();
        lastEl.focus();
      } else if (!e.shiftKey && active === lastEl) {
        e.preventDefault();
        firstEl.focus();
      }
    }

    function onPointerDown(e: PointerEvent) {
      if (!enabled) return;
      const target = e.target as Node;
      if (ref.current?.contains(target)) return;
      for (const r of optsRef.current.excludeRefs ?? []) {
        if (r.current?.contains(target)) return;
      }
      optsRef.current.onClose();
    }

    document.addEventListener("keydown", onKeyDown, capture);
    if (closeOnOutsideClick) {
      document.addEventListener("pointerdown", onPointerDown, capture);
    }
    return () => {
      document.removeEventListener("keydown", onKeyDown, capture);
      document.removeEventListener("pointerdown", onPointerDown, capture);
    };
  }, [ref, nav, itemSelector, closeOnOutsideClick, enabled]);
}
