/**
 * The hand-rolled overlay foundation shared by the extensions' popovers and dialogs: a focus +
 * keyboard + outside-click hook, and an imperative mounter for overlays opened from a bulk-action
 * handler that owns no React tree.
 *
 * Not a native `<dialog>`. `showModal()` makes the rest of the host page inert and draws the dialog
 * above the host's own portalled overlays, such as its toasts. It also turns Escape into a browser
 * close request, which a page cannot always block, so `enabled: false` would not reliably hold a
 * dialog open.
 */
import { useEffect, useLayoutEffect, useRef } from "react";
import type { ReactElement, RefObject } from "react";
import { createRoot } from "react-dom/client";

// The tab-order set for the dialog focus trap.
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';

const DEFAULT_MENU_ITEM_SELECTOR = '[role^="menuitem"]';

export interface OverlayKeysOptions {
  onClose: () => void;
  /**
   * "menu": ArrowUp/Down roving focus over `itemSelector`, capture-phase Escape that stops
   * propagation, so no other handler on the page sees the key.
   * "dialog": Tab focus-trap over the focusable set, bubble-phase Escape marked with
   * `preventDefault`. The mark does not contain the key: it still reaches every other document and
   * window handler, and only one that reads `defaultPrevented` leaves it alone. Cove's bulk-edit
   * dialog reads it; its entity edit modal and its video queue do not, so a dialog opened over
   * either of those closes both on one Escape.
   */
  nav: "menu" | "dialog";
  /** menu mode; default `[role^="menuitem"]` - the prefix form also catches menuitemcheckbox/radio. */
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
 * `enabled` gates only the cancels, Escape and outside-click. The Tab trap keeps running while
 * suspended, and toggling it re-runs neither focus-first nor the opener captured for restore.
 */
export function useOverlayKeys(
  ref: RefObject<HTMLElement | null>,
  options: OverlayKeysOptions,
): void {
  const {
    nav,
    itemSelector = DEFAULT_MENU_ITEM_SELECTOR,
    closeOnOutsideClick = true,
    restoreFocus = nav === "dialog",
  } = options;

  // Every per-render option is read through this ref, so the listener effect never re-attaches on
  // their identity and focus-first stays a mount-once run.
  //
  // The write must be a layout effect. A passive effect runs after the browser may already have
  // painted and delivered input, so a key event arriving in that gap would read the previous
  // render's values - for `enabled`, a cancel suppressed by an operation that has already finished.
  // `enabled` is read from the ref and kept out of the deps below for the same reason:
  // re-subscribing the listener to pick up a new value reintroduces the gap.
  const optsRef = useRef(options);
  useLayoutEffect(() => {
    optsRef.current = options;
  });

  // Runs once per open: re-running on an option change would steal focus back mid-interaction.
  useLayoutEffect(() => {
    const opener = restoreFocus ? (document.activeElement as HTMLElement | null) : null;
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
      return Array.from(ref.current?.querySelectorAll<HTMLElement>(itemSelector) ?? []);
    }

    function closeOnEscape(e: KeyboardEvent) {
      if (optsRef.current.enabled === false) return;
      if (nav === "menu") e.stopPropagation();
      else {
        // A control inside the dialog may have taken this Escape already, as a combobox does to put
        // its suggestion list away. The same mark this sets for the host page is read from below,
        // so one Escape is one action. Menu mode listens in the capture phase, where nothing inside
        // has run yet and the mark is always clear.
        if (e.defaultPrevented) return;
        e.preventDefault();
      }
      optsRef.current.onClose();
    }

    function moveMenuFocus(e: KeyboardEvent) {
      if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
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

    function trapTab(e: KeyboardEvent) {
      const panel = ref.current;
      if (!panel) return;
      const items = Array.from(panel.querySelectorAll<HTMLElement>(FOCUSABLE));
      const firstEl = items.at(0);
      const lastEl = items.at(-1);
      if (!firstEl || !lastEl) return;
      const active = document.activeElement as HTMLElement | null;
      if (e.shiftKey && active === firstEl) {
        e.preventDefault();
        lastEl.focus();
      } else if (!e.shiftKey && active === lastEl) {
        e.preventDefault();
        firstEl.focus();
      }
    }

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") closeOnEscape(e);
      else if (nav === "menu") moveMenuFocus(e);
      else if (e.key === "Tab") trapTab(e);
    }

    function onPointerDown(e: PointerEvent) {
      if (optsRef.current.enabled === false) return;
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
  }, [ref, nav, itemSelector, closeOnOutsideClick]);
}

/**
 * Imperatively mount an overlay outside any React tree, for a bulk-action handler that owns no tree
 * of its own. `finish` is single-shot: it unmounts the root, removes the container and resolves the
 * promise. `null` is the cancel value.
 *
 * @public
 */
export function presentOverlay<T>(
  render: (finish: (result: T | null) => void) => ReactElement,
): Promise<T | null> {
  return new Promise<T | null>((resolve) => {
    const container = document.createElement("div");
    document.body.appendChild(container);
    const root = createRoot(container);

    let settled = false;
    function finish(result: T | null) {
      if (settled) return;
      settled = true;
      root.unmount();
      container.remove();
      resolve(result);
    }

    root.render(render(finish));
  });
}
