/**
 * A read surface's four slots, chosen by the pure derivation beside it.
 *
 * A surface that can never answer renders nothing at all: an empty state on a surface with no
 * possible answer reads as a factual zero, which is a confident report the product cannot support.
 */
import type { ReactNode } from "react";
import { Spinner } from "@cove-extensions/ui-shared";

import type { AsyncRegionState } from "./asyncRegionLogic";

export function AsyncRegion({
  state,
  available = true,
  outageNotice,
  // Defaulted here rather than inside the branch, so an OMITTED slot takes the spinner while a slot
  // passed as null renders nothing. A coalesce in the branch treats the two the same, and a caller
  // asking for nothing then gets a spinner with no error anywhere.
  reading = <Spinner />,
  content,
  empty,
  failed,
}: {
  state: AsyncRegionState;
  /**
   * Whether this surface can answer at all. `false` omits it from the DOM; a caller reaching a
   * capability gap passes it rather than substituting an empty state.
   */
  available?: boolean;
  /** Rendered above kept content when a read failed behind it. */
  outageNotice?: ReactNode;
  /** Omit for the spinner; pass `null` for a surface that must draw nothing while it reads. */
  reading?: ReactNode;
  content: ReactNode;
  empty: ReactNode;
  failed: ReactNode;
}) {
  if (!available) {
    return null;
  }

  switch (state.status) {
    case "reading":
      return <>{reading}</>;
    case "content":
      return (
        <>
          {state.outage ? outageNotice : null}
          {content}
        </>
      );
    case "empty":
      return <>{empty}</>;
    case "failed":
      return <>{failed}</>;
  }
}
