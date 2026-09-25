/**
 * Where to put an overlay, given the control it belongs to, and how much room there is for it.
 *
 * Fixed and portaled to the document rather than laid out beside the control, because the host
 * clips its entity hero: the action row sits inside a container carrying `overflow-hidden`, and
 * `z-50` does not escape one. A panel in the flow there is cut off at the hero's edge with no
 * error.
 *
 * The offset and the gutter are the host's own numbers, so the panels line up with its own.
 *
 * @param triggerRef The control the overlay belongs to.
 * @returns The style placing the overlay beside that control, and the room below it in pixels.
 */
import { useLayoutEffect, useState, type CSSProperties, type RefObject } from "react";

/** The host's own gap between a control and the panel it opens. */
const OFFSET = 4;

/** The host's own margin between a panel and the viewport edge. */
const GUTTER = 8;

// The least room an overlay is ever given, whatever the measurement says. A room near zero leaves
// a part that may not shrink taking all of it. This is enough for the longest outcome sentence and
// a row or two, which then scroll.
const MIN_ROOM = 160;

export interface AnchoredPlacement {
  /** The style placing the overlay beside its control. */
  readonly at: CSSProperties;
  /**
   * The room between the overlay's own top and the viewport's bottom gutter, or null until the
   * control has been measured. It bounds the whole overlay, so a multi-part overlay bounds its
   * container and not one part.
   *
   * Never below `MIN_ROOM`. A window too short for that gets an overlay whose lower part is below
   * the fold, which is chosen over one bounded to a height nothing can be read in.
   */
  readonly availableHeight: number | null;
}

export function useAnchoredTo(triggerRef: RefObject<HTMLElement | null>): AnchoredPlacement {
  const [placement, setPlacement] = useState<AnchoredPlacement>({
    at: { top: 0, right: 0 },
    availableHeight: null,
  });

  useLayoutEffect(() => {
    const place = () => {
      const anchor = triggerRef.current;
      if (anchor === null) return;
      const rect = anchor.getBoundingClientRect();
      const top = rect.bottom + OFFSET;
      setPlacement({
        at: { top, right: Math.max(GUTTER, window.innerWidth - rect.right) },
        availableHeight: Math.max(MIN_ROOM, window.innerHeight - top - GUTTER),
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
