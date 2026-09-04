/**
 * The hand-rolled overlay foundation shared by the extensions' popovers and dialogs: a focus +
 * keyboard + outside-click hook, and an imperative mounter for overlays opened from a bulk-action
 * handler that owns no React tree.
 *
 * Deliberately hand-rolled (not Radix, not a native `<dialog>` `showModal()`): the two nav modes
 * below keep semantics that differ for real reasons, and a library would either flatten them or pull
 * in a second focus manager. See each mode's inline note.
 */
import { useEffect, useLayoutEffect, useRef } from "react";
import type { ReactElement, RefObject } from "react";
import { createRoot } from "react-dom/client";

// The tab-order set for the dialog focus trap: the same query the Renamer Dialog used.
const FOCUSABLE =
  'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';

const DEFAULT_MENU_ITEM_SELECTOR = '[role^="menuitem"]';

/**
 * The rows under `root` matching `selector` that can take focus.
 *
 * The exclusion belongs to the focus logic rather than to the selector, so a caller supplying its
 * own `itemSelector` gets it too. A disabled button matches a role selector and ignores `focus()`,
 * so `document.activeElement` never changes, `indexOf` reads the same index back on the next press,
 * and the roving focus is pinned on that row with no error.
 */
function focusableMenuItems(root: HTMLElement | null, selector: string): HTMLElement[] {
  return Array.from(root?.querySelectorAll<HTMLElement>(selector) ?? []).filter(
    (item) => !item.hasAttribute("disabled") && item.getAttribute("aria-disabled") !== "true",
  );
}

export interface OverlayKeysOptions {
  onClose: () => void;
  /**
   * "menu": ArrowUp/Down roving focus over `itemSelector`, capture-phase Escape that stops
   * propagation so the menu beats host key handlers.
   * "dialog": Tab focus-trap over the focusable set, bubble-phase Escape that prevents default so
   * the close does not leak to the host page.
   */
  nav: "menu" | "dialog";
  /**
   * menu mode; default `[role^="menuitem"]` — the prefix form also catches menuitemcheckbox/radio.
   * A matching row that is disabled is skipped by the roving focus.
   */
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
    restoreFocus = nav === "dialog",
  } = options;

  // Every per-render option is read through this ref, so the listener effect never re-attaches on
  // their identity and focus-first stays a mount-once run.
  //
  // The write MUST be a layout effect. A passive effect runs after the browser may already have
  // painted and delivered input, so a key event arriving in that gap would read the previous
  // render's values — which for `enabled` means a cancel suppressed by an operation that has
  // already finished. `enabled` is likewise read from the ref and kept out of the deps below:
  // re-subscribing the listener to pick up a new value reintroduces the same gap.
  const optsRef = useRef(options);
  useLayoutEffect(() => {
    optsRef.current = options;
  });

  // Focus the first target on open; on dialog cleanup, restore focus to whoever opened it. Runs once
  // per open (empty deps): re-running on an option change would steal focus back mid-interaction.
  useLayoutEffect(() => {
    const opener = restoreFocus ? (document.activeElement as HTMLElement | null) : null;
    if (nav === "menu") {
      focusableMenuItems(ref.current, itemSelector).at(0)?.focus();
    } else {
      ref.current?.querySelector<HTMLElement>(FOCUSABLE)?.focus();
    }
    return () => {
      opener?.focus();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const capture = nav === "menu";

    function menuItems(): HTMLElement[] {
      return focusableMenuItems(ref.current, itemSelector);
    }

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") {
        if (optsRef.current.enabled === false) return;
        if (nav === "menu") e.stopPropagation();
        else e.preventDefault();
        optsRef.current.onClose();
        return;
      }
      if (nav === "menu") {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
          e.preventDefault();
          const list = menuItems();
          // Empty is every row disabled, which is what an action in flight leaves. Returning leaves
          // the focus where it is, because there is nothing to move it to.
          if (list.length === 0) return;
          const down = e.key === "ArrowDown";
          const current = list.indexOf(document.activeElement as HTMLElement);
          // -1 is focus on nothing in the list, which is the state a settled press leaves: the
          // pressed row is disabled while its action runs, the browser moves focus off it, and the
          // rows re-enable. An arrow press from there enters the list at the end it points at, in
          // both directions; a wrap from inside the list is the modulus below.
          const next =
            current < 0
              ? down
                ? 0
                : list.length - 1
              : (current + (down ? 1 : list.length - 1)) % list.length;
          list[next]?.focus();
        }
        return;
      }
      // dialog: Tab is trapped within the panel (wrap first↔last over the focusable set).
      if (e.key !== "Tab") return;
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
 * Imperatively mount an overlay outside any React tree — for bulk-action handlers, which own no tree
 * of their own. Renders `render(finish)` into a body-attached root; `finish` is single-shot (a
 * settled guard), unmounts the root, removes the container, and resolves the promise. `null` is the
 * cancel value.
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
