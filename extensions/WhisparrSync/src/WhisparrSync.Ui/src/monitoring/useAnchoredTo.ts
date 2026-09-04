/**
 * Where to put an overlay, given the control it belongs to, and how much room there is for it.
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
 * @returns The style placing the overlay beside that control, and the room below it in pixels.
 */
import { useEffect, useState, type CSSProperties, type RefObject } from "react";

/** The host's own gap between a control and the panel it opens. */
const OFFSET = 4;

/** The host's own margin between a panel and the viewport edge. */
const GUTTER = 8;

export interface AnchoredPlacement {
  /** The style placing the overlay beside its control. */
  readonly at: CSSProperties;
  /**
   * The room between the overlay's own top and the viewport's bottom gutter, or null until the
   * control has been measured. This is the room for the whole overlay, so an overlay with more than
   * one part bounds the container and not one part; null means no bound is known yet, and a bound
   * guessed before the measurement would be a wrong one.
   */
  readonly availableHeight: number | null;
}

export function useAnchoredTo(triggerRef: RefObject<HTMLElement | null>): AnchoredPlacement {
  const [placement, setPlacement] = useState<AnchoredPlacement>({
    at: { top: 0, right: 0 },
    availableHeight: null,
  });

  useEffect(() => {
    const place = () => {
      const anchor = triggerRef.current;
      if (anchor === null) return;
      const rect = anchor.getBoundingClientRect();
      const top = rect.bottom + OFFSET;
      setPlacement({
        at: { top, right: Math.max(GUTTER, window.innerWidth - rect.right) },
        availableHeight: Math.max(0, window.innerHeight - top - GUTTER),
      });
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

  return placement;
}
