/**
 * Where to put an overlay, given the control it belongs to.
 *
 * Fixed and portaled to the document rather than laid out beside the control, because the host
 * clips its entity hero: the action row sits inside a container carrying `overflow-hidden`, so a
 * panel in the flow there is cut off at the hero's own edge with nothing to see and no error, and
 * `z-50` does not escape an `overflow-hidden` ancestor. The host's own overflow menu leaves that
 * container for the same reason.
 *
 * The offset and the gutter are the host's own numbers, so the panels line up the way its do.
 *
 * @param triggerRef The control the overlay belongs to.
 * @returns The style placing the overlay beside that control.
 */
import { useEffect, useState, type CSSProperties, type RefObject } from "react";

export function useAnchoredTo(triggerRef: RefObject<HTMLElement | null>): CSSProperties {
  const [at, setAt] = useState<CSSProperties>({ top: 0, right: 0 });

  useEffect(() => {
    const place = () => {
      const anchor = triggerRef.current;
      if (anchor === null) return;
      const rect = anchor.getBoundingClientRect();
      setAt({ top: rect.bottom + 4, right: Math.max(8, window.innerWidth - rect.right) });
    };

    place();
    window.addEventListener("resize", place);
    // Capture, so the overlay follows a scroll of any container between it and the document rather
    // than only of the document itself.
    window.addEventListener("scroll", place, true);
    return () => {
      window.removeEventListener("resize", place);
      window.removeEventListener("scroll", place, true);
    };
  }, [triggerRef]);

  return at;
}
